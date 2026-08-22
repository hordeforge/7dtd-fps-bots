using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using Utf8Json;
using Webserver;
using Webserver.WebAPI;
using BotMod.Config;
using BotMod.Core;
using UnityEngine;

namespace BotMod.Web
{
    /// <summary>
    /// Admin web API for the bot mod: GET /api/bot reports config + alive bots
    /// with the stock entity scoreboard stats; POST /api/bot drives the same
    /// paths as the `bot` console command (enable/disable/spawn/remove/neural).
    /// The menu entry and data are admin-only (permission level 0).
    /// </summary>
    public sealed class Bot : AbsRestApi
    {
        public Bot() : base(null) { }

        static string CanonicalConfigPath => "/mods/BotMod/Config/botmod.json"; // host-mounted, survives restarts
        static string GameConfigPath => BotConfig.DefaultPathBesideAssembly();  // the copy the running game reads

        public override void HandleRestGet(RequestContext context)
        {
            PrepareEnvelopedResult(out JsonWriter writer);
            writer.WriteRaw(Encoding.UTF8.GetBytes(RunOnMain(BuildStatus, "{}")));
            SendEnvelopedResult(context, ref writer, HttpStatusCode.OK, null, null, null);
        }

        public override void HandleRestPost(RequestContext context, IDictionary<string, object> _jsonInput, byte[] _jsonInputData)
        {
            PrepareEnvelopedResult(out JsonWriter writer);
            string action = _jsonInput != null && _jsonInput.TryGetValue("action", out object a) && a != null
                ? Convert.ToString(a).ToLowerInvariant() : null;
            try
            {
                switch (action)
                {
                    case "enable":
                        ModApi.Config.Enabled = true;
                        PersistEnabled(true);
                        Respond(writer, context, "enabled", true);
                        break;
                    case "disable":
                        ModApi.Config.Enabled = false;
                        PersistEnabled(false);
                        Respond(writer, context, "enabled", false);
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
                            }, 0);
                            Respond(writer, context, "spawned", spawned);
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
                                EntityPlayer target = BotMod.Commands.ConsoleCmdBot.FindPlayerByNameOrId(world, ident);
                                if (target == null) return new { spawned = 0, found = false, name = ident };
                                int n = 0;
                                for (int i = 0; i < count; i++)
                                {
                                    UnityEngine.Vector3 pos = BotSpawner.PickSpawnNearPlayer(world, target, ModApi.Config);
                                    if (pos == UnityEngine.Vector3.zero) pos = BotSpawner.PickSpawnNearPlayer(world, target, ModApi.Config); // retry
                                    if (BotManager.Instance.TrySpawnOne(pos, null, weapon)) n++;
                                }
                                return new { spawned = n, found = true, name = target.EntityName ?? target.PlayerDisplayName ?? ident };
                            }, new { spawned = 0, found = false, name = ident });
                            Respond(writer, context, "spawned", r.spawned, "found", r.found, "player", r.name);
                        }
                        break;
                    case "remove":
                    case "clear":
                        {
                            int removed = RunOnMain(() => BotManager.Instance.RemoveAllBots("web"), 0);
                            Respond(writer, context, "removed", removed);
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
                                    false);
                                if (!ok) ModApi.Log("BotNeuralBrain web enable: load failed (" + why + ")");
                            }
                            Respond(writer, context, "neural", on, "loaded", on ? BotMod.AI.BotNeuralBrain.Loaded : false, "reason", why);
                        }
                        break;
                    case "removeone":
                        {
                            // {"action":"removeOne","entityId":N} - remove a single bot.
                            int entityId = 0;
                            if (_jsonInput != null && _jsonInput.TryGetValue("entityId", out object id))
                                int.TryParse(Convert.ToString(id), out entityId);
                            bool removed = RunOnMain(() => BotManager.Instance.RemoveBot(entityId), false);
                            Respond(writer, context, "removed", removed, "entityId", entityId);
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
                            Respond(writer, context, "difficulty", level);
                        }
                        break;
                    default:
                        SendEmptyResponse(context, HttpStatusCode.BadRequest, null, "INVALID_ACTION", null);
                        break;
                }
            }
            catch (Exception ex)
            {
                ModApi.Log("bot web api failed: " + ex);
                SendEmptyResponse(context, HttpStatusCode.InternalServerError, null, "ERROR", ex);
            }
        }

        public override int[] DefaultMethodPermissionLevels() => new[] { 0, 0, 0, 0, 0 };

        /// <summary>Run a world-touching action on the game's main thread and
        /// wait for it (the web server handler runs on a thread pool thread;
        /// Unity/world state must not be touched from there).</summary>
        static T RunOnMain<T>(Func<T> fn, T fallback)
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
                throw new TimeoutException("main-thread dispatch timeout");
            if (error != null) throw error;
            return result;
        }

        static void PersistEnabled(bool enabled)
        {
            // Keep the canonical (host-mounted) copy and the running game copy
            // in sync so the flag survives container restarts.
            foreach (string path in new[] { CanonicalConfigPath, GameConfigPath })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    JObject root = JObject.Parse(File.ReadAllText(path));
                    root["Enabled"] = enabled;
                    File.WriteAllText(path, root.ToString(Newtonsoft.Json.Formatting.Indented));
                }
                catch (Exception ex) { ModApi.Log("bot config persist failed (" + path + "): " + ex.Message); }
            }
        }

        static void Respond(JsonWriter writer, RequestContext context, string key, object value, string key2 = null, object value2 = null, string key3 = null, object value3 = null)
        {
            var payload = new Dictionary<string, object> { [key] = value };
            if (key2 != null) payload[key2] = value2;
            if (key3 != null) payload[key3] = value3;
            writer.WriteRaw(Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(payload)));
            SendEnvelopedResult(context, ref writer, HttpStatusCode.OK, null, null, null);
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
                botHealth = cfg.BotHealth,
                useSpawnpoints = cfg.UseSpawnpoints,
                players = players,
                bots = bots
            });
        }
    }
}
