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
            "Usage: bot <help|list|spawn [count] [x z] [weapon] | player <name/id> [count] [weapon] | add <n> | remove [all|<id>] | count <n> | weapon <gunId|mixed> | skill <0-4> | neural <on|off|reload|status> | reload | enable | disable | status>\n" +
            "  bot player <nameOrId> [count] [weapon] - spawn bots near that player (DM-safe, not too close)\n" +
            "  bot spawn 4                    - spawn 4 mixed bots\n" +
            "  bot spawn 1 0 0 gunMGT1AK47    - spawn AK bot at x,z\n" +
            "  bot weapon gunShotgunT1DoubleBarrel - default for next spawns\n" +
            "  bot skill 3                  - nightmare aim/reaction\n" +
            "  bot neural status            - neural brain loaded? path, weights\n" +
            "  bot neural reload            - re-read evolved/best.json (or BotNeuralWeightPath)\n" +
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
                    case "player": case "near": case "at": DoPlayer(_params, _senderInfo); break;
                    case "reload": ModApi.ReloadConfig(); SdtdConsole.Instance.Output("BotMod config reloaded. diff=" + ModApi.Config.Difficulty + " weapon=" + ModApi.Config.BotWeapon + " neural=" + (ModApi.Config.UseNeuralBrain ? "on" : "off") + " (" + BotMod.AI.BotNeuralBrain.LastReason + ")"); break;
                    case "enable": ModApi.Config.Enabled = true; SdtdConsole.Instance.Output("BotMod enabled."); break;
                    case "disable": ModApi.Config.Enabled = false; SdtdConsole.Instance.Output("BotMod disabled. Existing bots remain until removed."); break;
                    case "neural": DoNeural(_params); break;
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
        void DoPlayer(List<string> p, CommandSenderInfo sender)
        {
            if (p.Count < 2) { SdtdConsole.Instance.Output("Usage: bot player <nameOrId> [count] [weapon]\n  e.g. bot player Kira / bot player 171 3 gunShotgunT1DoubleBarrel"); return; }
            string ident = p[1];
            int count = 1; string weapon = null;
            // parse: bot player <ident> [count] [weapon]
            if (p.Count >= 3 && int.TryParse(p[2], out int c)) { count = Math.Max(1, Math.Min(16, c)); if (p.Count >= 4) { string last = p[p.Count - 1]; if (last.StartsWith("gun", StringComparison.OrdinalIgnoreCase) || last == "mixed") weapon = last; } }
            else if (p.Count >= 3) { string last = p[p.Count - 1]; if (last.StartsWith("gun", StringComparison.OrdinalIgnoreCase) || last == "mixed") weapon = last; }
            var world = GameManager.Instance?.World;
            if (world == null) { SdtdConsole.Instance.Output("No world."); return; }
            EntityPlayer target = FindPlayerByNameOrId(world, ident);
            // Also try via sender fallback: if ident is "me" and sender has RemoteClientInfo
            if (target == null && (ident == "me" || ident == "self"))
                target = FindPlayerBySender(world, sender);
            if (target == null) { SdtdConsole.Instance.Output($"Player not found: {ident}. Try: bot player <name>, bot player 171, or bot player me (when you type it in-game).\n  Online: " + ListPlayerNames(world)); return; }
            int spawned = 0;
            for (int i = 0; i < count; i++) {
                UnityEngine.Vector3 pos = BotSpawner.PickSpawnNearPlayer(world, target, ModApi.Config);
                if (pos == UnityEngine.Vector3.zero) pos = BotSpawner.PickSpawnNearPlayer(world, target, ModApi.Config); // retry
                if (BotManager.Instance.TrySpawnOne(pos, null, weapon)) spawned++;
            }
            SdtdConsole.Instance.Output($"Spawned {spawned}/{count} bots near {target.EntityName ?? target.PlayerDisplayName ?? ident} (id {target.entityId})" + (weapon != null ? $" weapon={weapon}" : "") + ".");
        }
        static EntityPlayer FindPlayerByNameOrId(World world, string ident)
        {
            if (world == null || string.IsNullOrEmpty(ident)) return null;
            // by entityId
            if (int.TryParse(ident, out int eid)) {
                var e = world.GetEntity(eid) as EntityPlayer;
                if (e != null) return e;
                // also try ClientInfo entityId lookup
                var cm = ConnectionManager.Instance;
                if (cm != null) {
                    var ci = cm.Clients.ForEntityId(eid);
                    if (ci != null) { var ep = world.GetEntity(ci.entityId) as EntityPlayer; if (ep != null) return ep; }
                }
            }
            string low = ident.ToLowerInvariant();
            if (world.Players != null && world.Players.list != null) {
                foreach (var p in world.Players.list) if (p != null) {
                    string name = p.EntityName ?? p.PlayerDisplayName ?? "";
                    if (name.ToLowerInvariant() == low || name.ToLowerInvariant().Contains(low)) return p;
                }
                // exact entityId string already tried; try prefix match
                foreach (var p in world.Players.list) if (p != null) {
                    if (p.entityId.ToString() == ident) return p;
                }
            }
            return null;
        }
        static EntityPlayer FindPlayerBySender(World world, CommandSenderInfo sender)
        {
            try {
                var ci = sender.RemoteClientInfo;
                if (ci != null) {
                    var e = world.GetEntity(ci.entityId) as EntityPlayer;
                    if (e != null) return e;
                }
            } catch {}
            return null;
        }
        static string ListPlayerNames(World world)
        {
            try {
                var names = new List<string>();
                if (world.Players != null && world.Players.list != null) foreach (var p in world.Players.list) if (p != null) names.Add($"{p.EntityName ?? p.PlayerDisplayName ?? "?"}#{p.entityId}");
                return names.Count > 0 ? string.Join(", ", names.ToArray()) : "(none online)";
            } catch { return "(unknown)"; }
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
        void DoNeural(List<string> p)
        {
            string sub2 = p.Count >= 2 ? p[1].ToLowerInvariant() : "status";
            switch (sub2)
            {
                case "status":
                    SdtdConsole.Instance.Output($"Neural: use={ModApi.Config.UseNeuralBrain} loaded={BotMod.AI.BotNeuralBrain.Loaded} weights={BotMod.AI.BotNeuralBrain.WeightCount} hidden={BotMod.AI.BotNeuralBrain.Hidden} inputs={BotMod.AI.BotNeuralBrain.Inputs} outputs={BotMod.AI.BotNeuralBrain.Outputs}");
                    SdtdConsole.Instance.Output($"  path={BotMod.AI.BotNeuralBrain.LoadedPath} hash={BotMod.AI.BotNeuralBrain.LoadedHash}");
                    SdtdConsole.Instance.Output($"  last={BotMod.AI.BotNeuralBrain.LastReason}");
                    SdtdConsole.Instance.Output($"  config path={ModApi.Config.BotNeuralWeightPath}");
                    break;
                case "on": case "enable": case "true": case "1":
                    ModApi.Config.UseNeuralBrain = true;
                    {
                        string why2; bool ok = BotMod.AI.BotNeuralBrain.TryLoad(ModApi.Config.BotNeuralWeightPath, out why2);
                        ModApi.Log("BotNeuralBrain: " + (ok ? "loaded " + why2 : "not loaded (" + why2 + ")"));
                        SdtdConsole.Instance.Output(ok ? "Neural ON, loaded: " + why2 : "Neural ON but load failed: " + why2 + " — heuristic until reload succeeds.");
                    }
                    break;
                case "off": case "disable": case "false": case "0":
                    ModApi.Config.UseNeuralBrain = false;
                    SdtdConsole.Instance.Output("Neural OFF — using heuristic. (weights stay cached; `bot neural on` re-enables)");
                    break;
                case "reload": case "load":
                    {
                        string custom = p.Count >= 3 ? p[2] : ModApi.Config.BotNeuralWeightPath;
                        string why3; bool ok = BotMod.AI.BotNeuralBrain.TryLoad(custom, out why3);
                        ModApi.Log("BotNeuralBrain reload: " + (ok ? "loaded " + why3 : "failed " + why3));
                        SdtdConsole.Instance.Output(ok ? "Neural reloaded: " + why3 : "Neural reload failed: " + why3);
                    }
                    break;
                default:
                    SdtdConsole.Instance.Output("Usage: bot neural <on|off|reload [path]|status>");
                    break;
            }
        }
    }
}
