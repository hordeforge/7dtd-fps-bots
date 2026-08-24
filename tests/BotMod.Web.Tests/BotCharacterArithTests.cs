// BotCharacterArithTests — numeric-correctness pins for characters.json
// ingestion (BotCharacter.Normalize), complementing BotConfigFuzzTests which
// covers botmod.json ranges.
//
// Why: characters.json is operator-authored hand-edited text, and Newtonsoft
// parses bare NaN/Infinity/-Infinity number literals straight into float
// properties (verified against the game's bundled Newtonsoft.Json.dll; note
// overflowing literals like 1e400 instead throw JsonReaderException and fail
// the whole-file load in BotCharacterDB). These floats feed the neural
// observation vector (slots 7/8/9/10/11), the per-engagement aim-bias window
// ((1-AimAccuracy)*0.45 rotated into the aim direction every shot) and the
// camp/retreat gates. Before Normalize existed, one NaN literal poisoned the
// whole neural forward pass (every sigmoid/tanh of NaN stays NaN, so
// ShouldFire went permanently false) and made the aim rotation NaN.
//
// Needs Newtonsoft.Json.dll from the game install (same gate as the neural
// suites); compiles BotCharacter.cs + BotText.cs plus a ModApi.Warn stub.
// Run locally: bash scripts/test-idempotency.sh
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using BotMod.Config;

namespace BotMod
{
    // Headless stand-in for the engine type BotCharacterDB.Load consults;
    // same shim the neural/config suites use (separate exe, no collision).
    public class ModApi { public static void Warn(string msg) { } }
}

static class BotCharacterArithTests
{
    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    static BotCharacter Parse(string json)
    {
        var ch = JsonConvert.DeserializeObject<BotCharacter>(json);
        ch.Normalize();
        return ch;
    }

    // The consumer math Bot.AdoptTarget / AttackInRange run per engagement.
    static float AimBiasWindow(float acc) => Math.Max(0.03f, (1f - acc) * 0.45f);

    static int Main()
    {
        // NaN literals parse cleanly into float properties and must come out
        // as the documented defaults.
        var nan = Parse("{\"Camper\": NaN, \"AimAccuracy\": NaN, \"Aggression\": NaN}");
        Check("NaN camper -> default 0.2", nan.Camper == 0.2f);
        Check("NaN aim accuracy -> default 0.75", nan.AimAccuracy == 0.75f);
        Check("NaN aggression -> default 0.6", nan.Aggression == 0.6f);

        // Bare Infinity literals also parse cleanly; non-finite values fall
        // back to the documented defaults rather than saturating at the
        // clamp edges (same convention as CampHashGate's non-finite camper).
        // (Overflowing decimal literals like 1e400 throw instead, failing
        // the whole-file load in BotCharacterDB - not tested here.)
        var inf = Parse("{\"Aggression\": Infinity, \"SelfPreservation\": -Infinity}");
        Check("Infinity aggression -> default 0.6", inf.Aggression == 0.6f && !float.IsInfinity(inf.Aggression));
        Check("-Infinity self-preservation -> default 0.5", inf.SelfPreservation == 0.5f && !float.IsInfinity(inf.SelfPreservation));

        // Out-of-range finite values clamp into [0,1].
        var range = Parse("{\"AimSkill\": 5.0, \"Camper\": -3.0, \"FireThrottle\": 2.5}");
        Check("aim skill 5 -> 1", range.AimSkill == 1f);
        Check("camper -3 -> 0", range.Camper == 0f);
        Check("fire throttle 2.5 -> 1", range.FireThrottle == 1f);

        // Magnitude traits: non-positive or non-finite fall back to defaults.
        var mag = Parse("{\"ViewMaxChange\": -50, \"ReactionTime\": 0, \"ViewFactor\": NaN}");
        Check("negative view max change -> default 600", mag.ViewMaxChange == 600f);
        Check("zero reaction time -> default 0.35", mag.ReactionTime == 0.35f);
        Check("NaN view factor -> default 0.35", mag.ViewFactor == 0.35f);

        // Per-weapon tables sanitize entry by entry (same trust boundary).
        var wpn = Parse("{\"AimAccuracyWeapon\": {\"gunMGT1AK47\": NaN, \"gunShotgunT1DoubleBarrel\": 9}}");
        Check("per-weapon NaN accuracy -> default", wpn.AimAccuracyWeapon["gunMGT1AK47"] == 0.75f);
        Check("per-weapon out-of-range accuracy -> 1", wpn.AimAccuracyWeapon["gunShotgunT1DoubleBarrel"] == 1f);

        // Sane hand-tuned files survive Normalize bit-for-bit.
        var sane = new BotCharacter { Name = "Kíra", AimAccuracy = 0.83f, Camper = 0.65f, ReactionTime = 0.31f };
        sane.Normalize();
        Check("in-range values unchanged", sane.AimAccuracy == 0.83f && sane.Camper == 0.65f && sane.ReactionTime == 0.31f);

        // End-to-end: the aim-bias window computed from any ingested value is
        // finite and inside its documented [0.03, 0.45] band (NaN input used
        // to make this NaN and rotate aim by NaN every engagement tick).
        foreach (var ch in new[] { nan, inf, range, mag })
        {
            float w = AimBiasWindow(ch.AimAccuracy);
            bool ok = !float.IsNaN(w) && !float.IsInfinity(w) && w >= 0.03f && w <= 0.45f;
            Check("aim window finite in [0.03,0.45] for acc=" + ch.AimAccuracy, ok);
        }

        if (_failures == 0) { Console.WriteLine("all bot character arithmetic tests passed"); return 0; }
        Console.WriteLine(_failures + " bot character arithmetic tests FAILED");
        return 1;
    }
}
