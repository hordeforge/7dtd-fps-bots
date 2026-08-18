using System;
using System.IO;
using System.Reflection;
using BotMod.Config;
using BotMod.Core;
using HarmonyLib;
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
                try { BotCharacterDB.Load(Config); } catch {}
                Log($"BotMod v0.2.0 loading. ModPath={ModPath} Enabled={Config.Enabled} DedicatedOnly={Config.DedicatedOnly}");

                if (!Config.Enabled)
                    Log("Disabled by config (enabled=false). Use 'bot enable' or edit Config/botmod.json then 'bot reload'.");

                Active = true;

                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log("Harmony patches applied.");

                try { ModEvents.GameStartDone.RegisterHandler(OnGameStartDone); }
                catch (Exception ex) { Log("ModEvents.GameStartDone register failed: " + ex.Message); }

                try { ModEvents.GameUpdate.RegisterHandler(OnGameUpdate); }
                catch (Exception ex) { Log("ModEvents.GameUpdate register failed: " + ex.Message); }

                try { ModEvents.WorldShuttingDown.RegisterHandler(OnWorldShuttingDown); }
                catch (Exception ex) { Log("WorldShuttingDown register failed: " + ex.Message); }

                Log("BotMod init OK. Commands: bot help");
            }
            catch (Exception ex)
            {
                Log("InitMod failed: " + ex);
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
            catch (Exception ex) { Log("OnGameStartDone failed: " + ex); }
        }

        static void OnGameUpdate(ref ModEvents.SGameUpdateData data)
        {
            try
            {
                if (!ShouldRun()) return;
                BotManager.Instance.Tick(Time.deltaTime);
            }
            catch (Exception ex) { Log("GameUpdate tick failed: " + ex); }
        }

        static void OnWorldShuttingDown(ref ModEvents.SWorldShuttingDownData data)
        {
            try { BotManager.Instance.OnWorldShuttingDown(); } catch { }
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
            try { BotMod.Config.BotCharacterDB.Load(Config); } catch {}
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
    }
}
