namespace BotMod.Config
{
    /// <summary>
    /// Shared vs-class gate for bot combat, one definition for every surface
    /// that applies the BotVs* toggles (target acquisition, aggro swap,
    /// DamageEntity filtering) so they cannot drift apart again: the class
    /// gates describe WORLD bodies (players, zombies) only. A bot victim is
    /// governed by the ally rule (AllyBlocks: BotVsBot, squad mode,
    /// team share), never by its body class - spawned bodies are usually
    /// zombieSoldier (an EntityZombie subclass), which made "bot vs zombie
    /// off" silently eat every bot-vs-bot engagement in IsValidTarget and the
    /// DamageEntity patch even with BotVsBot on. Same ordering as
    /// BotBrain.IsFriendly, which already answered bot identity before body
    /// class.
    ///
    /// Pure BCL (bools only): compiled and unit-tested headless by
    /// scripts/test-idempotency.sh.
    /// </summary>
    public static class CombatGates
    {
        /// <summary>True when the vs-class toggles block a bot from engaging
        /// this victim. <paramref name="victimIsBot"/> exempts the victim from
        /// both class gates; the caller still enforces the ally rule on top.</summary>
        public static bool ClassGateBlocks(bool victimIsBot, bool victimIsPlayerBody, bool victimIsZombieBody, bool vsPlayers, bool vsZombies)
        {
            if (victimIsBot) return false;
            if (victimIsPlayerBody && !vsPlayers) return true;
            if (victimIsZombieBody && !vsZombies) return true;
            return false;
        }

        /// <summary>True when two entities may NOT damage each other under the
        /// ally rule. A BOT-PAIR relation: vsBot-off ("bots never fight bots")
        /// and squad mode ("all bots one team") speak only of bot-vs-bot
        /// engagement, so a MIXED pair (a bot against a player or zombie body)
        /// is never allied - those pairs answer to ClassGateBlocks alone.
        /// Applying the bot-pair early returns to mixed pairs silenced ALL bot
        /// fire: the trigger-pull guard in Bot.TryShootBurst runs unguarded by
        /// target class, so with vsBot off or squad mode on bots chased their
        /// targets forever without ever firing a round.</summary>
        public static bool AllyBlocks(bool aIsBot, bool bIsBot, bool botVsBot, bool squad, int teamA, int teamB)
        {
            if (!aIsBot || !bIsBot) return false;
            if (!botVsBot) return true; // global no-bot-vs-bot
            if (squad) return true;     // squad mode: every bot allies
            return teamA != 0 && teamA == teamB; // shared nonzero team only
        }
    }
}
