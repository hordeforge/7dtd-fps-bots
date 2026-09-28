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
        // The block LAN synthetic clients are issued. Fixed on purpose: it is the
        // set of ids the bypass recognizes, not a value derived from a client.
        internal const ulong SyntheticIdMin = 76561199000000000UL;
        internal const ulong SyntheticIdMax = 76561199000010000UL;

        static bool Prefix(ClientInfo _cInfo, ref Platform.EBeginUserAuthenticationResult __result)
        {
            try
            {
                if (_cInfo == null) return true;
                if (!ModApi.Config.AllowSyntheticAuthBypass) return true;
                var pid = _cInfo.PlatformId as Platform.Steam.UserIdentifierSteam;
                if (pid == null) return true;
                // No inner catch: a SteamId read that throws is a bypass check
                // failure like any other, and the outer handler below reports it
                // before falling through to vanilla auth. A local catch here
                // returned true with no trace, so the client was rejected with
                // the bypass silently stopped applying.
                ulong sid = pid.SteamId;
                // Our synthetic range
                if (sid < SyntheticIdMin || sid > SyntheticIdMax) return true;
                __result = Platform.EBeginUserAuthenticationResult.Ok;
                // Audit line names the connection's in-world entityId only. The
                // SteamId that got here is one of SyntheticIdMin..Max, a fixed
                // block, so its value carries no diagnostic information; the
                // client ip is personal data. Neither is needed to correlate the
                // bypassed join with the rest of the session: entityId is the key
                // `lp`, listplayers and every other log line already use.
                BotMod.ModApi.Log("synthetic auth bypass for client entityId=" + _cInfo.entityId);
                return false;
            }
            // A failure here silently reverts to vanilla auth: synthetic
            // clients would be rejected with no trace of why the configured
            // bypass stopped applying. Join-frequency bounded, so a plain
            // warn cannot flood (no rate gate: Time.time is unreliable off
            // the main thread and this runs on the network thread).
            catch (Exception ex)
            {
                BotMod.ModApi.Warn("synthetic auth bypass check failed, falling through to vanilla auth: " + ex);
            }
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
            var mgr = BotMod.Core.BotManager.Instance;
            if (mgr == null || mgr.BotCount == 0) return;
            var world = GameManager.Instance?.World;
            if (world == null) return;
            // A per-bot failure is reported instead of skipped: this is the
            // operator's roster, so a bot that throws here vanishes from `lp`
            // with no trace and reads as despawned. lp is operator-run, not
            // per-frame, so an unguarded Warn cannot flood.
            foreach (var bot in mgr.Bots)
            {
                try
                {
                    var ent = world.GetEntity(bot.EntityId) as EntityAlive;
                    if (ent == null) continue;
                    string pos = ent.GetPosition().ToString();
                    // bot.Name already carries the "[Bot] " tag (BotSpawner.PickName
                    // guarantees it); prefixing again printed "[Bot] [Bot] Grunt_42".
                    string line = $"{bot.Name} id={bot.EntityId} pos={pos} health={ent.Health} deaths={ent.Died} zombies={ent.KilledZombies} players={ent.KilledPlayers} score={ent.Score} level={(ent.Progression!=null?ent.Progression.GetLevel():1)}";
                    SdtdConsole.Instance.Output(line);
                }
                catch (Exception ex) { BotMod.ModApi.Warn("listplayers: bot id=" + bot.EntityId + " omitted from the roster: " + ex); }
            }
        }
    }

    // team stamping removed: Entity.TeamNumber is not reliably on base Entity in this build; scoring uses explicit team=0 anyway.

    /// <summary>Bot-victim death side effects: nudge a stat refresh so the HUD score
    /// column tracks (the vanilla lane only fires for <c>EntityPlayer</c> killers;
    /// bot-shooter scoring is handled in <see cref="BotCombat.OnKilled"/>), mark the
    /// manager's bookkeeping dead, and drop loot unless configured otherwise.</summary>
    [HarmonyPatch(typeof(EntityAlive), "OnEntityDeath")]
    public static class BotDeathPatch
    {
        static void Postfix(EntityAlive __instance)
        {
            try
            {
                if (__instance == null) return;
                if (!BotMod.Core.BotManager.Instance.IsBotEntity(__instance.entityId)) return;
                // Bot victims were already counted via Died prior to death; just nudge replication
                // (bump the stat's Changed flag so the 0.5s TickWait push ships it to clients).
                try { __instance.Stats?.Health?.SetChangedFlag(__instance.Health, __instance.Health - 1); }
                catch (Exception ex) { BotMod.ModApi.Warn("bot death stat refresh failed for entity " + __instance.entityId + " (clients keep a stale health/score column): " + ex); }
                BotMod.Core.BotManager.Instance.NotifyBotDeath(__instance.entityId);
                if (!ModApi.Config.DropLootOnDeath)
                {
                    try { __instance.lootList = null; }
                    catch (Exception ex) { BotMod.ModApi.Warn("loot drop for dead bot " + __instance.entityId + " failed (DropLootOnDeath=false): " + ex); }
                }
            }
            catch (Exception ex)
            {
                // Deaths are rare; a failing death side effect (bookkeeping,
                // loot drop) must stay visible.
                BotMod.ModApi.Warn("BotDeathPatch failed for entity " + (__instance != null ? __instance.entityId.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?") + ": " + ex);
            }
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
                    var cfg = ModApi.Config;
                    if (BotMod.Core.BotManager.Instance.IsBotEntity(attackerId))
                    {
                        // Class gates describe world bodies only; a bot victim's
                        // soldier body is an EntityZombie, so the vsZombie gate
                        // used to block bot-on-bot damage whenever it was off
                        // (shared rule: BotMod.Config.CombatGates). Bot victims
                        // answer to the ally check below alone.
                        bool victimIsBot = BotMod.Core.BotManager.Instance.IsBotEntity(__instance.entityId);
                        if (BotMod.Config.CombatGates.ClassGateBlocks(victimIsBot, __instance is EntityPlayer, __instance is EntityZombie, cfg.BotVsPlayer, cfg.BotVsZombie)) return false;
                        // Squad mode, vsBot-off and same-team block bot-on-bot damage.
                        if (victimIsBot && BotMod.Core.BotManager.Instance.AreAllies(attackerId, __instance.entityId)) return false;
                    }
                    // Route damage back to bot for FPS dodge/aggro swap (victim is a bot).
                    // No inner catch here: an OnDamaged failure must reach the
                    // outer rate-limited gate below instead of silently
                    // disabling dodging and aggro swaps (the gate names this
                    // source and counts repeats).
                    if (BotMod.Core.BotManager.Instance.IsBotEntity(__instance.entityId))
                    {
                        var world = GameManager.Instance?.World;
                        if (world != null)
                        {
                            var attacker = world.GetEntity(attackerId) as EntityAlive;
                            var victim = BotMod.Core.BotManager.Instance.GetBot(__instance.entityId);
                            if (victim != null) victim.OnDamaged(attacker, _strength);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // This hook runs on every DamageEntity call; if it starts
                // throwing persistently the BotVs*/team gating silently stops
                // applying (damage falls through as vanilla). Rate-limited so
                // a storm cannot flood the log while still naming the cause.
                // Lazy message: ex.ToString() walks the stack, so it must not
                // run per event while the gate suppresses.
                ModApi.WarnRateLimited(() => "DamageEntity filter failed: " + ex);
            }
            return true;
        }
    }
}
