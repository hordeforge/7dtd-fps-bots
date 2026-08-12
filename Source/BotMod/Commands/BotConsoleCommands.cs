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
            "Usage: bot <help|list|spawn [count] [x z] [weapon] | add <n> | remove [all|<id>] | count <n> | weapon <gunId|mixed> | skill <0-4> | reload | enable | disable | status>\n" +
            "  bot spawn 4                    - spawn 4 mixed bots\n" +
            "  bot spawn 1 0 0 gunMGT1AK47    - spawn AK bot at x,z\n" +
            "  bot weapon gunShotgunT1DoubleBarrel - default for next spawns\n" +
            "  bot skill 3                  - nightmare aim/reaction\n" +
            "  bot list                     - alive bots (weapon/state/target)\n" +
            "  bot count 8                  - keep 8 alive\n" +
            "  bot reload                   - reload Config/botmod.json";

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            string sub = _params.Count > 0 ? _params[0].ToLowerInvariant() : "help";
            try
            {
                switch (sub)
                {
                    case "help": case "?": case "h": SdtdConsole.Instance.Output(GetHelp()); break;
                    case "status": DoStatus(); break;
                    case "list": case "ls": DoList(); break;
                    case "spawn": case "add": DoSpawn(_params); break;
                    case "remove": case "rm": case "kick": case "clear": DoRemove(_params); break;
                    case "count": case "set": DoCount(_params); break;
                    case "weapon": case "gun": DoWeapon(_params); break;
                    case "skill": case "difficulty": DoSkill(_params); break;
                    case "reload": ModApi.ReloadConfig(); SdtdConsole.Instance.Output("BotMod config reloaded. diff=" + ModApi.Config.Difficulty + " weapon=" + ModApi.Config.BotWeapon); break;
                    case "enable": ModApi.Config.Enabled = true; SdtdConsole.Instance.Output("BotMod enabled."); break;
                    case "disable": ModApi.Config.Enabled = false; SdtdConsole.Instance.Output("BotMod disabled. Existing bots remain until removed."); break;
                    default: SdtdConsole.Instance.Output("Unknown bot subcommand: " + sub + ". Try: bot help"); break;
                }
            }
            catch (Exception ex) { SdtdConsole.Instance.Output("bot command failed: " + ex.Message); ModApi.Log("bot cmd failed: " + ex); }
        }
        void DoStatus()
        {
            var cfg = ModApi.Config; var mgr = BotManager.Instance;
            SdtdConsole.Instance.Output($"BotMod: enabled={cfg.Enabled} target={cfg.TargetBotCount} max={cfg.MaxBots} alive={mgr.BotCount} class={cfg.BotEntityClass} weapon={cfg.BotWeapon} diff={cfg.Difficulty} vision={cfg.VisionRange} attack={cfg.AttackRange} vsBot={cfg.BotVsBot}");
            SdtdConsole.Instance.Output($"  spawn: radius={cfg.SpawnRadius} nearPlayer={cfg.SpawnNearPlayerChance} spawnpoints={cfg.UseSpawnpoints} strafe={cfg.StrafeChance} dodge={cfg.DodgeOnHitChance}");
        }
        void DoList()
        {
            var mgr = BotManager.Instance; var world = GameManager.Instance?.World;
            if (mgr.BotCount == 0) { SdtdConsole.Instance.Output("No bots alive."); return; }
            foreach (var b in mgr.Bots) SdtdConsole.Instance.Output(b.Status(world));
        }
        void DoSpawn(List<string> p)
        {
            int count = 1; Vector3? pos = null; string weapon = null;
            if (p.Count >= 2 && int.TryParse(p[1], out int c)) count = Math.Max(1, Math.Min(16, c));
            // parse trailing gun id if present (non-numeric last token that looks like a gun)
            if (p.Count >= 2)
            {
                string last = p[p.Count - 1];
                if (last.StartsWith("gun", StringComparison.OrdinalIgnoreCase) || last == "mixed") weapon = last;
            }
            // parse x z if two floats near end (before optional weapon)
            int scanEnd = weapon != null ? p.Count - 1 : p.Count;
            if (scanEnd >= 4 && float.TryParse(p[scanEnd - 2], out float x) && float.TryParse(p[scanEnd - 1], out float z))
                pos = new Vector3(x, 60f, z);
            int spawned = 0;
            for (int i = 0; i < count; i++) if (BotManager.Instance.TrySpawnOne(pos, null, weapon)) spawned++;
            SdtdConsole.Instance.Output($"Spawned {spawned}/{count} bots" + (weapon != null ? $" weapon={weapon}" : "") + "." + (spawned < count ? " (max or spawn failed)" : ""));
        }
        void DoRemove(List<string> p)
        {
            if (p.Count >= 2 && p[1].ToLowerInvariant() == "all") { int n = BotManager.Instance.RemoveAllBots("command"); SdtdConsole.Instance.Output($"Removed {n} bots."); return; }
            if (p.Count >= 2 && int.TryParse(p[1], out int id)) { bool ok = BotManager.Instance.RemoveBot(id); SdtdConsole.Instance.Output(ok ? $"Removed bot {id}." : $"No bot with id {id}. Try: bot list"); return; }
            int n2 = BotManager.Instance.RemoveAllBots("command"); SdtdConsole.Instance.Output($"Removed {n2} bots.");
        }
        void DoCount(List<string> p)
        {
            if (p.Count < 2 || !int.TryParse(p[1], out int n)) { SdtdConsole.Instance.Output("Usage: bot count <n>  (0..16)"); return; }
            n = Math.Max(0, Math.Min(ModApi.Config.MaxBots, n)); ModApi.Config.TargetBotCount = n; SdtdConsole.Instance.Output($"Target bot count set to {n}. Will converge within a few seconds.");
        }
        void DoWeapon(List<string> p)
        {
            if (p.Count < 2) { SdtdConsole.Instance.Output("Usage: bot weapon <gunId|mixed>  e.g. bot weapon gunMGT1AK47  (also: bot spawn 2 gunShotgunT1DoubleBarrel)"); return; }
            ModApi.Config.BotWeapon = p[1]; SdtdConsole.Instance.Output($"Default weapon set to {p[1]}. Next spawns use it; existing bots keep theirs.");
        }
        void DoSkill(List<string> p)
        {
            if (p.Count < 2 || !int.TryParse(p[1], out int d)) { SdtdConsole.Instance.Output($"Skill {ModApi.Config.Difficulty} (0 bot, 1 easy, 2 normal, 3 hard, 4 nightmare). Usage: bot skill <0-4>"); return; }
            d = Math.Max(0, Math.Min(4, d)); ModApi.Config.Difficulty = d; ModApi.Config.Normalize(); SdtdConsole.Instance.Output($"Skill set to {d}. Aim jitter {ModApi.Config.AimJitterDegrees:F1}deg, reaction {ModApi.Config.ReactionTimeSec:F2}s.");
        }
    }
}
