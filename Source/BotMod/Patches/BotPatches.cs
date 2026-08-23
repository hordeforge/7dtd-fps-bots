using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace BotMod.Patches
{
    // EAC-off LAN uses synthetic ids; client never finishes full EOS/Steam handshake, so any post-Steam authorizer (Eac, Crossplay, etc.) would stall loopback joins. Let loopback synthetic ids auto-pass all IAuthorizer chains after PlayerId/Basic checks. Generic patch covers every authorizer type that implements IAuthorizer.Authorize, not just Steam.
    // Gated by AllowSyntheticAuthBypass (default off): the range is predictable, so an
    // always-on bypass lets anyone join a server running this mod without owning the game.
    [HarmonyPatch(typeof(Platform.Steam.AuthenticationServer), "AuthenticateUser")]
    public static class Patch_SteamAuthServer_SyntheticBypass
    {
        static bool Prefix(ClientInfo _cInfo, ref Platform.EBeginUserAuthenticationResult __result)
        {
            try
            {
                if (_cInfo == null) return true;
                if (!ModApi.Config.AllowSyntheticAuthBypass) return true;
                var pid = _cInfo.PlatformId as Platform.Steam.UserIdentifierSteam;
                if (pid == null) return true;
                ulong sid = 0;
                try { sid = pid.SteamId; } catch { return true; }
                // Our synthetic range
                if (sid < 76561199000000000UL || sid > 76561199000010000UL) return true;
                __result = Platform.EBeginUserAuthenticationResult.Ok;
                BotMod.ModApi.Log("synthetic auth bypass for SteamId=" + sid + " ip=" + (_cInfo.ip ?? "?"));
                return false;
            }
            catch { }
            return true;
        }
    }

    // Generic authorizer bypass was too broad; keep only the concrete Steam auth server bypass above. AuthorizationManager dispatches sync+async; patching it generically interferes with normal flow.

    /// <summary>Server console lp/listplayers should also list [Bot] entries so operators see bots in the roster.</summary>
    [HarmonyPatch(typeof(ConsoleCmdListPlayers), "Execute")]
    public static class Patch_ListPlayers_Bots
    {
        static void Postfix(ConsoleCmdListPlayers __instance, List<string> _params, CommandSenderInfo _senderInfo)
        {
            try
            {
                var mgr = BotMod.Core.BotManager.Instance;
                if (mgr == null || mgr.BotCount == 0) return;
                var world = GameManager.Instance?.World;
                if (world == null) return;
                foreach (var bot in mgr.Bots)
                {
                    try
                    {
                        var ent = world.GetEntity(bot.EntityId) as EntityAlive;
                        if (ent == null) continue;
                        string pos = ent.GetPosition().ToString();
                        string line = $"[Bot] {bot.Name} id={bot.EntityId} pos={pos} health={ent.Health} deaths={ent.Died} zombies={ent.KilledZombies} players={ent.KilledPlayers} score={ent.Score} level={(ent.Progression!=null?ent.Progression.GetLevel():1)}";
                        SdtdConsole.Instance.Output(line);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    // team stamping removed: Entity.TeamNumber is not reliably on base Entity in this build; scoring uses explicit team=0 anyway.

    /// <summary>Every death re-fires the same scoreboard packet the XUi player list reads. Vanilla <c>AwardKill</c> only fires
    /// for <c>EntityPlayer</c> killers, so a bot's frag would never surface in the HUD. We push a best-effort stat refresh for a
    /// bot that just died so the HUD score column tracks. Clients ignore the packet for a zombie-typed id today, but this lays
    /// the deduplicated wiring for when the Tab source is patched to interleave bots.</summary>
    public static class BotScoreNet
    {
        /// <summary>Best effort: bump the stat's Changed flag so the normal 0.5s
        /// TickWait push (waitTicks=5) ships the updated stats to clients.</summary>
        public static void Refresh(EntityAlive who)
        {
            if (who == null) return;
            try { who.Stats?.Health?.SetChangedFlag(who.Health, who.Health - 1); } catch { }
        }
    }

    /// <summary>Deaths where a bot is killer or victim were previously invisible in the HUD score column because the
    /// vanilla score lane only fires for <c>EntityPlayer</c> killers. This postfix credits the right side directly on the shared
    /// <c>EntityAlive</c> counters (<c>KilledPlayers</c>/<c>KilledZombies</c>/<c>Died</c>/<c>Score</c>), and then nudges a stat
    /// refresh toward the scoreboard.
    /// NOTE: scoring for bot shooters is already handled in <see cref="BotCombat.OnKilled"/> which runs from <c>Bot.TryShootBurst</c>
    /// on the killing blow. This patch only handles the victim side and the rarer paths where the bot wasn't the direct DamageEntity
    /// caller (explosions, fall, zombie melee finishing a bot).</summary>
    [HarmonyPatch(typeof(EntityAlive), "OnEntityDeath")]
    public static class BotDeathPatch
    {
        static void Postfix(EntityAlive __instance)
        {
            try
            {
                if (__instance == null) return;
                if (!BotMod.Core.BotManager.Instance.IsBotEntity(__instance.entityId)) return;
                // Bot victims were already counted via Died prior to death; just nudge replication.
                try { BotScoreNet.Refresh(__instance); } catch { }
                BotMod.Core.BotManager.Instance.NotifyBotDeath(__instance.entityId);
                if (!ModApi.Config.DropLootOnDeath) try { __instance.lootList = null; } catch { }
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
                        // Squad mode, vsBot-off and same-team block bot-on-bot damage.
                        if (BotMod.Core.BotManager.Instance.IsBotEntity(__instance.entityId) && BotMod.Core.BotManager.Instance.AreAllies(attackerId, __instance.entityId)) return false;
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
                        if (victim != null) try { victim.OnDamaged(attacker, _strength); } catch { }
                    }
                }
            }
            catch { }
            return true;
        }
    }
}
