using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BotMod.Config;
using BotMod.Core;
using BotMod.Foundation;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace BotMod
{
    public class ModApi : IModApi
    {
        public const string HarmonyId = "com.7dtd.botmod";
        static Harmony _harmony;
        static volatile BotConfig _config = new BotConfig();
        /// <summary>Live config instance. Volatile because it is not a
        /// main-thread-only value: `bot reload` swaps the whole instance on the
        /// main thread, while the auth patch (Patch_SteamAuthServer_SyntheticBypass)
        /// reads AllowSyntheticAuthBypass from the connection thread and web
        /// handlers reach the fields from their own dispatch. Without the
        /// barrier neither side is guaranteed to see the other's instance, so a
        /// reload could leave the auth path reading a pre-reload config (or a
        /// half-published one) indefinitely.</summary>
        public static BotConfig Config { get { return _config; } private set { _config = value; } }
        public static string ModPath { get; private set; } = "";
        public static bool Active { get; private set; }

        public void InitMod(Mod modInstance)
        {
            try
            {
                // Config-layer warnings route through the same WARN log line.
                BotConfig.Warn = Warn;
                AtomicTextFile.Warn = Warn;
                Web.IdempotencyLedger.Warn = Warn;
                ModPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                // Neural weight-path resolution anchors on the mod root (see
                // BotNeuralBrain.ModRoot); wired before any TryLoad call.
                BotMod.AI.BotNeuralBrain.ModRoot = ModPath;
                string configPath = BotConfig.ConfigPath();
                Config = BotConfig.Load(configPath);
                Config.Normalize();
                try { BotCharacterDB.Load(Config); }
                catch (Exception ex) { Warn("characters.json load failed, using defaults: " + ex); }
                Log($"BotMod v{BotModVersion.Number} loading. ModPath={ModPath} ConfigPath={configPath} Enabled={Config.Enabled} DedicatedOnly={Config.DedicatedOnly} AuthBypass={Config.AllowSyntheticAuthBypass}");
                // The effective config, post-Normalize: the file alone does not
                // say which values were clamped, which the difficulty preset
                // moved, or that a field was never set at all.
                Log("Config: " + Config.EffectiveSummary());

                if (!Config.Enabled)
                    Log("Disabled by config (enabled=false). Use 'bot enable' or edit Config/botmod.json then 'bot reload'.");

                Active = true;

                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log("Harmony patches applied.");

                try { ModEvents.GameStartDone.RegisterHandler(OnGameStartDone); }
                catch (Exception ex) { Error("ModEvents.GameStartDone register failed: " + ex); }

                try { ModEvents.GameUpdate.RegisterHandler(OnGameUpdate); }
                catch (Exception ex) { Error("ModEvents.GameUpdate register failed: " + ex); }

                try { ModEvents.WorldShuttingDown.RegisterHandler(OnWorldShuttingDown); }
                catch (Exception ex) { Error("WorldShuttingDown register failed: " + ex); }

                Log("BotMod init OK. Commands: bot help");
            }
            catch (Exception ex)
            {
                Error("InitMod failed: " + ex);
            }
        }

        static void OnGameStartDone(ref ModEvents.SGameStartDoneData data)
        {
            try
            {
                if (!ShouldRun()) return;
                BotManager.Instance.OnGameStartDone();
                Log("GameStartDone -> BotManager started.");
                if (Config.UseNeuralBrain)
                    LoadNeuralWeights("loaded", ", using heuristic.");
            }
            catch (Exception ex) { Error("OnGameStartDone failed: " + ex); }
        }

        static void OnGameUpdate(ref ModEvents.SGameUpdateData data)
        {
            try
            {
                if (!ShouldRun()) return;
                BotManager.Instance.Tick(Time.deltaTime);
            }
            catch (Exception ex) { Error("GameUpdate tick failed: " + ex); }
        }

        static void OnWorldShuttingDown(ref ModEvents.SWorldShuttingDownData data)
        {
            // Full exception (not ex.Message): shutdown cleanup failures need
            // the stack to be diagnosable, same as every other handler here.
            try { BotManager.Instance.OnWorldShuttingDown(); }
            catch (Exception ex) { Warn("WorldShuttingDown cleanup failed: " + ex); }
        }

        public static bool ShouldRun()
        {
            if (!Active || Config == null || !Config.Enabled) return false;
            if (!Config.DedicatedOnly) return true;
            try { return GameManager.IsDedicatedServer; }
            catch { return false; }
        }

        public static void ReloadConfig()
        {
            string configPath = BotConfig.ConfigPath();
            Config = BotConfig.Load(configPath);
            Config.Normalize();
            try { BotCharacterDB.Load(Config); }
            catch (Exception ex) { Warn("characters.json load failed, keeping previous characters: " + ex); }
            Log($"Config reloaded: ConfigPath={configPath} Enabled={Config.Enabled} TargetBotCount={Config.TargetBotCount} Weapon={Config.BotWeapon}");
            Log("Config: " + Config.EffectiveSummary());
            if (Config.UseNeuralBrain)
                LoadNeuralWeights("reloaded", ", keeping heuristic.");
        }

        /// <summary>TryLoad the configured weights file and log the outcome once
        /// (server log; console/web surfaces add their own user-facing echo).
        /// <paramref name="failTail"/> completes the "BotNeuralBrain not loaded
        /// (reason)" line with the caller's consequence ("using heuristic.").
        /// Returns true when the brain is loaded.</summary>
        public static bool LoadNeuralWeights(string verb, string failTail)
        {
            string why;
            bool ok = BotMod.AI.BotNeuralBrain.TryLoad(Config.BotNeuralWeightPath, out why);
            if (ok) Log("BotNeuralBrain: " + verb + " " + why);
            else Warn("BotNeuralBrain not loaded (" + why + ")" + failTail);
            return ok;
        }

        public static void Log(string msg)
        {
            try { global::Log.Out("[BotMod] " + msg); }
            catch { Console.WriteLine("[BotMod] " + msg); }
        }

        /// <summary>Recoverable problem: feature degraded or an operation failed
        /// but the server keeps running. Surfaces as WARN in the server log.</summary>
        public static void Warn(string msg)
        {
            try { global::Log.Warning("[BotMod] " + msg); }
            catch { Console.WriteLine("[BotMod] WARNING: " + msg); }
        }

        /// <summary>Broken functionality: init failure, tick loop failure, or a
        /// request that failed unexpectedly. Surfaces as ERR in the server log.</summary>
        public static void Error(string msg)
        {
            try { global::Log.Error("[BotMod] " + msg); }
            catch { Console.WriteLine("[BotMod] ERROR: " + msg); }
        }

        // Flood gate for hot-path failure logs (per-frame ticks, per-shot combat,
        // per-damage-event hooks). A failure that repeats every frame would flood
        // the server log (~60 lines/s otherwise), so the first occurrence logs in
        // full and repeats inside the cooldown are counted, then surfaced as
        // "(+ N suppressed)" on the next emitted line (same contract the tick
        // loop used before this became shared). One global gate: distinct failure
        // sources can suppress each other during a storm; acceptable because each
        // emitted line still names its source and storms are exactly when volume
        // must stay bounded.
        const float WarnCooldownSec = 10f;
        static float _warnGateUntil;
        static int _warnSuppressed;

        /// <summary>Rate-limited Warn for per-frame / per-shot / per-damage call
        /// sites where building the message costs real work (Exception.ToString
        /// walks the stack): the factory runs only when the gate is open, so a
        /// failure repeating every frame pays the string construction once per
        /// cooldown window instead of on every suppressed call.
        /// Main-thread only (reads UnityEngine.Time.time).</summary>
        public static void WarnRateLimited(Func<string> msgFactory)
        {
            float now = Time.time;
            if (now < _warnGateUntil) { _warnSuppressed++; return; }
            EmitRateLimitedWarn(now, msgFactory());
        }

        static void EmitRateLimitedWarn(float now, string msg)
        {
            string suppressed = _warnSuppressed > 0 ? " (+ " + _warnSuppressed + " suppressed)" : "";
            Warn(msg + suppressed);
            _warnSuppressed = 0;
            _warnGateUntil = now + WarnCooldownSec;
        }

        // Persist one config field to the host-mounted canonical copy
        // (/mods/BotMod/Config/botmod.json) and the copy the running game reads,
        // so a toggle survives container restarts. The key is the JSON property
        // name in BotConfig (e.g. "BotTeam", "BotVsBot", "Enabled"). Writes go
        // through AtomicTextFile: a crash mid-persist must not tear the JSON
        // (an unparseable config resets all persisted operator state to
        // defaults on next start) and leaves a .bak last-known-good behind.
        //
        // Web handlers run concurrently on thread pool threads, so persists are
        // serialized: without this gate two overlapping requests read-modify-
        // write the same JSON (one field update lost) and interleave
        // AtomicTextFile's fixed .tmp staging (a half-written tmp can be moved
        // onto the live file). No I/O outside the lock; callers never take
        // other locks around it, so there is no ordering hazard.
        static readonly object PersistGate = new object();

        // Candidate config locations, de-duplicated by normalized full path.
        // A dedi install places the assembly under /mods/BotMod, so the
        // hardcoded server path and ConfigPath() routinely name the same
        // file. Writing it twice is not merely wasted work:
        // AtomicTextFile stages the previous content into <path>.bak, so the
        // second pass would back up the content the first pass just wrote and
        // the last-known-good would no longer predate the live value. That is
        // exactly the recovery copy BotConfig.Load reaches for when the next
        // write is interrupted, so each distinct file must be written once.
        // With BOTMOD_CONFIG set, the operator named one file as the config:
        // the /mods mount is a different path in that deployment, and writing
        // the same toggle into both would leave the authoritative one behind.
        static List<string> ConfigWritePaths()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>(2);
            string[] candidates = BotConfig.ConfigPathOverride() != null
                ? new[] { BotConfig.ConfigPath() }
                : new[] { "/mods/BotMod/Config/botmod.json", BotConfig.ConfigPath() };
            foreach (string candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                // GetFullPath is what makes "./Config/botmod.json" and
                // "/mods/BotMod/Config/botmod.json" compare equal; the raw
                // strings do not. Exceptions here (malformed path) fall
                // through as distinct entries and the write is attempted.
                string full;
                try { full = Path.GetFullPath(candidate); }
                catch (Exception) { full = candidate; }
                if (seen.Add(full)) paths.Add(candidate);
            }
            return paths;
        }

        public static void PersistConfigField(string key, object value)
        {
            lock (PersistGate)
            {
                bool wrote = false;
                foreach (string path in ConfigWritePaths())
                {
                    try
                    {
                        if (!File.Exists(path)) continue;
                        // Explicit UTF-8: matches AtomicTextFile.Write's Encoding.UTF8.
                        var root = JObject.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
                        root[key] = JToken.FromObject(value);
                        AtomicTextFile.Write(path, root.ToString(Newtonsoft.Json.Formatting.Indented));
                        wrote = true;
                    }
                    catch (Exception ex) { Warn("bot config persist failed (" + path + "): " + ex.Message); }
                }
                // The audit line below claims the mutation survived to disk, so a
                // run with no config file present must say so instead of logging a
                // persist that never happened (the toggle would silently revert on
                // restart despite the log).
                if (!wrote) Warn("bot config persist skipped for '" + key + "': no botmod.json found (expected /mods/BotMod/Config or beside the assembly)");
                // One audit line per persisted mutation, covering both surfaces
                // (web API handlers log their own request outcome; console
                // commands only echo to the issuing telnet/console session,
                // which never reaches the server log). Keeps state changes
                // reconstructable from the log alone.
                Log("config persist " + key + "=" + value);
            }
        }
    }
}
