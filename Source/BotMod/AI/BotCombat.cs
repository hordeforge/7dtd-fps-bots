namespace BotMod.AI
{
    public static class BotCombat
    {
        public static void OnKilled(EntityAlive killer, EntityAlive victim)
        {
            try
            {
                bool killerIsBot = killer != null && BotMod.Core.BotManager.Instance.IsBotEntity(killer.entityId);
                bool victimIsBot = victim != null && BotMod.Core.BotManager.Instance.IsBotEntity(victim.entityId);
                bool killerIsPlayer = killer is EntityPlayer;
                bool victimIsPlayer = victim is EntityPlayer;
                if (!killerIsBot && !victimIsBot && !killerIsPlayer && !victimIsPlayer) return;

                // EntityName for players is player-chosen text (arbitrary under
                // EAC-off / synthetic-auth joins), so scrub it before it reaches
                // the server log or client chat: control, DEL/C1, bidi and
                // zero-width characters would otherwise forge log lines or
                // reorder visible chat text (same contract as the web API's
                // sanitized audit fields).
                string k = BotMod.Web.LogSanitizer.Clean(killer != null ? (killer.EntityName ?? killer.name ?? killer.entityId.ToString()) : "?");
                string v = BotMod.Web.LogSanitizer.Clean(victim != null ? (victim.EntityName ?? victim.name ?? victim.entityId.ToString()) : "?");
                ModApi.Log($"Kill: {k} killed {v}");

                // Keep vanilla score paths for player->anything. For bot killers we must credit manually
                // because vanilla AwardKill only awards when killer is EntityPlayer.
                if (killerIsBot && killer != null && victim != null)
                {
                    try
                    {
                        // Bucket: zombie victims count as zombie kills for the leaderboard, everything else as player kills.
                        bool victimCountsAsZombie = victim is EntityZombie;
                        if (victimCountsAsZombie)
                        {
                            try { killer.KilledZombies++; } catch { }
                        }
                        else
                        {
                            try { killer.KilledPlayers++; } catch { }
                        }

                        // Score mirrors EntityAlive.AwardKill -> AddScore via GameStats 28/29/30.
                        // Do the AddScore path directly so scores track even on zombie-entity killers.
                        int z = victimCountsAsZombie ? 1 : 0;
                        int p = victimCountsAsZombie ? 0 : 1;
                        // Team isn't replicated on bots; use 0 (no team) to avoid bogus friendly-fire checks.
                        try { GameManager.Instance?.AddScoreServer(killer.entityId, z, p, 0, 0); }
                        catch { try { killer.Score++; } catch { } }

                        // Shout it so players see bot frags alongside player frags in chat.
                        try
                        {
                            string msg = $"[Bot] {k} fragged {v}";
                            ModApi.Log($"{msg} (K:{killer.KilledPlayers} Z:{killer.KilledZombies} D:{killer.Died} S:{killer.Score})");
                            // Best-effort chat broadcast to connected players (reflection-based so the
                            // exact GameMessageServer signature never breaks the build; no-op if the
                            // API differs or no players are connected).
                            if (ModApi.Config.BotAnnounceKillsInChat)
                                try { ChatMessageServer(msg); } catch { }
                        }
                        catch { }
                    }
                    catch { }
                }

                // Bot victims also need a visible death bump even if the killer already logged.
                // Victim-side Died/Score is normally handled by DamageEntity death path, but keep a trace.
                if (victimIsBot && victim != null)
                {
                    try { ModApi.Log($"victim [Bot] {v} died (D:{victim.Died} S:{victim.Score})"); } catch { }
                }
            }
            catch (System.Exception ex)
            {
                // Kill events are rare; an unexpected failure here (score crediting,
                // chat announce) must not vanish without a trace.
                ModApi.Warn("OnKilled failed: " + ex);
            }
        }

        /// <summary>Best-effort server->client chat broadcast (dedicated-safe). Uses reflection
        /// against GameManager/ChatMessageServer so the exact API signature never breaks the
        /// build; no-op when the API differs or no players are connected.</summary>
        static void ChatMessageServer(string msg)
        {
            try
            {
                var gm = GameManager.Instance;
                if (gm == null) return;
                // Recipients = connected players (empty collection => broadcast).
                System.Collections.Generic.List<ClientInfo> cts = new System.Collections.Generic.List<ClientInfo>();
                try { if (ConnectionManager.Instance?.Clients?.List != null) cts = new System.Collections.Generic.List<ClientInfo>(ConnectionManager.Instance.Clients.List); } catch { }
                // Prefer ChatMessageServer(int?) constructor chain; wrap as few assumptions as possible.
                try
                {
                    var t = typeof(GameManager);
                    // Try GameManager.GameMessage(EnumGameMessages, string, string, float, string[]) or similar.
                    var m = t.GetMethod("GameMessage", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
                    if (m != null)
                    {
                        // Best-effort: find an overload accepting (EnumGameMessages, string message).
                        foreach (var ov in t.GetMethods())
                        {
                            if (ov.Name != "GameMessage") continue;
                            var ps = ov.GetParameters();
                            try
                            {
                                if (ps.Length >= 2 && ps[0].ParameterType.Name == "EnumGameMessages" && ps[1].ParameterType == typeof(string))
                                    { ov.Invoke(gm, new object[] { (int)0, msg }); return; }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
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
                            var p = sp.GetParameters();
                            try
                            {
                                if (p.Length >= 1 && p[0].ParameterType == typeof(byte))
                                    { sp.Invoke(inst, new object[] { (byte)0 }); return; }
                                if (p.Length >= 2)
                                    { sp.Invoke(inst, new object[] { cts, msg, false }); return; }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            catch { }
        }
    }
}
