// BotNeuralBrainEvalTests — numeric-correctness pins for the neural forward
// pass, complementing BotNeuralBrainFuzzTests (which pins robustness
// invariants: no-throw, finite outputs, load rejection). Here the weights
// are constructed so every expectation is hand-derivable from the documented
// contract (docs/research/01 §2/§4, tools/ga/ga.py INPUTS):
//
//   1. Input packing: one weight per probe makes the camp head answer "is
//      NeuralInputs field i nonzero?" — a field packed into the wrong scratch
//      slot leaves its own slot at zero and the probe fails.
//   2. Head math: sigmoid saturation (>8 -> exactly 1), the 0.5 decision
//      threshold behind WantCamp/WantRetreat/ShouldFire/StrafeDir, and the
//      tanh-bounded aim head with sign following the logit.
//   3. Determinism: identical inputs give bitwise-identical outputs (the
//      brain must stay a pure function; no hidden state across evals).
//
// Needs Newtonsoft.Json.dll from the game install (same gate as the neural
// weights fuzzer); compiles only BotNeuralBrain.cs plus a ModApi.ModPath
// stub. Run locally:
//
//   bash scripts/test-idempotency.sh
using System;
using System.Text;
using BotMod.AI;

namespace BotMod
{
    // Headless stand-in for the engine type BotNeuralBrain.TryLoad consults;
    // same shim BotNeuralBrainFuzzTests uses (separate exe, no collision).
    public class ModApi { public static string ModPath = ""; }
}

static class BotNeuralBrainEvalTests
{
    const int Inputs = 14;
    const int Hidden = 2;
    const int Outputs = 5;

    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    // Weights JSON for hidden=2, outputs=5. w1[h,i] selects which input slot
    // drives hidden unit h; w2[o,h] wires heads to hidden units.
    static string WeightsJson(float gainSlot, float[] w2RowValues)
    {
        var w = new StringBuilder();
        for (int h = 0; h < Hidden; h++)
            for (int i = 0; i < Inputs; i++)
                w.Append(h == 0 && i == gainSlot ? "3," : "0,");
        for (int h = 0; h < Hidden; h++) w.Append("0,");                    // b1
        for (int o = 0; o < Outputs; o++)
            for (int h = 0; h < Hidden; h++)
                w.Append(w2RowValues[o * Hidden + h]).Append(",");          // w2
        for (int o = 0; o < Outputs; o++) w.Append("0,");                   // b2
        return "{\"version\":1,\"inputs\":" + Inputs + ",\"hidden\":" + Hidden
            + ",\"outputs\":" + Outputs + ",\"weights\":["
            + w.ToString(0, w.Length - 1) + "]}";
    }

    static bool Load(string json, out string reason)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "botmod-neuraleval-" + Guid.NewGuid().ToString("N") + ".json");
        System.IO.File.WriteAllText(path, json);
        try { return BotNeuralBrain.TryLoad(path, out reason); }
        finally { try { System.IO.File.Delete(path); } catch (System.IO.IOException) { } }
    }

    static BotNeuralBrain.NeuralInputs In(int hotSlot, float value)
    {
        var inp = default(BotNeuralBrain.NeuralInputs);
        switch (hotSlot)
        {
            case 0: inp.HpFrac = value; break;
            case 1: inp.EnemyHpFrac = value; break;
            case 2: inp.DistNorm = value; break;
            case 3: inp.CanSee = value; break;
            case 4: inp.SpreadFrac = value; break;
            case 5: inp.WeaponRangeNorm = value; break;
            case 6: inp.PelletsNorm = value; break;
            case 7: inp.AimAcc = value; break;
            case 8: inp.AimSkill = value; break;
            case 9: inp.Aggression = value; break;
            case 10: inp.SelfPreservation = value; break;
            case 11: inp.Camper = value; break;
            case 12: inp.AmmoLeftFrac = value; break;
            case 13: inp.StuckFrac = value; break;
        }
        return inp;
    }

    static readonly string[] SlotNames =
    {
        "HpFrac", "EnemyHpFrac", "DistNorm", "CanSee", "SpreadFrac",
        "WeaponRangeNorm", "PelletsNorm", "AimAcc", "AimSkill", "Aggression",
        "SelfPreservation", "Camper", "AmmoLeftFrac", "StuckFrac"
    };

    static int Main()
    {
        Check("unloaded TryEval refuses to evaluate",
            !BotNeuralBrain.TryEval(default(BotNeuralBrain.NeuralInputs), out _) && !BotNeuralBrain.Loaded);
        Check("documented input count is frozen at 14", BotNeuralBrain.Inputs == Inputs);

        // 1. Packing probes: hidden0 reads exactly documented slot i, camp
        //    head reads hidden0 with a saturating weight. Field i set to +1
        //    must flip camp on; -1 must leave it off. A swapped field would
        //    energize the wrong column and fail both halves.
        {
            var campWired = new float[Outputs * Hidden];
            campWired[0] = 100f; // camp head reads hidden0
            string reason;
            bool packingOk = true;
            for (int i = 0; i < Inputs && packingOk; i++)
            {
                if (!Load(WeightsJson(i, campWired), out reason) || !BotNeuralBrain.Loaded)
                {
                    Console.WriteLine("     slot " + SlotNames[i] + ": load failed: " + reason);
                    packingOk = false;
                    break;
                }
                BotNeuralBrain.NeuralOutputs pos, neg, off;
                BotNeuralBrain.TryEval(In(i, 1f), out pos);
                BotNeuralBrain.TryEval(In(i, -1f), out neg);
                BotNeuralBrain.TryEval(In((i + 1) % Inputs, 1f), out off);
                if (!pos.WantCamp || neg.WantCamp || off.WantCamp)
                {
                    Console.WriteLine("     slot " + SlotNames[i] + ": pos=" + pos.WantCamp
                        + " neg=" + neg.WantCamp + " neighbor=" + off.WantCamp);
                    packingOk = false;
                }
            }
            Check("every input field lands in its documented scratch slot", packingOk);
        }

        // 2. Head semantics: one-hot on HpFrac (slot 0), each head probed by
        //    its w2 row. hidden0 = tanh(3) ~ 0.995 when HpFrac = 1.
        {
            string reason;
            var w2 = new float[Outputs * Hidden];
            w2[0 * Hidden + 0] = 100f; // camp: saturating positive
            w2[1 * Hidden + 0] = -100f; // retreat: saturating negative
            w2[2 * Hidden + 0] = 0.5f; // aim: mid-range logit, tanh-bounded
            w2[3 * Hidden + 0] = 100f; // fire: saturating positive
            w2[4 * Hidden + 0] = -0.1f; // strafe: just below threshold
            Check("probe weights load", Load(WeightsJson(0, w2), out reason));

            BotNeuralBrain.NeuralOutputs o;
            BotNeuralBrain.TryEval(In(0, 1f), out o);
            Check("saturating camp logit is exactly 1 and wants camp",
                o.CampLogit == 1f && o.WantCamp);
            Check("negative retreat logit is exactly 0 and refuses retreat",
                o.RetreatLogit == 0f && !o.WantRetreat);
            Check("aim bias stays inside (-1,1) with the logit's sign",
                o.AimBiasYaw > 0f && o.AimBiasYaw < 1f);
            Check("fire gate saturates to should-fire", o.ShouldFire && o.FireGate == 1f);
            Check("strafe just below the 0.5 threshold picks dir -1",
                o.StrafeDir == -1 && o.StrafeLogit > 0.45f && o.StrafeLogit < 0.5f);

            // Flip the small strafe weight positive: crosses 0.5 -> dir +1.
            w2[4 * Hidden + 0] = 0.1f;
            w2[3 * Hidden + 0] = -100f;
            Check("threshold-crossing probe loads", Load(WeightsJson(0, w2), out reason));
            BotNeuralBrain.TryEval(In(0, 1f), out o);
            Check("strafe just above the 0.5 threshold picks dir +1",
                o.StrafeDir == 1 && o.StrafeLogit > 0.5f && o.StrafeLogit < 0.55f);
            Check("saturating negative fire gate refuses to fire",
                !o.ShouldFire && o.FireGate == 0f);

            // Zero input: hidden units sit at tanh(0)=0, all gates at their
            // 0.5 boundary resolve false (logits are exactly 0).
            BotNeuralBrain.TryEval(In(-1, 0f), out o);
            Check("all-zero input resolves every decision to false / dir -1",
                !o.WantCamp && !o.WantRetreat && !o.ShouldFire && o.StrafeDir == -1
                && o.CampLogit == 0.5f && o.AimBiasYaw == 0f);
        }

        // 3. Purity: the same inputs twice give bitwise-equal outputs.
        {
            string reason;
            var live = new float[Outputs * Hidden];
            live[0] = 100f;        // camp head
            live[2 * Hidden + 0] = 0.5f; // aim head
            Check("determinism probe loads", Load(WeightsJson(7, live), out reason));
            var inp = In(7, 0.75f);
            BotNeuralBrain.NeuralOutputs a, b;
            bool e1 = BotNeuralBrain.TryEval(inp, out a);
            bool e2 = BotNeuralBrain.TryEval(inp, out b);
            Check("repeated evaluation is bitwise deterministic",
                e1 && e2
                && a.CampLogit == b.CampLogit && a.RetreatLogit == b.RetreatLogit
                && a.AimBiasYaw == b.AimBiasYaw && a.FireGate == b.FireGate
                && a.StrafeLogit == b.StrafeLogit && a.StrafeDir == b.StrafeDir
                && a.WantCamp == b.WantCamp && a.WantRetreat == b.WantRetreat
                && a.ShouldFire == b.ShouldFire);
        }

        if (_failures == 0) { Console.WriteLine("all bot neural eval tests passed"); return 0; }
        Console.WriteLine(_failures + " bot neural eval test(s) FAILED");
        return 1;
    }
}
