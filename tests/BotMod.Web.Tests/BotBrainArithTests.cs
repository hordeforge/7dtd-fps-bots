// BotBrainArithTests — pins the arithmetic of the idle-camper hash gate.
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

        if (_failures == 0) { Console.WriteLine("all bot brain arithmetic tests passed"); return 0; }
        Console.WriteLine(_failures + " bot brain arithmetic tests FAILED");
        return 1;
    }

    static double PassRate(int lo, int hi, float camper)
    {
        int hits = 0, n = 0;
        for (int id = lo; id <= hi; id++) { n++; if (BotBrain.CampHashGate(id, camper)) hits++; }
        return (double)hits / n;
    }

    static string Pct(double v) => Math.Round(v * 100.0, 1) + "%";
}
