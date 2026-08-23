using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Utf8Json;
using Webserver;
using Webserver.WebAPI;
using BotMod.Core;
using UnityEngine;

namespace BotMod.Web
{
    /// <summary>
    /// Admin web API for the bot mod: GET /api/bot reports config + alive bots
    /// with the stock entity scoreboard stats; POST /api/bot drives the same
    /// paths as the `bot` console command (enable/disable/spawn/remove/neural).
    /// The menu entry and data are admin-only (permission level 0).
    ///
    /// Duplicate semantics: POST bodies accept an optional client-generated
    /// "requestId" idempotency key. Retries reusing a key within the ledger
    /// retention window replay the recorded response instead of executing
    /// again (a retried spawn does not spawn twice); a concurrent duplicate
    /// with the same key gets 409 REQUEST_IN_PROGRESS; failures are not
    /// cached, so a retry may run again. Without a key, execution is
    /// unchanged and repeated calls repeat the effect.
    /// </summary>
    public sealed class Bot : AbsRestApi
    {
        public Bot() : base(null) { }

        public override void HandleRestGet(RequestContext context)
        {
            PrepareEnvelopedResult(out JsonWriter writer);
            writer.WriteRaw(Encoding.UTF8.GetBytes(RunOnMain(BuildStatus, "{}", "status")));
            SendEnvelopedResult(context, ref writer, HttpStatusCode.OK, null, null, null);
        }

        public override void HandleRestPost(RequestContext context, IDictionary<string, object> _jsonInput, byte[] _jsonInputData)
        {
            PrepareEnvelopedResult(out JsonWriter writer);
            string action = _jsonInput != null && _jsonInput.TryGetValue("action", out object a) && a != null
                ? Convert.ToString(a).ToLowerInvariant() : null;
            // Optional idempotency key: one per logical request, reused across
            // retries (see class doc + IdempotencyLedger).
            string requestId = _jsonInput != null && _jsonInput.TryGetValue("requestId", out object rid) && rid != null
                ? Convert.ToString(rid) : null;
            bool keyed = IdempotencyLedger.IsValidKey(requestId);
            // One audit line per executed/replayed/rejected mutation; GET stays
            // unlogged because the dashboard polls it continuously.
            string reqTag = keyed ? requestId : "-";
            if (keyed)
            {
                string cached;
                IdempotencyLedger.BeginResult begin = IdempotencyLedger.TryBegin(requestId, out cached);
                if (begin == IdempotencyLedger.BeginResult.Replay)
                {
                    ModApi.Log("web api action=" + action + " req=" + reqTag + " replay (cached response resent)");
                    writer.WriteRaw(Encoding.UTF8.GetBytes(cached));
                    SendEnvelopedResult(context, ref writer, HttpStatusCode.OK, null, null, null);
                    return;
                }
                if (begin == IdempotencyLedger.BeginResult.InProgress)
                {
                    ModApi.Log("web api action=" + action + " req=" + reqTag + " rejected REQUEST_IN_PROGRESS");
                    SendEmptyResponse(context, HttpStatusCode.Conflict, null, "REQUEST_IN_PROGRESS", null);
                    return;
                }
            }
            string respBody = null;
            string errorCode = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                switch (action)
                {
                    case "enable":
                        ModApi.Config.Enabled = true;
                        PersistEnabled(true);
                        respBody = RespondJson("enabled", true);
                        break;
                    case "disable":
                        ModApi.Config.Enabled = false;
                        PersistEnabled(false);
                        respBody = RespondJson("enabled", false);
                        break;
                    case "spawn":
                        {
                            int count = 1;
                            if (_jsonInput != null && _jsonInput.TryGetValue("count", out object c))
                                int.TryParse(Convert.ToString(c), out count);
                            count = Math.Max(1, Math.Min(16, count));
                            // Bot spawning touches Unity/world state and must run
                            // on the main thread (a direct call from the web
                            // thread pool segfaulted the server).
                            int spawned = RunOnMain(() =>
                            {
                                int n = 0;
                                for (int i = 0; i < count; i++)
                                    if (BotManager.Instance.TrySpawnOne()) n++;
                                return n;
                            }, 0, "spawn");
                            respBody = RespondJson("spawned", spawned);
                        }
                        break;
                    case "spawnnear":
                        {
                            // {"action":"spawnNear","player":"<name|id>","count":N,"weapon":"<gunId|mixed>"}
                            // Same path as `bot player <name>`: bots spawn 12-30m
                            // from the target player (out-of-sight preferred).
                            string ident = _jsonInput != null && _jsonInput.TryGetValue("player", out object p)
                                ? Convert.ToString(p) : null;
                            int count = 1;
                            if (_jsonInput != null && _jsonInput.TryGetValue("count", out object c))
                                int.TryParse(Convert.ToString(c), out count);
                            count = Math.Max(1, Math.Min(16, count));
                            string weapon = null;
                            if (_jsonInput != null && _jsonInput.TryGetValue("weapon", out object w))
                            {
                                string wv = Convert.ToString(w);
                                if (!string.IsNullOrEmpty(wv) && (wv.StartsWith("gun", StringComparison.OrdinalIgnoreCase) || wv == "mixed"))
                                    weapon = wv;
                            }
                            var r = RunOnMain(() =>
                            {
                                var world = GameManager.Instance?.World;
                                if (world == null || string.IsNullOrEmpty(ident)) return new { spawned = 0, found = false, name = ident };
                                EntityPlayer target = BotManager.FindPlayerByNameOrId(world, ident);
                                if (target == null) return new { spawned = 0, found = false, name = ident };
                                int n = 0;
                                for (int i = 0; i < count; i++)
                                {
                                    UnityEngine.Vector3 pos = BotSpawner.PickSpawnNearPlayer(world, target, ModApi.Config);
                                    if (pos == UnityEngine.Vector3.zero) pos = BotSpawner.PickSpawnNearPlayer(world, target, ModApi.Config); // retry
                                    if (BotManager.Instance.TrySpawnOne(pos, null, weapon)) n++;
                                }
                                return new { spawned = n, found = true, name = target.EntityName ?? target.PlayerDisplayName ?? ident };
                            }, new { spawned = 0, found = false, name = ident }, "spawnNear");
                            respBody = RespondJson("spawned", r.spawned, "found", r.found, "player", r.name);
                        }
                        break;
                    case "remove":
                    case "clear":
                        {
                            int removed = RunOnMain(() => BotManager.Instance.RemoveAllBots("web"), 0, "removeAll");
                            respBody = RespondJson("removed", removed);
                        }
                        break;
                    case "neural":
                        {
                            bool on = _jsonInput != null && _jsonInput.TryGetValue("on", out object o)
                                && Convert.ToString(o).ToLowerInvariant() == "true";
                            ModApi.Config.UseNeuralBrain = on;
                            string why = "";
                            if (on)
                            {
                                bool ok = RunOnMain(
                                    () => BotMod.AI.BotNeuralBrain.TryLoad(ModApi.Config.BotNeuralWeightPath, out why),
                                    false, "neuralLoad");
                                // Load failure stays visible in the response body
                                // ("loaded":false,"reason":"...") and in this line.
                                if (!ok) ModApi.Warn("web api neural on: weights load failed: " + why);
                            }
                            respBody = RespondJson("neural", on, "loaded", on ? BotMod.AI.BotNeuralBrain.Loaded : false, "reason", why);
                        }
                        break;
                    case "removeone":
                        {
                            // {"action":"removeOne","entityId":N} - remove a single bot.
                            int entityId = 0;
                            if (_jsonInput != null && _jsonInput.TryGetValue("entityId", out object id))
                                int.TryParse(Convert.ToString(id), out entityId);
                            bool removed = RunOnMain(() => BotManager.Instance.RemoveBot(entityId), false, "removeOne");
                            respBody = RespondJson("removed", removed, "entityId", entityId);
                        }
                        break;
                    case "skill":
                        {
                            // {"action":"skill","level":0-4} - same as `bot skill`.
                            int level = ModApi.Config.Difficulty;
                            if (_jsonInput != null && _jsonInput.TryGetValue("level", out object lv))
                                int.TryParse(Convert.ToString(lv), out level);
                            level = Math.Max(0, Math.Min(4, level));
                            ModApi.Config.Difficulty = level;
                            ModApi.Config.Normalize();
                            respBody = RespondJson("difficulty", level);
                        }
                        break;
                    case "team":
                        {
                            // {"action":"team","on":bool} - squad mode: all bots are
                            // one team (never target/damage each other). Persisted.
                            bool on = _jsonInput != null && _jsonInput.TryGetValue("on", out object o)
                                && Convert.ToString(o).ToLowerInvariant() == "true";
                            ModApi.Config.BotTeam = on;
                            ModApi.PersistConfigField("BotTeam", on);
                            respBody = RespondJson("team", on);
                        }
                        break;
                    case "vs":
                        {
                            // {"action":"vs","target":"bot|zombie|player","on":bool} -
                            // bots shoot that target class (same as `bot vs`). Persisted.
                            string target = _jsonInput != null && _jsonInput.TryGetValue("target", out object t)
                                ? Convert.ToString(t).ToLowerInvariant() : "";
                            bool on = _jsonInput != null && _jsonInput.TryGetValue("on", out object o)
                                && Convert.ToString(o).ToLowerInvariant() == "true";
                            string field = null;
                            switch (target)
                            {
                                case "bot": case "bots": ModApi.Config.BotVsBot = on; field = "BotVsBot"; break;
                                case "zombie": case "zombies": ModApi.Config.BotVsZombie = on; field = "BotVsZombie"; break;
                                case "player": case "players": case "human": ModApi.Config.BotVsPlayer = on; field = "BotVsPlayer"; break;
                                default: errorCode = "INVALID_TARGET"; break;
                            }
                            if (errorCode == null)
                            {
                                ModApi.PersistConfigField(field, on);
                                respBody = RespondJson("vs", target, "on", on);
                            }
                        }
                        break;
                    case "setteam":
                        {
                            // {"action":"setTeam","name":"<botName>","team":N} - assign
                            // a bot to a team (0 = free-for-all). Keyed by base name,
                            // persists to config, applies to live bots immediately.
                            string name = _jsonInput != null && _jsonInput.TryGetValue("name", out object nm)
                                ? Convert.ToString(nm) : "";
                            int team = 0;
                            if (_jsonInput != null && _jsonInput.TryGetValue("team", out object tv))
                                int.TryParse(Convert.ToString(tv), out team);
                            if (string.IsNullOrEmpty(name)) { errorCode = "INVALID_NAME"; break; }
                            string baseName = BotManager.BaseName(name);
                            var cfg = ModApi.Config;
                            team = Math.Max(0, Math.Min(cfg.BotTeamCount, team));
                            // Locked helper + snapshot: this handler runs on a web
                            // thread pool thread while the game tick reads the map.
                            cfg.SetTeamAssignment(baseName, team);
                            ModApi.PersistConfigField("TeamAssignments", cfg.SnapshotTeamAssignments());
                            respBody = RespondJson("name", baseName, "team", team);
                        }
                        break;
                    case "teamcount":
                        {
                            // {"action":"teamCount","count":N} - number of team
                            // buckets (0 = free-for-all only). Persisted.
                            int count = ModApi.Config.BotTeamCount;
                            if (_jsonInput != null && _jsonInput.TryGetValue("count", out object c))
                                int.TryParse(Convert.ToString(c), out count);
                            count = Math.Max(0, Math.Min(8, count));
                            ModApi.Config.BotTeamCount = count;
                            ModApi.Config.Normalize(); // drops assignments outside the range
                            ModApi.PersistConfigField("BotTeamCount", count);
                            ModApi.PersistConfigField("TeamAssignments", ModApi.Config.SnapshotTeamAssignments());
                            respBody = RespondJson("teamCount", count);
                        }
                        break;
                    case "clearteams":
                        {
                            ModApi.Config.ClearTeamAssignments();
                            ModApi.PersistConfigField("TeamAssignments", ModApi.Config.SnapshotTeamAssignments());
                            respBody = RespondJson("cleared", true);
                        }
                        break;
                    default:
                        errorCode = "INVALID_ACTION";
                        break;
                }
            }
            catch (Exception ex)
            {
                if (keyed) IdempotencyLedger.Fail(requestId); // retryable: nothing cached
                ModApi.Error("web api action=" + action + " req=" + reqTag + " failed 500 after " + sw.ElapsedMilliseconds + "ms: " + ex);
                SendEmptyResponse(context, HttpStatusCode.InternalServerError, null, "ERROR", ex);
                return;
            }
            if (errorCode != null)
            {
                if (keyed) IdempotencyLedger.Fail(requestId); // client error: retry may resubmit
                ModApi.Log("web api action=" + action + " req=" + reqTag + " rejected " + errorCode);
                SendEmptyResponse(context, HttpStatusCode.BadRequest, null, errorCode, null);
                return;
            }
            if (keyed) IdempotencyLedger.Complete(requestId, respBody);
            ModApi.Log("web api action=" + action + " req=" + reqTag + " ok in " + sw.ElapsedMilliseconds + "ms " + respBody);
            writer.WriteRaw(Encoding.UTF8.GetBytes(respBody));
            SendEnvelopedResult(context, ref writer, HttpStatusCode.OK, null, null, null);
        }

        public override int[] DefaultMethodPermissionLevels() => new[] { 0, 0, 0, 0, 0 };

        /// <summary>Run a world-touching action on the game's main thread and
        /// wait for it (the web server handler runs on a thread pool thread;
        /// Unity/world state must not be touched from there). <paramref name="op"/>
        /// names the operation so a dispatch timeout is attributable in the log.</summary>
        static T RunOnMain<T>(Func<T> fn, T fallback, string op)
        {
            if (ThreadManager.IsMainThread()) return fn();
            T result = fallback;
            Exception error = null;
            var done = new System.Threading.ManualResetEventSlim(false);
            ThreadManager.AddSingleTaskMainThread("bot-web-api", () =>
            {
                try { result = fn(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            if (!done.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("main-thread dispatch timeout after 15s: " + op);
            if (error != null) throw error;
            return result;
        }

        static void PersistEnabled(bool enabled) => ModApi.PersistConfigField("Enabled", enabled);

        /// <summary>Serialize the success payload. The caller sends it and, for
        /// keyed requests, records it in the idempotency ledger for replays.</summary>
        static string RespondJson(string key, object value, string key2 = null, object value2 = null, string key3 = null, object value3 = null)
        {
            var payload = new Dictionary<string, object> { [key] = value };
            if (key2 != null) payload[key2] = value2;
            if (key3 != null) payload[key3] = value3;
            return Newtonsoft.Json.JsonConvert.SerializeObject(payload);
        }

        static string BuildStatus()
        {
            var cfg = ModApi.Config;
            var mgr = BotManager.Instance;
            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            var bots = new List<object>();
            var players = new List<object>();
            if (world != null)
            {
                // Real players (bots are EntityTrader bodies, so they are not in
                // world.Players; a spawned test client counts as a player here).
                var plist = world.Players != null ? world.Players.list : null;
                if (plist != null)
                {
                    foreach (var p in plist)
                    {
                        if (p == null || p.IsDead() || !p.IsSpawned()) continue;
                        players.Add(new
                        {
                            name = p.EntityName ?? p.PlayerDisplayName ?? ("#" + p.entityId),
                            entityId = p.entityId
                        });
                    }
                }
                foreach (var b in mgr.Bots)
                {
                    var ent = world.GetEntity(b.EntityId) as EntityAlive;
                    // Nearest live player + distance in metres (blocks).
                    string nearName = null;
                    float nearDist = -1f;
                    if (ent != null && plist != null)
                    {
                        float best = float.MaxValue;
                        foreach (var p in plist)
                        {
                            if (p == null || p.IsDead() || !p.IsSpawned()) continue;
                            float d = Vector3.Distance(ent.position, p.position);
                            if (d < best) { best = d; nearName = p.EntityName; }
                        }
                        if (best < float.MaxValue) nearDist = best;
                    }
                    bots.Add(new
                    {
                        name = b.Name,
                        entityId = b.EntityId,
                        team = BotManager.Instance.GetTeamId(b.EntityId),
                        weapon = b.Weapon.GunId ?? "?",
                        status = b.Status(world),
                        health = ent != null ? (int)ent.Health : 0,
                        deaths = ent != null ? ent.Died : 0,
                        zombies = ent != null ? ent.KilledZombies : 0,
                        players = ent != null ? ent.KilledPlayers : 0,
                        score = ent != null ? (int)ent.Score : 0,
                        level = ent != null && ent.Progression != null ? ent.Progression.GetLevel() : 1,
                        nearestPlayer = nearName,
                        nearestPlayerDist = (int)nearDist
                    });
                }
            }
            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                enabled = cfg.Enabled,
                dedicatedOnly = cfg.DedicatedOnly,
                targetBotCount = cfg.TargetBotCount,
                maxBots = cfg.MaxBots,
                alive = mgr.BotCount,
                difficulty = cfg.Difficulty,
                weapon = cfg.BotWeapon,
                neural = cfg.UseNeuralBrain,
                neuralLoaded = BotMod.AI.BotNeuralBrain.Loaded,
                neuralPath = BotMod.AI.BotNeuralBrain.LoadedPath,
                visionRange = cfg.VisionRange,
                attackRange = cfg.AttackRange,
                spawnRadius = cfg.SpawnRadius,
                strafeChance = cfg.StrafeChance,
                dodgeOnHitChance = cfg.DodgeOnHitChance,
                botVsBot = cfg.BotVsBot,
                botVsZombie = cfg.BotVsZombie,
                botVsPlayer = cfg.BotVsPlayer,
                botTeam = cfg.BotTeam,
                teamCount = cfg.BotTeamCount,
                botHealth = cfg.BotHealth,
                useSpawnpoints = cfg.UseSpawnpoints,
                players = players,
                bots = bots
            });
        }
    }
}
