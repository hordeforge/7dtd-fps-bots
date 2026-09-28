using System;
using System.Collections.Generic;
using System.Globalization;
using BotMod.Config;
using BotMod.Core;
using BotMod.Foundation;
using UnityEngine;

namespace BotMod.Commands
{
    /// <summary>The `bot` admin console command. Authorization is the game's:
    /// ConsoleCmdAbstract's default permission level 0, which Execute does not
    /// relax. Attribution is the mod's: every invocation outside the read-only
    /// set logs its issuer to the server log before dispatching, because the
    /// console echo reaches only the issuing telnet/RCON window and dies with
    /// it. See ReadOnlySubcommands and SenderTag.</summary>
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
            "  bot players                      - online players (name#id), the ids `bot player` accepts\n" +
            "  bot status                       - config summary + alive count\n" +
            "Config (persisted to Config/botmod.json):\n" +
            "  bot config                       - effective config (post-clamp) + the file it was read from\n" +
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

        /// <summary>Every token Execute's dispatch switch accepts, aliases
        /// included. Feeds Suggest (so `bot rm` no longer falls through to "did
        /// you mean" for a subcommand that exists) and the audit-classification
        /// suite, which fails when a token is in neither classification.</summary>
        static readonly string[] Subcommands =
        {
            "help", "?", "h", "status", "config", "cfg", "list", "ls", "players", "who",
            "spawn", "add", "player", "near", "at", "remove", "rm", "kick", "clear",
            "count", "set", "weapon", "gun", "skill", "difficulty", "neural", "vs", "shoot",
            "team", "squad", "teams", "reload", "enable", "disable"
        };

        /// <summary>The tokens that only read state. Everything else in
        /// <see cref="Subcommands"/> is audited, so a subcommand added to the
        /// dispatch switch leaves an issuer-attributed log line by default; the
        /// only way to lose that record is to name it here on purpose, and the
        /// classification suite fails when a token is listed in both.
        ///
        /// Read-only is the right default to withhold: the console echo goes to
        /// the telnet/RCON window and dies with it, so a mutation reachable only
        /// from the console would otherwise be the one kind of privileged
        /// action with no record of who made it. The web surface already logs
        /// every mutation with the caller named. The dashboard polls the web
        /// status on a timer, which is why a read must stay silent here.</summary>
        static readonly string[] ReadOnlySubcommands =
        {
            "help", "?", "h", "status", "config", "cfg", "list", "ls", "players", "who"
        };

        /// <summary>Whether an invocation of <paramref name="sub"/> must leave
        /// an audit line. True for every subcommand not declared read-only,
        /// including one this build does not know (a typo, or a newer
        /// subcommand whose classification is missing): an unaudited mutation
        /// is the failure worth avoiding, so the unknown name is logged rather
        /// than dropped. Public so the suite can pin the classification without
        /// a live console.</summary>
        public static bool IsAuditedSubcommand(string sub)
        {
            for (int i = 0; i < ReadOnlySubcommands.Length; i++)
                if (string.Equals(ReadOnlySubcommands[i], sub, StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }

        /// <summary>Every token Execute accepts. Public for the same suite.</summary>
        public static string[] KnownSubcommands()
        {
            return (string[])Subcommands.Clone();
        }

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
            // One audit line per non-read invocation, emitted before the
            // dispatch switch rather than inside each mutating case: a new
            // subcommand cannot reach a mutation without passing through here,
            // so it is attributed by construction instead of by remembering to
            // log. spawn and player add their own effect line (counts, weapon,
            // target) on top of this one; the rest of a mutation's detail comes
            // from ModApi.PersistConfigFields' "config persist" line, which
            // carries the same change and the same server log.
            if (IsAuditedSubcommand(sub))
                ModApi.Log("bot cmd " + LogSanitizer.Clean(sub) + " by " + SenderTag(_senderInfo));
            try
            {
                switch (sub)
                {
                    case "help": case "?": case "h": SdtdConsole.Instance.Output(GetHelp()); break;
                    case "status": DoStatus(); break;
                    // Every effective value, from the file the server actually
                    // read. `bot status` is the six fields an admin watches;
                    // this is the answer to "is the config what I think it is",
                    // including values Normalize clamped or the difficulty
                    // preset moved and fields the file never set.
                    case "config": case "cfg":
                        SdtdConsole.Instance.Output("Config source: " + BotConfig.ConfigPath()
                            + (BotConfig.ConfigPathOverride() != null ? " (BOTMOD_CONFIG)" : ""));
                        SdtdConsole.Instance.Output("Effective: " + ModApi.Config.EffectiveSummary());
                        break;
                    case "list": case "ls": DoList(); break;
                    case "players": case "who": DoPlayers(); break;
                    case "spawn": case "add": DoSpawn(_params, _senderInfo); break;
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
            // The subcommand names the failing path: the exception alone can be
            // the same one thrown from six different handlers, and the console
            // echo the operator sees is not in this log.
            catch (Exception ex) { SdtdConsole.Instance.Output("bot command failed: " + ex.Message); ModApi.Error("bot cmd '" + LogSanitizer.Clean(sub) + "' failed: " + ex); }
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
        /// <summary>Online players, the identifiers `bot player` accepts. This
        /// listing is the operator's own action: the names are the data, not a
        /// side effect of another command, so nothing else in the command set
        /// prints them (a miss in `bot player` points here instead of dumping
        /// the roster of everyone who happens to be connected).</summary>
        void DoPlayers()
        {
            var world = GameManager.Instance?.World;
            if (world == null) { SdtdConsole.Instance.Output("No world."); return; }
            var roster = OnlinePlayerList(world);
            SdtdConsole.Instance.Output(roster.Count == 0
                ? "No players online."
                : "Online (" + roster.Count + "): " + string.Join(", ", roster.ToArray()));
        }
        void DoSpawn(List<string> p, CommandSenderInfo sender)
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
            // Server log, not just the issuing session: the console echo goes
            // to the telnet/console window and is lost with the connection, so
            // without this line a spawn leaves no trace in the log that the web
            // API, removals and config writes all record. The short-fall half
            // ("max or spawn failed") is what makes a partial spawn visible.
            ModApi.Log("bot cmd spawn: " + spawned + "/" + count + " bots"
                + (weapon != null ? " weapon=" + weapon : "")
                + (spawned < count ? " (max or spawn failed)" : "")
                + " by " + SenderTag(sender));
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
            if (p.Count < 2 || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            { SdtdConsole.Instance.Output($"Usage: bot count <n>  (0..{ModApi.Config.MaxBots})"); return; }
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
            if (target == null) { SdtdConsole.Instance.Output($"Player not found: {ident}. Try: bot player <name>, bot player 171, or bot player me (when you type it in-game), or run 'bot players' for the online list."); return; }
            int spawned = BotManager.Instance.SpawnNearPlayer(target, count, weapon);
            string nearName = LogSanitizer.Clean(target.EntityName ?? target.PlayerDisplayName ?? ident);
            SdtdConsole.Instance.Output($"Spawned {spawned}/{count} bots near {nearName} (id {target.entityId})" + (weapon != null ? $" weapon={weapon}" : "") + ".");
            // Same audit line as DoSpawn: this surface's echo never reaches the
            // server log, and a near-player spawn is the one an incident review
            // most needs to place in time. The name stays on the console echo,
            // which names who the operator asked about; the log line carries
            // the entity id instead, the reference every other log line uses
            // for a player, so a name a player chose never lands in the log
            // file. The echo above is the operator's own session and the one
            // they typed the name into.
            ModApi.Log("bot cmd player: " + spawned + "/" + count + " bots near entity "
                + target.entityId.ToString(CultureInfo.InvariantCulture)
                + (weapon != null ? " weapon=" + weapon : "")
                + " by " + SenderTag(sender));
        }

        /// <summary>Who issued a console mutation, for the audit line. The
        /// console echo identifies the session only to the session itself, so
        /// the server log needs the issuer recorded on the same line as the
        /// effect. Remote telnet clients carry a connection; the server console
        /// and RCON do not, and read as "local". A throwing sender lookup must
        /// not fail the command, so it degrades to "unknown".</summary>
        static string SenderTag(CommandSenderInfo sender)
        {
            try
            {
                var ci = sender.RemoteClientInfo;
                if (ci == null) return "local";
                return "entity " + ci.entityId.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception) { return "unknown"; }
        }
        static EntityPlayer FindPlayerBySender(World world, CommandSenderInfo sender)
        {
            try {
                var ci = sender.RemoteClientInfo;
                if (ci != null) {
                    var e = world.GetEntity(ci.entityId) as EntityPlayer;
                    if (BotManager.IsSelectablePlayer(e)) return e;
                }
            }
            // Reported, not silent: the caller treats null as "resolve me by
            // name", and the "me" name match can land on an unrelated online
            // player, so a broken sender lookup would spawn bots beside the
            // wrong person with no trace of why "me" did not mean the sender.
            catch (Exception ex) { ModApi.Warn("`bot player me` sender lookup failed, falling back to a name match: " + ex); }
            return null;
        }
        /// <summary>Display name and entity id of every connected player, in
        /// world order, each name through <see cref="LogSanitizer"/>. The
        /// entity id is what `bot player` actually matches on, so the listing
        /// is also the way an operator finds the id for a player whose name is
        /// not what they typed. Same
        /// <see cref="BotManager.IsSelectablePlayer"/> gate the lookup applies,
        /// so an id copied from this listing always resolves.</summary>
        static List<string> OnlinePlayerList(World world)
        {
            var names = new List<string>();
            if (world.Players != null && world.Players.list != null)
                foreach (var p in world.Players.list)
                    if (BotManager.IsSelectablePlayer(p)) names.Add($"{LogSanitizer.Clean(p.EntityName ?? p.PlayerDisplayName ?? "?")}#{p.entityId}");
            return names;
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
            if (p.Count < 2 || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int d)) { SdtdConsole.Instance.Output($"Skill {ModApi.Config.Difficulty} (0 bot, 1 easy, 2 normal, 3 hard, 4 nightmare). Usage: bot skill <0-4>"); return; }
            // Clamp + Normalize live in BotConfig.SetDifficulty (shared with the
            // web `skill` action); the persisted value is the post-clamp property.
            ModApi.PersistConfigField(ModApi.Config.SetDifficulty(d), ModApi.Config.Difficulty);
            // Reports the reaction time, which is the tunable the preset
            // actually moves here; aim tightness and burst shape are the
            // characters' and the weapon profile's, so naming them as
            // consequences of `bot skill` would claim an effect it does not
            // have. The next spawn picks up the new lerp.
            SdtdConsole.Instance.Output($"Skill set to {ModApi.Config.Difficulty} (persisted). Reaction {ModApi.Config.ReactionTimeSec:F2}s, vision {ModApi.Config.VisionRange:F0}m, headshot {ModApi.Config.HeadshotChance:P0}.");
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
            // Invariant parse: team ids are protocol tokens like entity ids
            // (same convention as DoRemove / BotArgParser).
            if (p.Count < 4 || !int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int team))
            {
                SdtdConsole.Instance.Output("Usage: bot team assign <botName> <teamId>  (teamId 0 = free-for-all, 1.." + ModApi.Config.BotTeamCount + ")"); return;
            }
            var cfg = ModApi.Config;
            if (team < 0 || team > cfg.BotTeamCount) { SdtdConsole.Instance.Output("teamId must be 0.." + cfg.BotTeamCount + "."); return; }
            string name = BotText.BaseName(p[2]);
            bool live = false;
            // Bot.TeamKey is BaseName(Name) frozen at spawn, so the roster scan
            // re-canonicalizing every live bot's name was re-deriving a value
            // each bot already carries.
            foreach (var b in BotManager.Instance.Bots) if (b.TeamKey == name) { live = true; break; }
            var result = cfg.SetTeamAssignment(name, team);
            if (result != BotConfig.TeamAssignResult.Ok)
            {
                // Reported, never silent: a stored-and-ignored assignment would
                // read to the operator as "the team is set" while the bots keep
                // fighting each other.
                SdtdConsole.Instance.Output(result == BotConfig.TeamAssignResult.AtCapacity
                    ? $"Team map is full ({BotConfig.MaxTeamAssignments} assignments); not stored. 'bot team clear' or assign teamId 0 first."
                    : "Team name is empty or longer than " + BotConfig.MaxTeamNameChars + " characters; not stored.");
                return;
            }
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
            if (p.Count < 2 || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                SdtdConsole.Instance.Output("Teams count: " + ModApi.Config.BotTeamCount + " (0 = free-for-all only). Usage: bot teams <0-8>"); return;
            }
            // Clamp + assignment pruning live in BotConfig.SetTeamCount (shared
            // with the web `teamCount` action).
            ModApi.PersistConfigFields(new[]
            {
                new KeyValuePair<string, object>(ModApi.Config.SetTeamCount(n), ModApi.Config.BotTeamCount),
                new KeyValuePair<string, object>("TeamAssignments", ModApi.Config.SnapshotTeamAssignments())
            });
            SdtdConsole.Instance.Output("Team count set to " + ModApi.Config.BotTeamCount + (ModApi.Config.BotTeamCount == 0 ? " - free-for-all only." : "."));
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
                        SdtdConsole.Instance.Output(ok ? "Neural ON, loaded: " + BotMod.AI.BotNeuralBrain.LastReason : "Neural ON but load failed: " + BotMod.AI.BotNeuralBrain.LastReason + ", heuristic until reload succeeds.");
                    }
                    break;
                case "off": case "disable": case "false": case "0":
                    ModApi.Config.UseNeuralBrain = false;
                    ModApi.PersistConfigField("UseNeuralBrain", false);
                    SdtdConsole.Instance.Output("Neural OFF (persisted), using heuristic. (weights stay cached; `bot neural on` re-enables)");
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
