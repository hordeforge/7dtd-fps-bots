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

                string k = killer != null ? (killer.EntityName ?? killer.name ?? killer.entityId.ToString()) : "?";
                string v = victim != null ? (victim.EntityName ?? victim.name ?? victim.entityId.ToString()) : "?";
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
                        try
                        {
                            int z = victimCountsAsZombie ? 1 : 0;
                            int p = victimCountsAsZombie ? 0 : 1;
                            // Team isn't replicated on bots; use 0 (no team) to avoid bogus friendly-fire checks.
                            try { GameManager.Instance?.AddScoreServer(killer.entityId, z, p, 0, 0); }
                            catch
                            {
                                try { killer.Score++; } catch { }
                            }
                        }
                        catch { try { killer.Score++; } catch { } }

                        // Shout it so players see bot frags alongside player frags in chat.
                        try
                        {
                            string msg = $"[Bot] {k} fragged {v}";
                            // Dedicated servers use GameManager.GameMessage(SGameEntityKilledData) under the hood;
                            // we don't call a player-only GameMessage overload directly.
                            global::Log.Out($"[BotMod] {msg} (K:{killer.KilledPlayers} Z:{killer.KilledZombies} D:{killer.Died} S:{killer.Score})");
                        }
                        catch { }
                    }
                    catch { }
                }

                // Bot victims also need a visible death bump even if the killer already logged.
                // Victim-side Died/Score is normally handled by DamageEntity death path, but keep a trace.
                if (victimIsBot && victim != null)
                {
                    try { global::Log.Out($"[BotMod] victim [Bot] {v} died (D:{victim.Died} S:{victim.Score})"); } catch { }
                }
            }
            catch { }
        }
    }
}
