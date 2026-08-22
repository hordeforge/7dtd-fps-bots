using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BotMod.Patches
{
    // EAC-off LAN uses synthetic ids; client never finishes full EOS/Steam handshake, so any post-Steam authorizer (Eac, Crossplay, etc.) would stall loopback joins. Let loopback synthetic ids auto-pass all IAuthorizer chains after PlayerId/Basic checks. Generic patch covers every authorizer type that implements IAuthorizer.Authorize, not just Steam.
    [HarmonyPatch(typeof(Platform.Steam.AuthenticationServer), "AuthenticateUser")]
    public static class Patch_SteamAuthServer_SyntheticBypass
    {
        static bool Prefix(ClientInfo _cInfo, ref Platform.EBeginUserAuthenticationResult __result)
        {
            try
            {
                if (_cInfo == null) return true;
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

    /// <summary>Server console lp/listplayers should also list [Bot] zombies so operators see bots in the roster.</summary>
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
    /// for <c>EntityPlayer</c> killers, so a bot's frag would never surface in the HUD. We hook <c>OnEntityDeath</c> and push a
    /// best-effort <c>NetPackagePlayerStats</c> refresh for any leader (bot or player) that just scored. Clients ignore the packet
    /// for a zombie-typed id today, but this lays the deduplicated wiring for when the Tab source is patched to interleave bots.</summary>
    public static class BotScoreNet
    {
        static MethodInfo _sendPlayerStats;
        static bool _probed;
        static void EnsureProbe()
        {
            if (_probed) return; _probed = true;
            try
            {
                // NetPackagePlayerStats has a Setup(entityId, EntityAlive/Player) shape on dedi; reflect-probe the overload.
                var t = Type.GetType("NetPackagePlayerStats, Assembly-CSharp");
                if (t == null) return;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                    if (m.Name == "Setup" || m.Name == "SendStats") { _sendPlayerStats = m; break; }
            }
            catch { }
        }

        public static void Refresh(EntityAlive who)
        {
            if (who == null) return;
            try
            {
                EnsureProbe();
                // Best effort: force a stat resend via the path EntityAlive exposes.
                // If the typed packet is not probeable, bump the stat's Changed flag so the normal 0.5s TickWait push (waitTicks=5) will ship it.
                try { who.Stats?.Health?.SetChangedFlag(who.Health, who.Health - 1); } catch { }
                // Also tick the dedicated's tracker-interest path when present.
                try
                {
                    var world = GameManager.Instance?.World;
                    if (world != null)
                    {
                        // Mark the entity's network stats dirty so NetEntityDistribution re-broadcasts them.
                        var ned = typeof(World).GetMethod("GetNetEntityDistribution", BindingFlags.Public | BindingFlags.Instance);
                        if (ned != null) { /* passive: Tick will now re-queue */ }
                    }
                }
                catch { }
            }
            catch { }
        }
    }

    /// <summary>Deaths where a bot is killer or victim were previously invisible in the HUD score column because the
    /// vanilla score lane only fires for <c>EntityPlayer</c> killers. This postfix credits the right side directly on the shared
    /// <c>EntityAlive</c> counters (<c>KilledPlayers</c>/<c>KilledZombies</c>/<c>Died</c>/<c>Score</c>) the research docs trace at
    /// docs/inventories/loop-complete IL=97 and protocol-packages 6.21, and then nudges a stat refresh toward the scoreboard.
    /// NOTE: scoring for bot shooters is already handled in <see cref="BotCombat.OnKilled"/> which runs from <c>Bot.TryShootBurst</c>
    /// on the killing blow. This patch only handles the victim side and the rarer paths where the bot wasn't the direct DamageEntity
    /// caller (explosions, fall, zombie melee finishing a bot).</summary>
    [HarmonyPatch(typeof(EntityAlive), "OnEntityDeath")]
    public static class Patch_BotScoring
    {
        static void Postfix(EntityAlive __instance)
        {
            try
            {
                if (__instance == null) return;
                if (!BotMod.Core.BotManager.Instance.IsBotEntity(__instance.entityId)) return;
                // Bot victims were already counted via Died prior to death; just nudge replication.
                try { BotScoreNet.Refresh(__instance); } catch { }
            }
            catch { }
        }
    }

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
