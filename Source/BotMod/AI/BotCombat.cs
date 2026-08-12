using UnityEngine;

namespace BotMod.AI
{
    public static class BotCombat
    {
        public static void OnKilled(EntityAlive killer, EntityAlive victim)
        {
            // Could add score, announcements, etc.
            try
            {
                bool killerIsBot = killer != null && BotMod.Core.BotManager.Instance.IsBotEntity(killer.entityId);
                bool victimIsBot = victim != null && BotMod.Core.BotManager.Instance.IsBotEntity(victim.entityId);
                if (killerIsBot || victimIsBot)
                {
                    string k = killer != null ? killer.EntityName : "?";
                    string v = victim != null ? victim.EntityName : "?";
                    ModApi.Log($"Kill: {k} killed {v}");
                }
            }
            catch { }
        }
    }
}
