// BotNeuralBrainFuzzTests: fuzzing of the evolved/best.json weights-file
// parser plus the forward pass behind it. TryLoad parses externally supplied
// JSON reachable from mod startup, the admin web API ("neural") and console,
// and its output drives every bot tick once loaded. Mutants (byte-level and
// structure-aware JSON tampering) must either fail cleanly with a non-empty
// reason and leave the brain unloaded, or load into a state where TryEval is
// finite, bounded and consistent for any sane observation. Includes targeted
// regressions for the frozen v1 dimension contract (inputs must be exactly
// 14 and outputs exactly 5: TryEval packs those input slots into fixed
// scratch buffers and reads exactly those five action heads by index).
//
// Needs Newtonsoft.Json.dll from the game install; scripts/test-idempotency.sh
// probes for it and skips this suite when absent. Run locally:
//
//   bash scripts/test-idempotency.sh
using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

static class BotNeuralBrainFuzzTests
{
    const int Mutants = 500;
    const int kExpectedInputs = 14;

    static int _failures;

    static void Check(bool ok, string detail)
    {
        if (!ok)
        {
            _failures++;
            Console.WriteLine("FAIL " + detail);
        }
    }

    static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "botmod-neuralfuzz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- observations ----

    static BotMod.AI.BotNeuralBrain.NeuralInputs SaneInputs(Random rng)
    {
        Func<float> r = delegate { return (float)(rng.NextDouble() * 4.0 - 2.0); };
        return new BotMod.AI.BotNeuralBrain.NeuralInputs
        {
            HpFrac = r(), EnemyHpFrac = r(), DistNorm = r(), CanSee = r(),
            SpreadFrac = r(), WeaponRangeNorm = r(), PelletsNorm = r(), AimAcc = r(),
            AimSkill = r(), Aggression = r(), SelfPreservation = r(), Camper = r(),
            AmmoLeftFrac = r(), StuckFrac = r()
        };
    }

    static BotMod.AI.BotNeuralBrain.NeuralInputs ExtremeInputs(float v)
    {
        return new BotMod.AI.BotNeuralBrain.NeuralInputs
        {
            HpFrac = v, EnemyHpFrac = v, DistNorm = v, CanSee = v,
            SpreadFrac = v, WeaponRangeNorm = v, PelletsNorm = v, AimAcc = v,
            AimSkill = v, Aggression = v, SelfPreservation = v, Camper = v,
            AmmoLeftFrac = v, StuckFrac = v
        };
    }

    /// <summary>When a model reports loaded, every sane evaluation must be
    /// finite, within the activation ranges, and internally consistent.</summary>
    static void CheckEvalContract(Random rng, string ctx)
    {
        for (int i = 0; i < 5; i++)
        {
            var inp = SaneInputs(rng);
            BotMod.AI.BotNeuralBrain.NeuralOutputs o;
            bool evaluated = false;
            try { evaluated = BotMod.AI.BotNeuralBrain.TryEval(inp, out o); }
            catch (Exception ex) { Check(false, ctx + ": TryEval threw: " + ex.GetType().Name); return; }
            Check(evaluated, ctx + ": loaded brain failed to evaluate");
            if (!evaluated) return;
            bool finite =
                !float.IsNaN(o.CampLogit) && !float.IsInfinity(o.CampLogit)
                && !float.IsNaN(o.RetreatLogit) && !float.IsInfinity(o.RetreatLogit)
                && !float.IsNaN(o.FireGate) && !float.IsInfinity(o.FireGate)
                && !float.IsNaN(o.StrafeLogit) && !float.IsInfinity(o.StrafeLogit)
                && !float.IsNaN(o.AimBiasYaw) && !float.IsInfinity(o.AimBiasYaw);
            Check(finite, ctx + ": non-finite outputs from finite inputs");
            Check(o.CampLogit >= -0.001f && o.CampLogit <= 1.001f, ctx + ": camp logit out of sigmoid range: " + o.CampLogit);
            Check(o.RetreatLogit >= -0.001f && o.RetreatLogit <= 1.001f, ctx + ": retreat logit out of range: " + o.RetreatLogit);
            Check(o.FireGate >= -0.001f && o.FireGate <= 1.001f, ctx + ": fire gate out of range: " + o.FireGate);
            Check(o.StrafeLogit >= -0.001f && o.StrafeLogit <= 1.001f, ctx + ": strafe logit out of range: " + o.StrafeLogit);
            Check(o.AimBiasYaw >= -1.001f && o.AimBiasYaw <= 1.001f, ctx + ": aim bias out of tanh range: " + o.AimBiasYaw);
            Check(o.StrafeDir == -1 || o.StrafeDir == 1, ctx + ": strafe dir not +/-1: " + o.StrafeDir);
            Check(o.WantCamp == (o.CampLogit > 0.5f), ctx + ": WantCamp inconsistent with logit");
            Check(o.WantRetreat == (o.RetreatLogit > 0.5f), ctx + ": WantRetreat inconsistent with logit");
            Check(o.ShouldFire == (o.FireGate > 0.5f), ctx + ": ShouldFire inconsistent with gate");
        }
        // Pathological observations must never throw; output quality is not
        // constrained there (garbage in may yield garbage out, safely).
        float[] pathological = { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1e30f, -1e30f };
        foreach (float v in pathological)
        {
            try
            {
                BotMod.AI.BotNeuralBrain.NeuralOutputs o;
                BotMod.AI.BotNeuralBrain.TryEval(ExtremeInputs(v), out o);
            }
            catch (Exception ex) { Check(false, ctx + ": TryEval threw on input " + v + ": " + ex.GetType().Name); }
        }
    }

    /// <summary>Load a mutant file and pin the full post-load state contract.</summary>
    static void FuzzLoad(string path, Random rng, string ctx)
    {
        BotMod.AI.BotNeuralBrain.Unload();
        string reason;
        bool ok;
        try { ok = BotMod.AI.BotNeuralBrain.TryLoad(path, out reason); }
        catch (Exception ex)
        {
            Check(false, ctx + ": TryLoad threw " + ex.GetType().Name + ": " + ex.Message);
            return;
        }
        if (!ok)
        {
            Check(!string.IsNullOrEmpty(reason), ctx + ": failed load has empty reason");
            Check(!BotMod.AI.BotNeuralBrain.Loaded, ctx + ": Loaded stays true after failed load");
            BotMod.AI.BotNeuralBrain.NeuralOutputs o;
            Check(!BotMod.AI.BotNeuralBrain.TryEval(SaneInputs(rng), out o),
                ctx + ": TryEval reported success while unloaded");
            return;
        }
        Check(BotMod.AI.BotNeuralBrain.Loaded, ctx + ": load ok but Loaded false");
        int ins = BotMod.AI.BotNeuralBrain.Inputs;
        int hid = BotMod.AI.BotNeuralBrain.Hidden;
        int outs = BotMod.AI.BotNeuralBrain.Outputs;
        Check(ins == kExpectedInputs,
            ctx + ": accepted inputs=" + ins + " (v1 packs exactly " + kExpectedInputs + ")");
        int want = hid * ins + hid + outs * hid + outs;
        Check(BotMod.AI.BotNeuralBrain.WeightCount == want,
            ctx + ": WeightCount " + BotMod.AI.BotNeuralBrain.WeightCount + " != " + want);
        Check(!string.IsNullOrEmpty(BotMod.AI.BotNeuralBrain.LoadedPath), ctx + ": loaded path empty");
        CheckEvalContract(rng, ctx);
    }

    // ---- mutant generation ----

    static byte[] MutateBytes(byte[] src, Random rng)
    {
        byte[] m = (byte[])src.Clone();
        switch (rng.Next(4))
        {
            case 0: // truncate
                int keep = rng.Next(0, m.Length);
                Array.Resize(ref m, keep);
                return m;
            case 1: // flip bytes
                for (int n = rng.Next(1, 8); n > 0 && m.Length > 0; n--)
                    m[rng.Next(m.Length)] = (byte)rng.Next(256);
                return m;
            case 2: // delete a chunk
                if (m.Length < 4) return m;
                int cutAt = rng.Next(m.Length - 1);
                int cutLen = Math.Min(rng.Next(1, 40), m.Length - cutAt);
                byte[] shorter = new byte[m.Length - cutLen];
                Array.Copy(m, shorter, cutAt);
                Array.Copy(m, cutAt + cutLen, shorter, cutAt, m.Length - cutAt - cutLen);
                return shorter;
            default: // insert junk
                int at = rng.Next(m.Length + 1);
                int junkLen = rng.Next(1, 20);
                byte[] longer = new byte[m.Length + junkLen];
                Array.Copy(m, longer, at);
                for (int j = 0; j < junkLen; j++) longer[at + j] = (byte)rng.Next(256);
                Array.Copy(m, at, longer, at + junkLen, m.Length - at);
                return longer;
        }
    }

    static readonly int[] BadVersions = { 0, 2, -1, 99 };
    static readonly int[] WrongInputs = { 13, 15, 0, -3, 32 };   // all must be rejected
    static readonly int[] WrongOutputs = { 1, 3, 9, 0, -2, 6 };  // all must be rejected
    static readonly int[] AltHidden = { 1, 2, 8, 64, 256 };

    static JArray BuildWeights(int inputs, int hidden, int outputs, Random rng)
    {
        int want = hidden * inputs + hidden + outputs * hidden + outputs;
        var arr = new JArray();
        for (int i = 0; i < want; i++) arr.Add(Math.Round(rng.NextDouble() * 2.0 - 1.0, 6));
        return arr;
    }

    /// <summary>Structure-aware tamper: returns mutated JSON text, or null when
    /// the strategy needs the base to parse and it does not.</summary>
    static string MutateStructure(string json, Random rng)
    {
        JObject obj;
        try { obj = JObject.Parse(json); }
        catch (Exception) { return null; }
        switch (rng.Next(7))
        {
            case 0:
                obj["version"] = BadVersions[rng.Next(BadVersions.Length)];
                break;
            case 1:
                obj["inputs"] = WrongInputs[rng.Next(WrongInputs.Length)];
                break;
            case 2: // alternate hidden width WITH a matching weight array: must load and run
                {
                    int h = AltHidden[rng.Next(AltHidden.Length)];
                    obj["hidden"] = h;
                    obj["weights"] = BuildWeights(kExpectedInputs, h, 5, rng);
                    break;
                }
            case 3: // alternate topology keeping stale weights: count mismatch
                obj["hidden"] = AltHidden[rng.Next(AltHidden.Length)];
                break;
            case 4:
                switch (rng.Next(5))
                {
                    case 0: obj.Property("weights").Remove(); break;
                    case 1: obj["weights"] = new JArray(); break;
                    case 2: ((JArray)obj["weights"]).Add(0.5); break;
                    case 3:
                        var w = (JArray)obj["weights"];
                        if (w.Count > 1) w.RemoveAt(w.Count - 1);
                        break;
                    default:
                        var w2 = (JArray)obj["weights"];
                        if (w2.Count > 0)
                        {
                            int at = rng.Next(w2.Count);
                            switch (rng.Next(4))
                            {
                                case 0: w2[at] = "not-a-number"; break;
                                case 1: w2[at] = null; break;
                                case 2: w2[at] = new JObject(); break;
                                default: w2[at] = rng.Next(2) == 0 ? 1e308 : -1e308; break;
                            }
                        }
                        break;
                }
                break;
            case 5: // configHash must never affect loadability
                obj["configHash"] = "\u00fcml\u00e4ut \"quoted\" \\ backslash";
                break;
            default: // extra unknown fields are ignored, load must still succeed
                obj["futureField"] = new JObject { ["nested"] = 42 };
                break;
        }
        return obj.ToString(Newtonsoft.Json.Formatting.None);
    }

    static string WriteMutant(string dir, string name, byte[] content)
    {
        string p = Path.Combine(dir, name);
        File.WriteAllBytes(p, content);
        return p;
    }

    // ---- driver ----

    static int Main(string[] args)
    {
        string root = args != null && args.Length > 0 ? args[0] : ".";
        string goldenPath = null;
        foreach (string cand in new[]
        {
            Path.Combine(root, "evolved", "best.json"),
            Path.Combine("evolved", "best.json"),
            "../evolved/best.json"
        })
            if (File.Exists(cand)) { goldenPath = cand; break; }

        string dir = TempDir();
        try
        {
            if (goldenPath == null)
            {
                Console.WriteLine("neural fuzz: evolved/best.json not found under '" + root + "', running synthetic corpus only");
                goldenPath = WriteSyntheticGolden(dir);
            }

            byte[] goldenBytes = File.ReadAllBytes(goldenPath);
            string goldenText = Encoding.UTF8.GetString(goldenBytes);
            var rng = new Random(20260823);

            // Deterministic regressions first: each pins one parser contract.
            RegressionSuite(goldenPath, goldenText, dir, rng);

            // Randomized mutants.
            for (int i = 0; i < Mutants; i++)
            {
                byte[] content;
                double roll = rng.NextDouble();
                if (roll < 0.40) content = MutateBytes(goldenBytes, rng);
                else
                {
                    string mutated = MutateStructure(goldenText, rng);
                    if (mutated == null) content = MutateBytes(goldenBytes, rng);
                    else content = Encoding.UTF8.GetBytes(mutated);
                }
                FuzzLoad(WriteMutant(dir, "mut-" + i + ".json", content), rng, "mutant-" + i);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        Console.WriteLine(_failures == 0 ? "all neural brain fuzz checks passed" : _failures + " fuzz check(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Minimal valid v1 file for runs without the repo's golden asset.</summary>
    static string WriteSyntheticGolden(string dir)
    {
        var rng = new Random(1);
        var obj = new JObject
        {
            ["version"] = 1,
            ["inputs"] = kExpectedInputs,
            ["hidden"] = 16,
            ["outputs"] = 5,
            ["configHash"] = "synthetic",
            ["weights"] = BuildWeights(kExpectedInputs, 16, 5, rng)
        };
        return WriteMutant(dir, "golden.json", Encoding.UTF8.GetBytes(obj.ToString()));
    }

    static string VariantFile(string dir, string name, Action<JObject> edit, Random rng, bool rebuildWeights)
    {
        var obj = JObject.Parse(File.ReadAllText(Path.Combine(dir, "_base.json")));
        edit(obj);
        if (rebuildWeights)
        {
            int ins = obj.Value<int?>("inputs") ?? kExpectedInputs;
            int h = obj.Value<int?>("hidden") ?? 16;
            int o = obj.Value<int?>("outputs") ?? 5;
            obj["weights"] = BuildWeights(ins, h, o, rng);
        }
        return WriteMutant(dir, name, Encoding.UTF8.GetBytes(obj.ToString()));
    }

    static void RegressionSuite(string goldenPath, string goldenText, string dir, Random rng)
    {
        File.WriteAllText(Path.Combine(dir, "_base.json"), goldenText);

        // 1. The shipped model loads and evaluates.
        FuzzLoad(goldenPath, rng, "golden");
        Check(BotMod.AI.BotNeuralBrain.Loaded, "regression: shipped best.json did not load");

        // 2. Reload determinism: same file twice gives the same verdict+reason.
        {
            string r1, r2;
            bool ok1 = BotMod.AI.BotNeuralBrain.TryLoad(goldenPath, out r1);
            bool ok2 = BotMod.AI.BotNeuralBrain.TryLoad(goldenPath, out r2);
            Check(ok1 && ok2 && r1 == r2, "regression: reload not deterministic: " + r1 + " vs " + r2);
        }

        // 3. Frozen dimension contract: anything but 14 inputs or 5 outputs is
        //    rejected outright. Before this was enforced, such files loaded
        //    and then silently misbehaved every tick (inputs: scratch-buffer
        //    overruns swallowed by TryEval's catch-all; outputs: the fire and
        //    strafe gates read stale scratch slots from the previous model).
        foreach (int bad in WrongInputs)
        {
            string p = VariantFile(dir, "bad-inputs-" + bad + ".json",
                o => o["inputs"] = bad, rng, rebuildWeights: true);
            string reason;
            bool ok = BotMod.AI.BotNeuralBrain.TryLoad(p, out reason);
            Check(!ok, "regression: inputs=" + bad + " was accepted");
            if (!ok) Check(reason != null && reason.Contains("inputs"),
                "regression: inputs=" + bad + " rejection lacks reason, got: " + reason);
            Check(!BotMod.AI.BotNeuralBrain.Loaded, "regression: loaded despite inputs=" + bad);
        }
        foreach (int bad in WrongOutputs)
        {
            // rebuildWeights matches the array to the mutated topology, so a
            // length-only check would accept these; rejection must come from
            // the action-head pin itself.
            string p = VariantFile(dir, "bad-outputs-" + bad + ".json",
                o => o["outputs"] = bad, rng, rebuildWeights: true);
            string reason;
            bool ok = BotMod.AI.BotNeuralBrain.TryLoad(p, out reason);
            Check(!ok, "regression: outputs=" + bad + " was accepted");
            if (!ok) Check(reason != null && reason.Contains("outputs"),
                "regression: outputs=" + bad + " rejection lacks reason, got: " + reason);
            Check(!BotMod.AI.BotNeuralBrain.Loaded, "regression: loaded despite outputs=" + bad);
        }

        // 4. Version pinning.
        {
            string p = VariantFile(dir, "bad-version.json", o => o["version"] = 2, rng, false);
            string reason;
            Check(!BotMod.AI.BotNeuralBrain.TryLoad(p, out reason), "regression: version=2 accepted");
        }

        // 5. Missing weights array.
        {
            string p = VariantFile(dir, "no-weights.json",
                delegate(JObject o) { o.Property("weights").Remove(); }, rng, false);
            string reason;
            Check(!BotMod.AI.BotNeuralBrain.TryLoad(p, out reason), "regression: missing weights accepted");
        }

        // 6. Off-by-one weight count on an otherwise valid file.
        {
            string p = VariantFile(dir, "short-weights.json", delegate(JObject o) { }, rng, false);
            var obj = JObject.Parse(File.ReadAllText(p));
            var w = (JArray)obj["weights"];
            w.RemoveAt(w.Count - 1);
            string p2 = WriteMutant(dir, "short-weights2.json", Encoding.UTF8.GetBytes(obj.ToString()));
            string reason;
            Check(!BotMod.AI.BotNeuralBrain.TryLoad(p2, out reason), "regression: short weight array accepted");
        }

        // 7. Buffer-growth paths: topologies larger than the defaults load and evaluate.
        foreach (int h in new[] { 1, 64, 256 })
        {
            string p = VariantFile(dir, "hidden-" + h + ".json", o => o["hidden"] = h, rng, true);
            FuzzLoad(p, rng, "hidden=" + h);
        }
    }
}
