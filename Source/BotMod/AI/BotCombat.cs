using System;
using BotMod.Core;
using BotMod.Foundation;

namespace BotMod.AI
{
    public static class BotCombat
    {
        /// <summary>Log-safe label for one combat participant. EntityName is
        /// player-chosen text (arbitrary under EAC-off / synthetic-auth joins),
        /// so scrub it before it reaches the server log or client chat: control,
        /// DEL/C1, bidi and zero-width characters would otherwise forge log
        /// lines or reorder visible chat text (same contract as the web API's
        /// sanitized audit fields). Entity ids are protocol tokens, so the
        /// fallback renders invariantly (same convention as the invariant
        /// int.TryParse on every id surface).</summary>
        static string Label(EntityAlive e)
        {
            if (e == null) return "?";
            return LogSanitizer.Clean(e.EntityName ?? e.name ?? e.entityId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public static void OnKilled(EntityAlive killer, EntityAlive victim)
        {
            try
            {
                bool killerIsBot = killer != null && BotManager.Instance.IsBotEntity(killer.entityId);
                bool victimIsBot = victim != null && BotManager.Instance.IsBotEntity(victim.entityId);
                bool killerIsPlayer = killer is EntityPlayer;
                bool victimIsPlayer = victim is EntityPlayer;
                if (!killerIsBot && !victimIsBot && !killerIsPlayer && !victimIsPlayer) return;

                string k = Label(killer);
                string v = Label(victim);
                ModApi.Log($"Kill: {k} killed {v}");

                // Keep vanilla score paths for player->anything. For bot killers we must credit manually
                // because vanilla AwardKill only awards when killer is EntityPlayer.
                if (killerIsBot && killer != null && victim != null)
                {
                    try
                    {
                        // Bucket: zombie victims count as zombie kills for the leaderboard, everything else as player kills.
                        bool victimCountsAsZombie = victim is EntityZombie;
                        // A throw here is lost leaderboard credit, not a degraded
                        // cosmetic: the frag count is what the dashboard's kill
                        // column reads. Rate-limited, so a body type that always
                        // rejects the write costs one line per window.
                        if (victimCountsAsZombie)
                        {
                            try { killer.KilledZombies++; }
                            catch (Exception ex) { ModApi.WarnRateLimited(() => "zombie kill credit write failed for bot " + killer.entityId + ": " + ex.Message); }
                        }
                        else
                        {
                            try { killer.KilledPlayers++; }
                            catch (Exception ex) { ModApi.WarnRateLimited(() => "player kill credit write failed for bot " + killer.entityId + ": " + ex.Message); }
                        }

                        // Score mirrors EntityAlive.AwardKill -> AddScore via GameStats 28/29/30.
                        // Do the AddScore path directly so scores track even on zombie-entity killers.
                        int z = victimCountsAsZombie ? 1 : 0;
                        int p = victimCountsAsZombie ? 0 : 1;
                        // Team isn't replicated on bots; use 0 (no team) to avoid bogus friendly-fire checks.
                        // The local bump is a fallback, not an equivalent: only
                        // GameManager's AddScoreServer feeds the server-side
                        // scoreboard, so a takeover is worth reporting.
                        try { GameManager.Instance?.AddScoreServer(killer.entityId, z, p, 0, 0); }
                        catch (Exception ex)
                        {
                            ModApi.WarnRateLimited(() => "AddScoreServer failed for bot " + killer.entityId
                                + ", bumping the entity's local score only (server scoreboard will lag): " + ex.Message);
                            try { killer.Score++; } catch { }
                        }

                        // Shout it so players see bot frags alongside player frags in chat.
                        string msg = $"[Bot] {k} fragged {v}";
                        ModApi.Log($"{msg} (K:{killer.KilledPlayers} Z:{killer.KilledZombies} D:{killer.Died} S:{killer.Score})");
                        // Best-effort chat broadcast to connected players (reflection-based so the
                        // exact GameMessageServer signature never breaks the build; no-op if the
                        // API differs or no players are connected).
                        // ChatMessageServer owns its own reporting: it distinguishes
                        // "no usable API" from a delivered message, so a throw
                        // escaping it must not be swallowed as though it were sent.
                        if (ModApi.Config.BotAnnounceKillsInChat)
                        {
                            try { ChatMessageServer(msg); }
                            catch (Exception ex) { ModApi.WarnRateLimited(() => "kill chat announce threw: " + ex.Message); }
                        }
                    }
                    catch (Exception ex) { ModApi.WarnRateLimited(() => "bot kill crediting failed for " + killer.entityId + " -> " + victim.entityId + ": " + ex.Message); }
                }

                // Bot victims also need a visible death bump even if the killer already logged.
                // Victim-side Died/Score is normally handled by DamageEntity death path, but keep a trace.
                if (victimIsBot && victim != null)
                {
                    ModApi.Log($"victim [Bot] {v} died (D:{victim.Died} S:{victim.Score})");
                }
            }
            catch (Exception ex)
            {
                // Kill events are rare; an unexpected failure here (score crediting,
                // chat announce) must not vanish without a trace.
                ModApi.Warn("OnKilled failed: " + ex);
            }
        }

        /// <summary>Best-effort server->client chat broadcast (dedicated-safe). Uses reflection
        /// against GameManager/ChatMessageServer so the exact API signature never breaks the
        /// build; no-op when the API differs or no players are connected. A persistent
        /// no-send (game update changed every probed signature) is surfaced through the
        /// rate-limited warn gate instead of disabling announcements silently forever.
        /// Exception: with no GameManager the method returns before that gate, so
        /// announcements go quiet unlogged.</summary>
        static void ChatMessageServer(string msg)
        {
            bool sent = false;
            try
            {
                var gm = GameManager.Instance;
                if (gm == null) return;
                // Prefer the cached GameManager.GameMessage method; wrap as few assumptions as possible.
                // The overload is resolved once per process: Type.GetMethods()
                // walks the whole method table and materializes a MethodInfo per
                // entry, and this runs on every bot kill, where a busy free-for-all
                // produces several a second. The assembly cannot change under a
                // running process, so a miss is a miss for the rest of the run.
                var gameMessage = GameMessageOverload();
                if (gameMessage != null)
                {
                    try { gameMessage.Invoke(gm, new object[] { (int)0, msg }); sent = true; return; }
                    catch { }
                }
                // Fallback: direct ChatMessageServer packet if reachable via NetPackage reflection.
                try
                {
                    var cmT = System.Type.GetType("ChatMessageServer, Assembly-CSharp");
                    if (cmT != null)
                    {
                        var inst = System.Activator.CreateInstance(cmT);
                        var sp = cmT.GetMethod("SendPackage", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        if (sp != null)
                        {
                            // Only an overload that can carry the message counts:
                            // a bare (byte) invocation would "succeed" while
                            // dropping msg entirely, announcing nothing and
                            // silencing the not-delivered warn below forever.
                            var p = sp.GetParameters();
                            try
                            {
                                if (p.Length >= 2)
                                {
                                    // Recipients = connected players (empty collection
                                    // => broadcast). Built here, not for both
                                    // branches: the preferred overload above never
                                    // looks at it. A read that throws leaves an
                                    // empty list, which sends to nobody while the
                                    // announce still counts as delivered below, so
                                    // it says so rather than passing for a send.
                                    System.Collections.Generic.List<ClientInfo> cts = new System.Collections.Generic.List<ClientInfo>();
                                    try
                                    {
                                        if (ConnectionManager.Instance?.Clients?.List != null)
                                            cts = new System.Collections.Generic.List<ClientInfo>(ConnectionManager.Instance.Clients.List);
                                    }
                                    catch (Exception ex)
                                    {
                                        ModApi.WarnRateLimited(() => "kill chat recipient list unread, announcing to an empty recipient set: " + ex.Message);
                                    }
                                    sp.Invoke(inst, new object[] { cts, msg, false });
                                    sent = true;
                                    return;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            catch { }
            if (!sent) ModApi.WarnRateLimited(() => "kill chat announce not delivered: no usable GameMessage/ChatMessageServer API (kill log lines unaffected)");
        }

        /// <summary>Cached GameManager.GameMessage(EnumGameMessages, string)
        /// lookup. The probe runs once per process: the method table cannot
        /// change under a running process, so a server whose API lacks the
        /// overload is not re-walked on every kill either.</summary>
        static System.Reflection.MethodInfo GameMessageOverload()
        {
            if (!s_gameMessageProbed)
            {
                s_gameMessageProbed = true;
                s_gameMessageOverload = ProbeGameMessageOverload();
            }
            return s_gameMessageOverload;
        }

        static System.Reflection.MethodInfo ProbeGameMessageOverload()
        {
            try
            {
                foreach (var ov in typeof(GameManager).GetMethods())
                {
                    if (ov.Name != "GameMessage") continue;
                    var ps = ov.GetParameters();
                    if (ps.Length >= 2 && ps[0].ParameterType.Name == "EnumGameMessages" && ps[1].ParameterType == typeof(string))
                        return ov;
                }
            }
            catch { }
            return null;
        }

        static bool s_gameMessageProbed;
        static System.Reflection.MethodInfo s_gameMessageOverload;
    }
}
