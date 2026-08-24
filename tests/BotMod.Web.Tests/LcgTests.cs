// LcgTests — pins the deterministic 32-bit LCG that every random decision in
// the mod rides (per-bot combat rolls, spawn picks, mixed-loadout counter,
// wander hash) and whose tap constants are mirrored BY HAND in tools/ga
// (combat_sim.py, replay.py) to keep the GA simulation faithful to in-game
// behavior. A changed multiplier, increment, shift or mask would not throw
// anywhere: bots would simply stop matching the simulated ones. The suite
// therefore pins exact state sequences derived independently from the
// documented formula (state -> state*1103515245+12345 mod 2^32, top 24 bits
// as the 0..1 float) plus the documented boundary behavior of Index/Range,
// struct stream-independence, and range containment over large sweeps.
//
// Pure BCL; compiled and run by scripts/test-idempotency.sh:
//
//   bash scripts/test-idempotency.sh
using System;
using BotMod.Config;

static class LcgTests
{
    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    static int Main()
    {
        // 1. Exact state sequence for seed 0 (first step is the increment
        //    alone). Values derived from the formula by hand, NOT by calling
        //    Lcg: they also pin the Python mirror's expectations.
        {
            var r = Lcg.Seeded(0u);
            Check("seed 0 sequence matches the documented taps",
                r.Next() == 12345u && r.Next() == 3554416254u && r.Next() == 2802067423u
                && r.Next() == 3596950572u && r.Next() == 229283573u && r.Next() == 3256818826u);
        }

        // 2. Top-24-bits extraction: seed 0's first draw is (12345 >> 8) /
        //    2^24 = 48/16777216 exactly representable in float.
        {
            float v = Lcg.Seeded(0u).Next01();
            Check("Next01 takes top 24 bits", v == 48f / 16777216f);
        }

        // 3. Seed sensitivity and a non-trivial Next01 sequence (seed
        //    0xDEADBEEF, values derived from the formula).
        {
            Check("seed 1 first state differs from seed 0", Lcg.Seeded(1u).Next() == 1103527590u);
            var r = Lcg.Seeded(0xDEADBEEFu);
            bool seqOk = true;
            foreach (float expected in new[] { 0.1093948483467102f, 0.6219661235809326f, 0.4170938730239868f })
                if (r.Next01() != expected) { seqOk = false; break; }
            Check("seeded Next01 sequence matches the documented taps", seqOk);
        }

        // 4. Struct streams are independent: copying an Lcg snapshots the
        //    state; advancing one must never advance the other (holders own
        //    their stream, no shared mutable state).
        {
            var a = Lcg.Seeded(42u);
            var b = a;
            a.Next();
            Check("copied stream keeps its own state", b.Next() == Lcg.Seeded(42u).Next());
        }

        // 5. Determinism: two streams seeded alike produce identical
        //    sequences (no hidden global state).
        {
            var a = Lcg.Seeded(1234u);
            var b = Lcg.Seeded(1234u);
            bool same = true;
            for (int i = 0; i < 64; i++) if (a.Next01() != b.Next01()) { same = false; break; }
            Check("same seed gives identical sequence", same);
        }

        // 6. Containment sweeps: Next01 in [0,1), NextSymmetric in [-1,1),
        //    across many draws and seeds.
        {
            var r = Lcg.Seeded(0xC0FFEEu);
            bool ok01 = true, okSym = true;
            for (int i = 0; i < 200000; i++)
            {
                float v = r.Next01();
                if (v < 0f || v >= 1f || float.IsNaN(v)) { ok01 = false; break; }
                float s = r.NextSymmetric();
                if (s < -1f || s >= 1f || float.IsNaN(s)) { okSym = false; break; }
            }
            Check("200k Next01 draws stay in [0,1)", ok01);
            Check("200k NextSymmetric draws stay in [-1,1)", okSym);
        }

        // 7. Index boundaries: count <= 0 yields 0 (documented), count 1 can
        //    only yield 0, and every draw lands in [0, count).
        {
            var r = Lcg.Seeded(7u);
            Check("Index(count <= 0) yields 0", r.Index(0) == 0 && r.Index(-5) == 0);
            bool oneHot = true, contained = true;
            for (int i = 0; i < 10000; i++)
            {
                if (r.Index(1) != 0) oneHot = false;
                int v = r.Index(13);
                if (v < 0 || v >= 13) { contained = false; break; }
            }
            Check("Index(1) always yields 0", oneHot);
            Check("Index(13) stays inside [0,13)", contained);
        }

        // 8. Range boundaries: hi <= lo yields lo (documented), and every
        //    draw lands in [lo, hi) including the negative-lo window.
        {
            var r = Lcg.Seeded(99u);
            Check("Range(hi <= lo) yields lo", r.Range(4, 4) == 4 && r.Range(6, 2) == 6 && r.Range(-3, -3) == -3);
            bool contained = true;
            for (int i = 0; i < 10000; i++)
            {
                int v = r.Range(-7, 19);
                if (v < -7 || v >= 19) { contained = false; break; }
            }
            Check("Range(-7,19) stays inside [-7,19)", contained);
        }

        if (_failures == 0) { Console.WriteLine("all lcg tests passed"); return 0; }
        Console.WriteLine(_failures + " lcg test(s) FAILED");
        return 1;
    }
}
