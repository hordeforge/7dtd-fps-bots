using System;
using System.Collections.Generic;
using BotMod.Core;
using UnityEngine;

namespace BotMod.Commands
{
    public class ConsoleCmdBot : ConsoleCmdAbstract
    {
        public override string[] getCommands() => new[] { "bot" };
        public override string getDescription() => "FPS bots: spawn, list, remove, config.";
        public override string getHelp() =>
            "Usage: bot <help|list|spawn [count] [x z] | add <count> | remove [all|<id>] | count <n> | reload | enable | disable | status>\n" +
            "  bot spawn 4            - spawn 4 bots near players\n" +
            "  bot spawn 1 0 0        - spawn 1 bot at x=0 z=0\n" +
            "  bot list               - list alive bots\n" +
            "  bot remove all         - remove all bots\n" +
            "  bot count 8            - set target bot count to 8\n" +
            "  bot reload             - reload Config/botmod.json\n" +
            "  bot status             - show config + counts";

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            string sub = _params.Count > 0 ? _params[0].ToLowerInvariant() : "help";
            try
            {
                switch (sub)
                {
                    case "help": case "?": case "h":
                        SdtdConsole.Instance.Output(GetHelp()); break;
                    case "status":
                        DoStatus(); break;
                    case "list": case "ls":
                        DoList(); break;
                    case "spawn": case "add":
                        DoSpawn(_params); break;
                    case "remove": case "rm": case "kick": case "clear":
                        DoRemove(_params); break;
                    case "count": case "set":
                        DoCount(_params); break;
                    case "reload":
                        ModApi.ReloadConfig(); SdtdConsole.Instance.Output("BotMod config reloaded."); break;
                    case "enable":
                        ModApi.Config.Enabled = true; SdtdConsole.Instance.Output("BotMod enabled."); break;
                    case "disable":
                        ModApi.Config.Enabled = false; SdtdConsole.Instance.Output("BotMod disabled. Existing bots remain until removed."); break;
                    default:
                        SdtdConsole.Instance.Output("Unknown bot subcommand: " + sub + ". Try: bot help");
                        break;
                }
            }
            catch (Exception ex)
            {
                SdtdConsole.Instance.Output("bot command failed: " + ex.Message);
                ModApi.Log("bot cmd failed: " + ex);
            }
        }

        void DoStatus()
        {
            var cfg = ModApi.Config;
            var mgr = BotManager.Instance;
            SdtdConsole.Instance.Output($"BotMod: enabled={cfg.Enabled} dedicatedOnly={cfg.DedicatedOnly} target={cfg.TargetBotCount} max={cfg.MaxBots} alive={mgr.BotCount} class={cfg.BotEntityClass} weapon={cfg.BotWeapon} vision={cfg.VisionRange} attack={cfg.AttackRange} fire={cfg.FireRateSec}s dmg={cfg.DamagePerShot}");
        }

        void DoList()
        {
            var mgr = BotManager.Instance;
            var world = GameManager.Instance?.World;
            if (mgr.BotCount == 0) { SdtdConsole.Instance.Output("No bots alive."); return; }
            foreach (var b in mgr.Bots)
                SdtdConsole.Instance.Output(b.Status(world));
        }

        void DoSpawn(List<string> p)
        {
            int count = 1;
            Vector3? pos = null;
            if (p.Count >= 2 && int.TryParse(p[1], out int c)) count = Math.Max(1, Math.Min(16, c));
            if (p.Count >= 4)
            {
                if (float.TryParse(p[p.Count - 2], out float x) && float.TryParse(p[p.Count - 1], out float z))
                    pos = new Vector3(x, 60f, z);
            }
            else if (p.Count == 3 && p[0] == "add" && int.TryParse(p[1], out int c2))
                count = c2;

            int spawned = 0;
            for (int i = 0; i < count; i++)
                if (BotManager.Instance.TrySpawnOne(pos)) spawned++;

            SdtdConsole.Instance.Output($"Spawned {spawned}/{count} bots." + (spawned < count ? " (max or spawn failed)" : ""));
        }

        void DoRemove(List<string> p)
        {
            if (p.Count >= 2 && p[1].ToLowerInvariant() == "all")
            {
                int n = BotManager.Instance.RemoveAllBots("command");
                SdtdConsole.Instance.Output($"Removed {n} bots.");
                return;
            }
            if (p.Count >= 2 && int.TryParse(p[1], out int id))
            {
                bool ok = BotManager.Instance.RemoveBot(id);
                SdtdConsole.Instance.Output(ok ? $"Removed bot {id}." : $"No bot with id {id}. Try: bot list");
                return;
            }
            int n2 = BotManager.Instance.RemoveAllBots("command");
            SdtdConsole.Instance.Output($"Removed {n2} bots.");
        }

        void DoCount(List<string> p)
        {
            if (p.Count < 2 || !int.TryParse(p[1], out int n))
            {
                SdtdConsole.Instance.Output("Usage: bot count <n>  (0..16)");
                return;
            }
            n = Math.Max(0, Math.Min(ModApi.Config.MaxBots, n));
            ModApi.Config.TargetBotCount = n;
            SdtdConsole.Instance.Output($"Target bot count set to {n}. Will converge within a few seconds.");
        }
    }
}
