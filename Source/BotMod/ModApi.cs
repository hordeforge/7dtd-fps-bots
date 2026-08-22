using System;
using System.IO;
using System.Reflection;
using BotMod.Config;
using BotMod.Core;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace BotMod
{
    public class ModApi : IModApi
    {
        public const string HarmonyId = "com.7dtd.botmod";
        static Harmony _harmony;
        public static BotConfig Config { get; private set; } = new BotConfig();
        public static string ModPath { get; private set; } = "";
        public static bool Active { get; private set; }

        public void InitMod(Mod modInstance)
        {
            try
            {
                ModPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                Config = BotConfig.Load(BotConfig.DefaultPathBesideAssembly());
                Config.Normalize();
                try { BotCharacterDB.Load(Config); }
                catch (Exception ex) { Warn("characters.json load failed, using defaults: " + ex); }
                Log($"BotMod v0.4.0 loading. ModPath={ModPath} Enabled={Config.Enabled} DedicatedOnly={Config.DedicatedOnly}");

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
                catch (Exception ex) { Error("WorldShuttingDown register failed: " + ex.Message); }

                // Ensure npcSurvivor* is available on dedi even though the vanilla XML has it
                // inside an HTML comment. Without this the engine has no EntitySurvivor class
                // and BotSpawner falls back to zombieSoldier every time.
                try { BotSurvivorPatch.EnsureSurvivorClasses(); } catch (Exception ex) { Warn("EnsureSurvivorClasses: " + ex.Message); }
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
                {
                    string why;
                    bool ok = BotMod.AI.BotNeuralBrain.TryLoad(Config.BotNeuralWeightPath, out why);
                    Log("BotNeuralBrain: " + (ok ? "loaded " + why : "not loaded (" + why + "), using heuristic."));
                }
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
            try { BotManager.Instance.OnWorldShuttingDown(); }
            catch (Exception ex) { Warn("WorldShuttingDown cleanup failed: " + ex.Message); }
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
            Config = BotConfig.Load(BotConfig.DefaultPathBesideAssembly());
            Config.Normalize();
            try { BotMod.Config.BotCharacterDB.Load(Config); }
            catch (Exception ex) { Warn("characters.json load failed, keeping previous characters: " + ex); }
            Log($"Config reloaded: Enabled={Config.Enabled} TargetBotCount={Config.TargetBotCount} Weapon={Config.BotWeapon}");
            if (Config.UseNeuralBrain)
            {
                string why;
                bool ok = BotMod.AI.BotNeuralBrain.TryLoad(Config.BotNeuralWeightPath, out why);
                Log("BotNeuralBrain: " + (ok ? "reloaded " + why : "not loaded (" + why + "), using heuristic."));
            }
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

        // Persist one config field to the host-mounted canonical copy
        // (/mods/BotMod/Config/botmod.json) and the copy the running game reads,
        // so a toggle survives container restarts. The key is the JSON property
        // name in BotConfig (e.g. "BotTeam", "BotVsBot", "Enabled"). Writes go
        // through AtomicTextFile: a crash mid-persist must not tear the JSON
        // (an unparseable config resets all persisted operator state to
        // defaults on next start) and leaves a .bak last-known-good behind.
        public static void PersistConfigField(string key, object value)
        {
            foreach (string path in new[] { "/mods/BotMod/Config/botmod.json", BotConfig.DefaultPathBesideAssembly() })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var root = JObject.Parse(File.ReadAllText(path));
                    root[key] = JToken.FromObject(value);
                    AtomicTextFile.Write(path, root.ToString(Newtonsoft.Json.Formatting.Indented));
                }
                catch (Exception ex) { Warn("bot config persist failed (" + path + "): " + ex.Message); }
            }
        }
    }
}
