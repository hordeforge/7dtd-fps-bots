// BotBrainArithTests: pins the arithmetic of the idle-camper hash gate.
//
// Why: C# promotes int*uint to long and % keeps the dividend's sign, so the
// original inline form ((me.entityId * 2654435761u) % 100 < (uint)(Camper*12))
// produced a negative remainder for every negative entity id (fallback spawn
// classes yield those on this dedi build) and compared true against the
// unsigned threshold unconditionally: every such bot camped every idle tick
// instead of rolling Camper*12 percent of the time.
//
// BotBrain references engine types, so this compiles the FULL mod source
// against the game DLLs; scripts/test-idempotency.sh gates it on a game
// install being present.
using System;
using BotMod.AI;

static class BotBrainArithTests
{
    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    static int Main()
    {
        const float fullCamper = 1f; // threshold = 12 of 100 hash buckets

        // The regression input: negative ids used to compare true unconditionally.
        bool anyNegativeMiss = false;
        for (int id = -5000; id <= -1; id++)
            if (!BotBrain.CampHashGate(id, fullCamper)) { anyNegativeMiss = true; break; }
        Check("negative entity ids can miss the gate (sign bug gone)", anyNegativeMiss);

        bool anyPositiveMiss = false;
        for (int id = 1; id <= 5000; id++)
            if (!BotBrain.CampHashGate(id, fullCamper)) { anyPositiveMiss = true; break; }
        Check("positive entity ids can miss the gate", anyPositiveMiss);

        // Pass rate tracks Camper*12 percent on both sides of zero.
        double negRate = PassRate(-5000, -1, fullCamper);
        double posRate = PassRate(1, 5000, fullCamper);
        Check("negative-id pass rate near 12% (" + Pct(negRate) + ")", negRate > 0.08 && negRate < 0.16);
        Check("positive-id pass rate near 12% (" + Pct(posRate) + ")", posRate > 0.08 && posRate < 0.16);

        double halfNeg = PassRate(-5000, -1, 0.5f);
        Check("half-camper pass rate near 6% (" + Pct(halfNeg) + ")", halfNeg > 0.03 && halfNeg < 0.09);

        // Determinism: same input, same roll (no wall-clock or RNG state).
        Check("gate is deterministic", BotBrain.CampHashGate(-1234, fullCamper) == BotBrain.CampHashGate(-1234, fullCamper)
            && BotBrain.CampHashGate(42, fullCamper) == BotBrain.CampHashGate(42, fullCamper));

        // Degenerate characteristic values stay total.
        Check("zero camper never camps", !BotBrain.CampHashGate(7, 0f));
        Check("negative camper never camps", !BotBrain.CampHashGate(7, -0.5f));
        Check("NaN camper never camps", !BotBrain.CampHashGate(7, float.NaN));
        Check("infinite camper never hangs", !BotBrain.CampHashGate(7, float.PositiveInfinity));
        Check("huge camper saturates to always", BotBrain.CampHashGate(7, 100f));

        CheckScoreBound();

        if (_failures == 0) { Console.WriteLine("all bot brain arithmetic tests passed"); return 0; }
        Console.WriteLine(_failures + " bot brain arithmetic tests FAILED");
        return 1;
    }

    // FindTarget prunes a candidate's line-of-sight raycast when its
    // distance-only lower bound already loses to the incumbent. The pruning is
    // only sound if that bound really is a lower bound on every score the
    // scoring code can produce, so re-derive the scores here over a spread of
    // distances, health fractions and class combinations and assert the
    // selection contract directly: a pruned candidate never scores below the
    // incumbent, so the pick is identical with or without the prune.
    static void CheckScoreBound()
    {
        const float grudge = 0.6f;   // BotBrain.GrudgeBias
        const float hpBonus = 6f;
        const float playerMult = 0.82f;
        const float botMult = 0.9f;
        const float minMult = playerMult * botMult; // BotBrain.MinScoreMult
        const float maxDist = 300f;                 // BotConfig.VisionRange ceiling

        // Every class/role combination the three FindTarget passes can score:
        // pass 1 (bounds box) applies the class multipliers, pass 2 (players)
        // applies 0.82, pass 3 (all EntityAlives) applies none.
        var multipliers = new[] { 1f, playerMult, botMult, playerMult * botMult };

        bool sound = true, prunes = false;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int mi = 0; mi < multipliers.Length; mi++)
            {
                float mult = pass == 0 ? multipliers[mi] : 1f;
                float boundMult = pass == 0 ? minMult : 1f;
                for (float dist = 0f; dist <= maxDist; dist += 0.25f)
                {
                    for (float hpFrac = 0f; hpFrac <= 1f; hpFrac += 0.25f)
                    {
                        float score = dist * mult + hpFrac * hpBonus;
                        // A far-away full-HP body vs a wounded near one: the
                        // incumbent the bound is compared against.
                        float incumbent = dist * 0.5f + 6f;
                        for (int role = 0; role < 2; role++)
                        {
                            bool preferred = role == 1;
                            bool pruned = BotBrain.CannotBeat(7, preferred ? 7 : 99, dist, incumbent, boundMult);
                            // The pick is strict (score < bestScore), so a tie
                            // keeps the incumbent and pruning is free.
                            float finalScore = preferred ? score * grudge : score;
                            if (pruned && finalScore < incumbent) sound = false;
                            if (pruned) prunes = true;
                        }
                    }
                }
            }
        }
        Check("a pruned candidate never out-scores the incumbent", sound);
        Check("far candidates are actually pruned", prunes);
        // The first candidate faces an empty incumbent: nothing may be pruned,
        // or FindTarget would return no target on a clear field.
        bool keepsFirst = !BotBrain.CannotBeat(7, -1, 0.01f, float.MaxValue, minMult)
                       && !BotBrain.CannotBeat(7, 7, 0.01f, float.MaxValue, minMult)
                       && !BotBrain.CannotBeat(7, 7, 0.01f, float.MaxValue, 1f);
        Check("nothing is pruned against an empty incumbent", keepsFirst);
    }

    static double PassRate(int lo, int hi, float camper)
    {
        int hits = 0, n = 0;
        for (int id = lo; id <= hi; id++) { n++; if (BotBrain.CampHashGate(id, camper)) hits++; }
        return (double)hits / n;
    }

    static string Pct(double v) => Math.Round(v * 100.0, 1) + "%";
}
