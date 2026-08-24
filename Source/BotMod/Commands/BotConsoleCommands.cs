using System;
using System.Collections.Generic;
using System.Globalization;
using BotMod.Core;
using UnityEngine;

namespace BotMod.Commands
{
    public class ConsoleCmdBot : ConsoleCmdAbstract
    {
        public override string[] getCommands() => new[] { "bot" };
        public override string getDescription() => "FPS bots: spawn, list, remove, config.";
        public override string getHelp() =>
            "Usage: bot <subcommand> [args]\n" +
            "Spawning:\n" +
            "  bot spawn [count] [x z] [weapon] - spawn bots (default 1); no x z = DM spawnpoints\n" +
            "  bot player <nameOrId> [count] [weapon] - spawn near that player ('me' = commanding player)\n" +
            "  bot remove all | bot remove <id> - despawn all / one bot\n" +
            "  bot list                         - alive bots (weapon/state/target/hp/burst)\n" +
            "  bot status                       - config summary + alive count\n" +
            "Config (persisted to Config/botmod.json):\n" +
            "  bot count <n>                    - keep n alive\n" +
            "  bot weapon <gunId|mixed>         - default weapon for future spawns\n" +
            "  bot skill <0-4>                  - 0 bot, 1 easy, 2 normal, 3 hard, 4 nightmare\n" +
            "  bot neural <on|off|reload [path]|status> - GA-evolved brain toggle/reload\n" +
            "  bot vs <bot|zombie|player> <on|off> - which target classes bots shoot (all on = FFA)\n" +
            "Teams:\n" +
            "  bot team <on|off>                - squad mode: all bots allies, never fight each other\n" +
            "  bot team assign <name> <id>      - put that bot on team id (0 = free-for-all)\n" +
            "  bot team list | bot team clear   - show / clear team assignments\n" +
            "  bot teams <0-8>                  - number of teams (0 = free-for-all only)\n" +
            "Lifecycle:\n" +
            "  bot reload                       - re-read Config/botmod.json\n" +
            "  bot enable | bot disable         - master switch (persisted)\n" +
            "Shortcuts: add=spawn ls=list rm/kick/clear=remove set=count gun=weapon near/at=player shoot=vs squad=team\n" +
            "Examples:\n" +
            "  bot spawn 4                      - 4 mixed-loadout bots at DM spawnpoints\n" +
            "  bot spawn 1 -1200.5 300          - 1 bot near x=-1200.5 z=300 (dot-decimal coords)\n" +
            "  bot player Kira 3 gunMGT1AK47    - 3 AK bots near Kira (out-of-sight preferred, ~22m ideal)\n" +
            "  bot vs bot off                   - bots stop shooting each other";

        static readonly string[] Subcommands =
        {
            "help", "status", "list", "spawn", "player", "remove", "count", "weapon",
            "skill", "neural", "vs", "team", "teams", "reload", "enable", "disable"
        };

        static string Suggest(string sub)
        {
            if (sub.Length == 0) return "";
            foreach (var name in Subcommands)
                if (name.StartsWith(sub, StringComparison.OrdinalIgnoreCase))
                    return " Did you mean '" + name + "'?";
            return "";
        }

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
                    case "enable": ModApi.Config.Enabled = true; ModApi.PersistConfigField("Enabled", true); SdtdConsole.Instance.Output("BotMod enabled (persisted)."); break;
                    case "disable": ModApi.Config.Enabled = false; ModApi.PersistConfigField("Enabled", false); SdtdConsole.Instance.Output("BotMod disabled (persisted). Existing bots remain until removed."); break;
                    case "neural": DoNeural(_params); break;
                    case "vs": case "shoot": DoVs(_params); break;
                    case "team": case "squad": DoTeam(_params); break;
                    case "teams": DoTeams(_params); break;
                    default: SdtdConsole.Instance.Output("Unknown bot subcommand: '" + sub + "'." + Suggest(sub) + " Try: bot help"); break;
                }
            }
            catch (Exception ex) { SdtdConsole.Instance.Output("bot command failed: " + ex.Message); ModApi.Error("bot cmd failed: " + ex); }
        }
        void DoStatus()
        {
            var cfg = ModApi.Config; var mgr = BotManager.Instance;
            SdtdConsole.Instance.Output($"BotMod: enabled={cfg.Enabled} target={cfg.TargetBotCount} max={cfg.MaxBots} alive={mgr.BotCount} class={cfg.BotEntityClass} weapon={cfg.BotWeapon} diff={cfg.Difficulty} vision={cfg.VisionRange} attack={cfg.AttackRange}");
            SdtdConsole.Instance.Output($"  team={cfg.BotTeam} teams={cfg.BotTeamCount} assigned={cfg.SnapshotTeamAssignments().Count} vsBot={cfg.BotVsBot} vsZombie={cfg.BotVsZombie} vsPlayer={cfg.BotVsPlayer} (bot team on|off / bot vs <target> on|off)");
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
            if (!BotArgParser.TryParseSpawn(p, 1, out int count, out float x, out float z, out bool hasPos, out string weapon, out string error))
            { SdtdConsole.Instance.Output(error); return; }
            Vector3? pos = null;
            if (hasPos)
            {
                // Ground the requested column on the terrain (same helper as every
                // generated spawn path); a raw "x, 60, z" buries bots in hills or
                // drops them from the sky wherever the surface is not at y≈60.
                var world = GameManager.Instance?.World;
                Vector3 raw = new Vector3(x, 60f, z);
                pos = world != null ? BotSpawner.GroundPosition(world, raw) : raw;
            }
            int spawned = 0;
            for (int i = 0; i < count; i++) if (BotManager.Instance.TrySpawnOne(pos, weaponOverride: weapon)) spawned++;
            SdtdConsole.Instance.Output($"Spawned {spawned}/{count} bots" + (weapon != null ? $" weapon={weapon}" : "") + "." + (spawned < count ? " (max or spawn failed)" : ""));
        }
        void DoRemove(List<string> p)
        {
            // Documented grammar (see `bot help`): bot remove all | bot remove
            // <id>. Bare `bot remove` keeps its remove-all shortcut; any other
            // token is a named usage error - a typo like `bot remove al` must
            // not silently wipe every live bot. Invariant parse: entity ids are
            // protocol tokens, not locale text.
            if (p.Count >= 2)
            {
                string arg = p[1];
                if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    int n = BotManager.Instance.RemoveAllBots("command");
                    SdtdConsole.Instance.Output($"Removed {n} bots.");
                    return;
                }
                if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                {
                    bool exists = BotManager.Instance.GetBot(id) != null;
                    bool ok = BotManager.Instance.RemoveBot(id);
                    if (ok) SdtdConsole.Instance.Output($"Removed bot {id}.");
                    else if (exists) SdtdConsole.Instance.Output($"Removal of bot {id} failed; it stays tracked - see server log, then retry.");
                    else SdtdConsole.Instance.Output($"No bot with id {id}. Try: bot list");
                    return;
                }
                SdtdConsole.Instance.Output($"Unrecognized argument '{arg}'.\n  Usage: bot remove all | bot remove <id>");
                return;
            }
            int n2 = BotManager.Instance.RemoveAllBots("command"); SdtdConsole.Instance.Output($"Removed {n2} bots.");
        }
        void DoCount(List<string> p)
        {
            if (p.Count < 2 || !int.TryParse(p[1], out int n)) { SdtdConsole.Instance.Output($"Usage: bot count <n>  (0..{ModApi.Config.MaxBots})"); return; }
            n = Math.Max(0, Math.Min(ModApi.Config.MaxBots, n)); ModApi.Config.TargetBotCount = n; ModApi.PersistConfigField("TargetBotCount", n); SdtdConsole.Instance.Output($"Target bot count set to {n} (persisted). Will converge within a few seconds.");
        }
        void DoPlayer(List<string> p, CommandSenderInfo sender)
        {
            if (p.Count < 2) { SdtdConsole.Instance.Output("Usage: bot player <nameOrId> [count] [weapon]\n  e.g. bot player Kira / bot player 171 3 gunShotgunT1DoubleBarrel / bot player me"); return; }
            string ident = p[1];
            if (!BotArgParser.TryParsePlayer(p, 2, out int count, out string weapon, out string error))
            { SdtdConsole.Instance.Output(error); return; }
            var world = GameManager.Instance?.World;
            if (world == null) { SdtdConsole.Instance.Output("No world."); return; }
            // "me"/"self" resolves to the commanding player FIRST: the name
            // lookup's substring match would otherwise hit any online player
            // whose name contains "me" (e.g. "Jeremy") and spawn near them
            // instead of the sender documented in `bot help`.
            EntityPlayer target = ident == "me" || ident == "self" ? FindPlayerBySender(world, sender) : null;
            if (target == null) target = BotManager.FindPlayerByNameOrId(world, ident);
            if (target == null) { SdtdConsole.Instance.Output($"Player not found: {ident}. Try: bot player <name>, bot player 171, or bot player me (when you type it in-game).\n  Online: " + ListPlayerNames(world)); return; }
            int spawned = 0;
            for (int i = 0; i < count; i++) {
                UnityEngine.Vector3 pos = BotSpawner.PickSpawnNearPlayer(world, target, ModApi.Config);
                if (pos == UnityEngine.Vector3.zero) pos = BotSpawner.PickSpawnNearPlayer(world, target, ModApi.Config); // retry
                if (BotManager.Instance.TrySpawnOne(pos, weaponOverride: weapon)) spawned++;
            }
            SdtdConsole.Instance.Output($"Spawned {spawned}/{count} bots near {target.EntityName ?? target.PlayerDisplayName ?? ident} (id {target.entityId})" + (weapon != null ? $" weapon={weapon}" : "") + ".");
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
            // Same grammar as the spawn tails and the web API's spawnNear: an
            // off-grammar id used to persist silently and every later spawn held
            // no item (ItemClass lookup misses) while running pistol stats.
            if (!BotArgParser.LooksLikeWeapon(p[1]))
            { SdtdConsole.Instance.Output($"Unknown weapon '{p[1]}'. Weapon ids start with 'gun' (or use 'mixed').\n  Usage: bot weapon <gunId|mixed>"); return; }
            ModApi.Config.BotWeapon = p[1]; ModApi.PersistConfigField("BotWeapon", p[1]); SdtdConsole.Instance.Output($"Default weapon set to {p[1]} (persisted). Next spawns use it; existing bots keep theirs.");
        }
        void DoSkill(List<string> p)
        {
            if (p.Count < 2 || !int.TryParse(p[1], out int d)) { SdtdConsole.Instance.Output($"Skill {ModApi.Config.Difficulty} (0 bot, 1 easy, 2 normal, 3 hard, 4 nightmare). Usage: bot skill <0-4>"); return; }
            d = Math.Max(0, Math.Min(4, d)); ModApi.Config.Difficulty = d; ModApi.Config.Normalize(); ModApi.PersistConfigField("Difficulty", d); SdtdConsole.Instance.Output($"Skill set to {d} (persisted). Aim jitter {ModApi.Config.AimJitterDegrees:F1}deg, reaction {ModApi.Config.ReactionTimeSec:F2}s.");
        }
        void DoVs(List<string> p)
        {
            if (p.Count < 3 || !ParseOnOff(p[2], out bool on))
            {
                SdtdConsole.Instance.Output("Usage: bot vs <bot|zombie|player> <on|off>  e.g. bot vs bot off  (all three on = free-for-all)");
                return;
            }
            string target = p[1].ToLowerInvariant();
            var cfg = ModApi.Config;
            if (!cfg.SetVsTarget(target, on, out string field))
            {
                SdtdConsole.Instance.Output("Unknown target: " + target + ". Use bot|zombie|player.");
                return;
            }
            ModApi.PersistConfigField(field, on);
            SdtdConsole.Instance.Output("Bots will now shoot " + target + ": " + (on ? "ON" : "OFF") + (ModApi.Config.BotTeam && target.StartsWith("bot", StringComparison.OrdinalIgnoreCase) ? " (note: squad mode overrides vs bot)" : "") + ".");
        }
        void DoTeam(List<string> p)
        {
            string sub2 = p.Count >= 2 ? p[1].ToLowerInvariant() : "list";
            if (ParseOnOff(p.Count >= 2 ? p[1] : "", out bool on))
            {
                ModApi.Config.BotTeam = on;
                ModApi.PersistConfigField("BotTeam", on);
                SdtdConsole.Instance.Output(on ? "Squad mode ON: all bots are allies. (players/zombies still fair game)" : "Squad mode OFF: bots fight per team assignment.");
                return;
            }
            switch (sub2)
            {
                case "assign": case "set": DoTeamAssign(p); break;
                case "clear": case "reset": DoTeamClear(); break;
                case "list": case "ls": case "status": DoTeamList(); break;
                default:
                    SdtdConsole.Instance.Output("Usage: bot team <on|off> | bot team assign <botName> <teamId> | bot team list | bot team clear\n  teamId 0 = free-for-all, 1.." + ModApi.Config.BotTeamCount + ". Also: bot teams <count> sets the number of teams.");
                    break;
            }
        }
        void DoTeamAssign(List<string> p)
        {
            if (p.Count < 4 || !int.TryParse(p[3], out int team))
            {
                SdtdConsole.Instance.Output("Usage: bot team assign <botName> <teamId>  (teamId 0 = free-for-all, 1.." + ModApi.Config.BotTeamCount + ")"); return;
            }
            var cfg = ModApi.Config;
            if (team < 0 || team > cfg.BotTeamCount) { SdtdConsole.Instance.Output("teamId must be 0.." + cfg.BotTeamCount + "."); return; }
            string name = BotManager.BaseName(p[2]);
            bool live = false;
            foreach (var b in BotManager.Instance.Bots) if (BotManager.BaseName(b.Name) == name) { live = true; break; }
            cfg.SetTeamAssignment(name, team);
            ModApi.PersistConfigField("TeamAssignments", cfg.SnapshotTeamAssignments());
            SdtdConsole.Instance.Output((team == 0 ? name + " is now free-for-all." : name + " assigned to team " + team + " (applies live).") + (live ? "" : " No live bot with that name - applies to future spawns."));
        }
        void DoTeamList()
        {
            var cfg = ModApi.Config;
            // Snapshot: never enumerate the live map (web threads mutate it).
            Dictionary<string, int> teams = cfg.SnapshotTeamAssignments();
            SdtdConsole.Instance.Output("Teams: count=" + cfg.BotTeamCount + " squadMode=" + cfg.BotTeam + " assigned=" + teams.Count);
            if (teams.Count == 0) { SdtdConsole.Instance.Output("  (none - all bots free-for-all)"); return; }
            foreach (var kv in teams) SdtdConsole.Instance.Output($"  {kv.Key} -> team {kv.Value}");
        }
        void DoTeamClear()
        {
            ModApi.Config.ClearTeamAssignments();
            ModApi.PersistConfigField("TeamAssignments", ModApi.Config.SnapshotTeamAssignments());
            SdtdConsole.Instance.Output("All team assignments cleared - every bot is free-for-all.");
        }
        void DoTeams(List<string> p)
        {
            if (p.Count < 2 || !int.TryParse(p[1], out int n))
            {
                SdtdConsole.Instance.Output("Teams count: " + ModApi.Config.BotTeamCount + " (0 = free-for-all only). Usage: bot teams <0-8>"); return;
            }
            n = Math.Max(0, Math.Min(8, n));
            ModApi.Config.BotTeamCount = n;
            ModApi.Config.Normalize(); // drops assignments outside the new range
            ModApi.PersistConfigField("BotTeamCount", n);
            ModApi.PersistConfigField("TeamAssignments", ModApi.Config.SnapshotTeamAssignments());
            SdtdConsole.Instance.Output("Team count set to " + n + (n == 0 ? " - free-for-all only." : "."));
        }
        static bool ParseOnOff(string v, out bool on)
        {
            string t = v.ToLowerInvariant();
            if (t == "on" || t == "true" || t == "1" || t == "yes") { on = true; return true; }
            if (t == "off" || t == "false" || t == "0" || t == "no") { on = false; return true; }
            on = false; return false;
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
                    ModApi.PersistConfigField("UseNeuralBrain", true);
                    {
                        // LoadNeuralWeights logs the server-side outcome; the
                        // user-facing echo reads LastReason (TryLoad records it
                        // on both the success and failure path).
                        bool ok = ModApi.LoadNeuralWeights("loaded", ", using heuristic.");
                        SdtdConsole.Instance.Output(ok ? "Neural ON, loaded: " + BotMod.AI.BotNeuralBrain.LastReason : "Neural ON but load failed: " + BotMod.AI.BotNeuralBrain.LastReason + " — heuristic until reload succeeds.");
                    }
                    break;
                case "off": case "disable": case "false": case "0":
                    ModApi.Config.UseNeuralBrain = false;
                    ModApi.PersistConfigField("UseNeuralBrain", false);
                    SdtdConsole.Instance.Output("Neural OFF (persisted) — using heuristic. (weights stay cached; `bot neural on` re-enables)");
                    break;
                case "reload": case "load":
                    {
                        string custom = p.Count >= 3 ? p[2] : ModApi.Config.BotNeuralWeightPath;
                        string why3; bool ok = BotMod.AI.BotNeuralBrain.TryLoad(custom, out why3);
                        if (ok) ModApi.Log("BotNeuralBrain reload: loaded " + why3);
                        else ModApi.Warn("BotNeuralBrain reload failed: " + why3);
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
