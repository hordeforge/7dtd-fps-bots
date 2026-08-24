// CombatGatesTests — pins the shared vs-class combat gate: the BotVs* toggles
// describe world bodies only, and a bot victim (spawned as zombieSoldier, an
// EntityZombie subclass) is exempt from both class gates because bot-vs-bot
// answers to the ally rule alone. Regression for the configuration where
// "bot vs zombie off" with BotVsBot on silently disabled every bot-vs-bot
// engagement in IsValidTarget and the DamageEntity patch.
// Pure BCL; compiled and run by scripts/test-idempotency.sh.
//
//   bash scripts/test-idempotency.sh
using System;
using BotMod.Config;

static class CombatGatesTests
{
    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    static int Main()
    {
        // 1. Bot victims never trip a class gate: body class is an engine
        //    detail (zombieSoldier bodies are EntityZombie), so vsZombie=false
        //    must not block bot-on-bot engagement.
        Check("bot victim in zombie body not blocked when vsZombie off",
            !CombatGates.ClassGateBlocks(victimIsBot: true, victimIsPlayerBody: false, victimIsZombieBody: true, vsPlayers: true, vsZombies: false));
        Check("bot victim in player body not blocked when vsPlayer off",
            !CombatGates.ClassGateBlocks(victimIsBot: true, victimIsPlayerBody: true, victimIsZombieBody: false, vsPlayers: false, vsZombies: true));
        Check("bot victim never blocked even with every toggle off",
            !CombatGates.ClassGateBlocks(victimIsBot: true, victimIsPlayerBody: true, victimIsZombieBody: true, vsPlayers: false, vsZombies: false));

        // 2. World bodies still answer to their own class gate.
        Check("player body blocked when vsPlayer off",
            CombatGates.ClassGateBlocks(victimIsBot: false, victimIsPlayerBody: true, victimIsZombieBody: false, vsPlayers: false, vsZombies: true));
        Check("zombie body blocked when vsZombie off",
            CombatGates.ClassGateBlocks(victimIsBot: false, victimIsPlayerBody: false, victimIsZombieBody: true, vsPlayers: true, vsZombies: false));
        Check("zombie body not blocked when vsZombie on",
            !CombatGates.ClassGateBlocks(victimIsBot: false, victimIsPlayerBody: false, victimIsZombieBody: true, vsPlayers: false, vsZombies: true));

        // 3. All toggles on (FFA default): nothing is class-blocked.
        Check("all toggles on block nothing",
            !CombatGates.ClassGateBlocks(false, true, true, vsPlayers: true, vsZombies: true) &&
            !CombatGates.ClassGateBlocks(false, true, false, vsPlayers: true, vsZombies: true) &&
            !CombatGates.ClassGateBlocks(false, false, true, vsPlayers: true, vsZombies: true));

        // 4. Exhaustive bot-victim exemption: for every body-class and toggle
        //    combination, a bot victim is never class-blocked (the ally rule
        //    answers on top). Guards against a future early-return reorder
        //    that re-exposes the regression in some combo the samples above
        //    do not name.
        {
            bool exempt = true;
            foreach (bool playerBody in new[] { false, true })
                foreach (bool zombieBody in new[] { false, true })
                    foreach (bool vsPlayers in new[] { false, true })
                        foreach (bool vsZombies in new[] { false, true })
                            if (CombatGates.ClassGateBlocks(true, playerBody, zombieBody, vsPlayers, vsZombies))
                                exempt = false;
            Check("bot victim exempt in every one of the 16 toggle combos", exempt);
        }

        if (_failures > 0)
        {
            Console.WriteLine(_failures + " combat gates test(s) failed");
            return 1;
        }
        Console.WriteLine("all combat gates tests passed");
        return 0;
    }
}
