using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// paths as the `bot` console command. Actions: enable, disable, spawn,
    /// spawnNear, remove (alias clear), removeOne, skill, neural, team, vs,
    /// setTeam, teamCount, clearTeams.
    /// The menu entry and data are admin-only (permission level 0).
    ///
    /// Duplicate semantics: POST bodies accept an optional client-generated
    /// "requestId" idempotency key. Retries reusing a key within the ledger
    /// retention window replay the recorded response instead of executing
    /// again (a retried spawn does not spawn twice); a concurrent duplicate
    /// with the same key gets 409 REQUEST_IN_PROGRESS; failures are not
    /// cached, so a retry may run again. The one ambiguous outcome is a
    /// main-thread dispatch timeout: the queued work still runs afterwards,
    /// so that key stays claimed (retries get 409 until the entry ages out)
    /// rather than risking a second execution. Without a key, execution is
    /// unchanged and repeated calls repeat the effect.
    /// </summary>
    public sealed class Bot : AbsRestApi
    {
        public Bot() : base(null) { }

        public override void HandleRestGet(RequestContext context)
        {
            PrepareEnvelopedResult(out JsonWriter writer);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                writer.WriteRaw(Encoding.UTF8.GetBytes(RunOnMain(BuildStatus, "status")));
            }
            catch (Exception ex)
            {
                // Same contract as the POST handler below: full detail to the
                // server log, generic 500 envelope to the client. Without this
                // a timed-out or failing status build escapes HandleRestGet
                // unhandled (dispatch timeouts are expected here: RunOnMain
                // throws TimeoutException when the main thread is stuck).
                ModApi.Error("web api status failed 500 after " + sw.ElapsedMilliseconds + "ms: " + ex);
                SendEmptyResponse(context, HttpStatusCode.InternalServerError, null, "ERROR", null);
                return;
            }
            SendEnvelopedResult(context, ref writer, HttpStatusCode.OK, null, null, null);
        }

        public override void HandleRestPost(RequestContext context, IDictionary<string, object> _jsonInput, byte[] _jsonInputData)
        {
            PrepareEnvelopedResult(out JsonWriter writer);
            string action = GetString(_jsonInput, "action")?.ToLowerInvariant();
            // Optional idempotency key: one per logical request, reused across
            // retries (see class doc + IdempotencyLedger).
            string requestId = GetString(_jsonInput, "requestId");
            bool keyed = IdempotencyLedger.IsValidKey(requestId);
            // One audit line per executed/replayed/rejected mutation; GET stays
            // unlogged because the dashboard polls it continuously.
            string reqTag = keyed ? requestId : "-";
            // Log-safe copies: requestId/action are request-supplied and must
            // not carry control characters into the audit trail (see
            // LogSanitizer). The raw values still drive routing and the ledger.
            string logAction = LogSanitizer.Clean(action);
            string logTag = LogSanitizer.Clean(reqTag);
            if (keyed)
            {
                string cached;
                IdempotencyLedger.BeginResult begin = IdempotencyLedger.TryBegin(requestId, out cached);
                if (begin == IdempotencyLedger.BeginResult.Replay)
                {
                    ModApi.Log("web api action=" + logAction + " req=" + logTag + " replay (cached response resent)");
                    writer.WriteRaw(Encoding.UTF8.GetBytes(cached));
                    SendEnvelopedResult(context, ref writer, HttpStatusCode.OK, null, null, null);
                    return;
                }
                if (begin == IdempotencyLedger.BeginResult.InProgress)
                {
                    ModApi.Log("web api action=" + logAction + " req=" + logTag + " rejected REQUEST_IN_PROGRESS");
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
                        ModApi.PersistConfigField("Enabled", true);
                        respBody = RespondJson("enabled", true);
                        break;
                    case "disable":
                        ModApi.Config.Enabled = false;
                        ModApi.PersistConfigField("Enabled", false);
                        respBody = RespondJson("enabled", false);
                        break;
                    case "spawn":
                        {
                            int count = Math.Max(1, Math.Min(16, GetInt(_jsonInput, "count", 1)));
                            // Bot spawning touches Unity/world state and must run
                            // on the main thread (a direct call from the web
                            // thread pool segfaulted the server).
                            int spawned = RunOnMain(() =>
                            {
                                int n = 0;
                                for (int i = 0; i < count; i++)
                                    if (BotManager.Instance.TrySpawnOne()) n++;
                                return n;
                            }, "spawn");
                            respBody = RespondJson("spawned", spawned);
                        }
                        break;
                    case "spawnnear":
                        {
                            // {"action":"spawnNear","player":"<name|id>","count":N,"weapon":"<gunId|mixed>"}
                            // Same path as `bot player <name>`: bots spawn near the
                            // target player, out-of-sight preferred (11-42m via DM
                            // spawnpoints with a ~22m sweet spot, else a 14-30m ring).
                            string ident = GetString(_jsonInput, "player");
                            int count = Math.Max(1, Math.Min(16, GetInt(_jsonInput, "count", 1)));
                            string weapon = null;
                            {
                                string wv = GetString(_jsonInput, "weapon");
                                if (!string.IsNullOrEmpty(wv))
                                {
                                    // Same grammar as `bot player <name> [count]
                                    // [weapon]` (BotArgParser.LooksLikeWeapon): an
                                    // off-grammar id used to be dropped silently and
                                    // the bots spawned with random loadouts instead
                                    // of the requested one.
                                    if (!BotMod.Commands.BotArgParser.LooksLikeWeapon(wv)) { errorCode = "INVALID_WEAPON"; break; }
                                    weapon = wv;
                                }
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
                            }, "spawnNear");
                            respBody = RespondJson("spawned", r.spawned, "found", r.found, "player", r.name);
                        }
                        break;
                    case "remove":
                    case "clear":
                        {
                            int removed = RunOnMain(() => BotManager.Instance.RemoveAllBots("web"), "removeAll");
                            respBody = RespondJson("removed", removed);
                        }
                        break;
                    case "neural":
                        {
                            bool on = GetBool(_jsonInput, "on");
                            ModApi.Config.UseNeuralBrain = on;
                            ModApi.PersistConfigField("UseNeuralBrain", on);
                            string why = "";
                            if (on)
                            {
                                bool ok = RunOnMain(
                                    () => BotMod.AI.BotNeuralBrain.TryLoad(ModApi.Config.BotNeuralWeightPath, out why),
                                    "neuralLoad");
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
                            int entityId = GetInt(_jsonInput, "entityId", 0);
                            bool removed = RunOnMain(() => BotManager.Instance.RemoveBot(entityId), "removeOne");
                            respBody = RespondJson("removed", removed, "entityId", entityId);
                        }
                        break;
                    case "skill":
                        {
                            // {"action":"skill","level":0-4} - same as `bot skill`.
                            int level = Math.Max(0, Math.Min(4, GetInt(_jsonInput, "level", ModApi.Config.Difficulty)));
                            ModApi.Config.Difficulty = level;
                            ModApi.Config.Normalize();
                            ModApi.PersistConfigField("Difficulty", level);
                            respBody = RespondJson("difficulty", level);
                        }
                        break;
                    case "team":
                        {
                            // {"action":"team","on":bool} - squad mode: all bots are
                            // one team (never target/damage each other). Persisted.
                            bool on = GetBool(_jsonInput, "on");
                            ModApi.Config.BotTeam = on;
                            ModApi.PersistConfigField("BotTeam", on);
                            respBody = RespondJson("team", on);
                        }
                        break;
                    case "vs":
                        {
                            // {"action":"vs","target":"bot|zombie|player","on":bool} -
                            // bots shoot that target class (same as `bot vs`). Persisted.
                            string target = GetString(_jsonInput, "target")?.ToLowerInvariant() ?? "";
                            bool on = GetBool(_jsonInput, "on");
                            if (ModApi.Config.SetVsTarget(target, on, out string field))
                            {
                                ModApi.PersistConfigField(field, on);
                                respBody = RespondJson("vs", target, "on", on);
                            }
                            else errorCode = "INVALID_TARGET";
                        }
                        break;
                    case "setteam":
                        {
                            // {"action":"setTeam","name":"<botName>","team":N} - assign
                            // a bot to a team (0 = free-for-all). Keyed by base name,
                            // persists to config, applies to live bots immediately.
                            string name = GetString(_jsonInput, "name") ?? "";
                            int team = GetInt(_jsonInput, "team", 0);
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
                            int count = Math.Max(0, Math.Min(8, GetInt(_jsonInput, "count", ModApi.Config.BotTeamCount)));
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
                // A dispatch timeout is not a clean failure: MainThreadDispatch
                // leaves the enqueued world-touching work queued, so it still
                // runs later and the action may yet take effect after this 500
                // ("lost response after the server acted"). Keep that key's
                // ledger claim InProgress so a same-key retry is rejected with
                // 409 instead of executing twice; the entry ages out via
                // Retention. Any other exception means nothing ran or the work
                // threw before producing a response: release the claim so a
                // retry can resubmit.
                if (keyed && !(ex is TimeoutException)) IdempotencyLedger.Fail(requestId);
                ModApi.Error("web api action=" + logAction + " req=" + logTag + " failed 500 after " + sw.ElapsedMilliseconds + "ms: " + ex);
                // Full exception detail goes to the server log above only; the
                // webserver envelope would otherwise embed the exception type,
                // message and stack trace in the response body.
                SendEmptyResponse(context, HttpStatusCode.InternalServerError, null, "ERROR", null);
                return;
            }
            if (errorCode != null)
            {
                if (keyed) IdempotencyLedger.Fail(requestId); // client error: retry may resubmit
                ModApi.Log("web api action=" + logAction + " req=" + logTag + " rejected " + errorCode);
                SendEmptyResponse(context, HttpStatusCode.BadRequest, null, errorCode, null);
                return;
            }
            if (keyed) IdempotencyLedger.Complete(requestId, respBody);
            // The body echoes request-supplied text (spawnNear's player name /
            // ident). Json.NET escapes C0 controls but passes DEL/C1, bidi
            // controls and zero-width characters through verbatim, so the same
            // LogSanitizer contract that guards action/requestId guards it too.
            ModApi.Log("web api action=" + logAction + " req=" + logTag + " ok in " + sw.ElapsedMilliseconds + "ms " + LogSanitizer.Clean(respBody));
            writer.WriteRaw(Encoding.UTF8.GetBytes(respBody));
            SendEnvelopedResult(context, ref writer, HttpStatusCode.OK, null, null, null);
        }

        public override int[] DefaultMethodPermissionLevels() => new[] { 0, 0, 0, 0, 0 };

        /// <summary>Run a world-touching action on the game's main thread and
        /// wait for it (the web server handler runs on a thread pool thread;
        /// Unity/world state must not be touched from there). <paramref name="op"/>
        /// names the operation so a dispatch timeout is attributable in the log.
        /// The wait-handle lifecycle lives in MainThreadDispatch (unit-tested).</summary>
        static T RunOnMain<T>(Func<T> fn, string op)
        {
            if (ThreadManager.IsMainThread()) return fn();
            return MainThreadDispatch.Execute(fn,
                task => ThreadManager.AddSingleTaskMainThread("bot-web-api", task),
                TimeSpan.FromSeconds(15), op);
        }

        /// <summary>Optional string field of the POST body; null when absent.</summary>
        static string GetString(IDictionary<string, object> body, string key)
        {
            return body != null && body.TryGetValue(key, out object v) && v != null ? Convert.ToString(v) : null;
        }

        /// <summary>Boolean flag of the POST body: true only when the value reads "true"
        /// (case-insensitive); absent or any other value is false.</summary>
        static bool GetBool(IDictionary<string, object> body, string key)
        {
            string v = GetString(body, key);
            return v != null && v.ToLowerInvariant() == "true";
        }

        /// <summary>Integer field of the POST body; fallback when absent or unparseable.
        /// Invariant parse: JSON numbers are protocol tokens, not host-locale text.</summary>
        static int GetInt(IDictionary<string, object> body, string key, int fallback)
        {
            string v = GetString(body, key);
            return v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;
        }

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
                // Real players only: world.Players holds connected EntityPlayer
                // clients, and bots are NPC bodies (zombieSoldier by default),
                // so bots never appear here. A spawned test client counts as a
                // player.
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
                        if (d < best) { best = d; nearName = p.EntityName ?? p.PlayerDisplayName ?? ("#" + p.entityId); }
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
                // Only what the dashboard panel reads: this is polled every
                // 5 s per admin session, so unused config echo is dead bytes.
                enabled = cfg.Enabled,
                targetBotCount = cfg.TargetBotCount,
                maxBots = cfg.MaxBots,
                alive = mgr.BotCount,
                difficulty = cfg.Difficulty,
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
                players = players,
                bots = bots
            });
        }
    }
}
