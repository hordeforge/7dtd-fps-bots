using HarmonyLib;

namespace BotMod.Patches
{
    [HarmonyPatch(typeof(EntityAlive), "OnEntityDeath")]
    public static class BotDeathPatch
    {
        static void Postfix(EntityAlive __instance)
        {
            try
            {
                if (__instance == null) return;
                if (BotMod.Core.BotManager.Instance.IsBotEntity(__instance.entityId))
                {
                    BotMod.Core.BotManager.Instance.NotifyBotDeath(__instance.entityId);
                    if (!ModApi.Config.DropLootOnDeath) try { __instance.lootList = null; } catch { }
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(EntityAlive), "DamageEntity")]
    public static class BotDamageFilterPatch
    {
        static bool Prefix(EntityAlive __instance, DamageSource _damageSource, int _strength)
        {
            try
            {
                if (_damageSource is DamageSourceEntity dse)
                {
                    int attackerId = dse.CreatorEntityId;
                    if (attackerId == 0) attackerId = dse.ownerEntityId;
                    if (BotMod.Core.BotManager.Instance.IsBotEntity(attackerId))
                    {
                        var cfg = ModApi.Config;
                        if (__instance is EntityPlayer && !cfg.BotVsPlayer) return false;
                        if (__instance is EntityZombie && !cfg.BotVsZombie) return false;
                        if (BotMod.Core.BotManager.Instance.IsBotEntity(__instance.entityId) && !cfg.BotVsBot) return false;
                    }
                }
                // Route damage back to bot for FPS dodge/aggro swap (victim is a bot)
                if (BotMod.Core.BotManager.Instance.IsBotEntity(__instance.entityId) && _damageSource is DamageSourceEntity ds2)
                {
                    int aid = ds2.CreatorEntityId != 0 ? ds2.CreatorEntityId : ds2.ownerEntityId;
                    var world = GameManager.Instance?.World;
                    if (world != null)
                    {
                        var attacker = world.GetEntity(aid) as EntityAlive;
                        var victim = BotMod.Core.BotManager.Instance.GetBot(__instance.entityId);
                        if (victim != null) try { victim.OnDamaged(attacker); } catch { }
                    }
                }
            }
            catch { }
            return true;
        }
    }
}
