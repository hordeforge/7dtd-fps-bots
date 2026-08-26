using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace BotMod.AI
{
    /// <summary>
    /// Advisory neural brain for the neuroevolution research
    /// (docs/research/00..06). Pure math only: no RNG, no allocs on tick,
    /// no framework. Handwritten forward pass with Tanh hidden and mixed
    /// output heads. When no model is loaded or the flag is off, callers fall
    /// back to the heuristic (zero behavior change).
    /// </summary>
    public static class BotNeuralBrain
    {
        const int kVersion = 1;
        // Frozen observation layout of version 1 (docs/research/01 §2, trainer
        // tools/ga/ga.py INPUTS): TryEval packs exactly these 14 features into
        // fixed scratch slots, so any other "inputs" value would load and then
        // fail every evaluation (or read/write past the scratch buffer).
        const int kInputs = 14;
        // Frozen action layout of version 1 (docs/research/01 §3, "Action heads
        // (5 out)"): TryEval reads exactly these five heads by index (camp,
        // retreat, aimBiasYaw, fireGate, strafe). A file declaring fewer heads
        // would load and then drive the fire/strafe gates from stale scratch
        // slots left over from the previous model; more heads would silently
        // ignore them. Same rejection contract as the inputs pin above.
        const int kOutputs = 5;
        static bool _loaded;
        static string _loadedPath = "";
        static string _loadedHash = "";
        static int _hidden = 16;
        static int _outputs = 5;
        static float[] _weights;          // flat canonical order (see docs)
        static float[] _hiddenBuf = new float[32];
        static float[] _outBuf = new float[8];
        static float[] _w1;  // views into _weights
        static float[] _b1;
        static float[] _w2;
        static float[] _b2;
        static string _lastReason = "not loaded";

        // Mod root directory for relative weight-path resolution. Wired from
        // ModApi.ModPath by ModApi.InitMod so this file stays free of a
        // dependency on the entry-point type (headless unit tests can exercise
        // TryLoad); the default keeps headless runs on the CWD candidates.
        internal static string ModRoot = "";

        public static bool Loaded => _loaded;
        public static string LastReason => _lastReason;
        public static string LoadedPath => _loadedPath;
        public static string LoadedHash => _loadedHash;
        public static int Inputs => kInputs;
        public static int Hidden => _hidden;
        public static int Outputs => _outputs;
        public static int WeightCount => _weights != null ? _weights.Length : 0;

        // 5 advisory outputs matching docs/research/01 §3:
        // 0 campLogit → wantCamp, 1 retreatLogit → wantRetreat,
        // 2 aimBiasYaw (tanh-scaled), 3 fireGate (sigmoid), 4 strafeDir (sigmoid)
        public struct NeuralOutputs
        {
            public bool WantCamp;
            public bool WantRetreat;
            public float AimBiasYaw; // radians, already clamped to ±0.45*(1-acc) window by caller
            public bool ShouldFire;  // still ANDed with reaction/burst/LOS/range in TryShootBurst
            public int StrafeDir;    // -1 or 1
            public float CampLogit;
            public float RetreatLogit;
            public float StrafeLogit; // continuous strafe sigmoid (R10 movement)
            public float FireGate;
        }

        public struct NeuralInputs
        {
            public float HpFrac;
            public float EnemyHpFrac;
            public float DistNorm;
            public float CanSee;
            // Sustained-fire spread fraction [0,1] (trainer: combat_sim.py spread[]).
            public float SpreadFrac;
            public float WeaponRangeNorm;
            public float PelletsNorm;
            public float AimAcc;
            public float AimSkill;
            public float Aggression;
            public float SelfPreservation;
            public float Camper;
            // Rounds-left fraction [0,1] (trainer: ammo+reserve over the pool).
            public float AmmoLeftFrac;
            public float StuckFrac;
            // ctor packs in order for Forward; keeps call sites greppable
        }

        static float Sigmoid(float x)
        {
            if (x > 8f) return 1f;
            if (x < -8f) return 0f;
            return 1f / (1f + (float)Math.Exp(-x));
        }

        /// <summary>Flat forward pass. No allocs; reuses static buffers.</summary>
        public static bool TryEval(in NeuralInputs inp, out NeuralOutputs outs)
        {
            outs = default(NeuralOutputs);
            if (!_loaded || _weights == null) return false;
            try
            {
                // Pack inputs in canonical order matching docs/research/01 §2.
                // Keep this order frozen: Python trainer and C# loader share it.
                // Slot semantics are aligned on both sides of the contract:
                // slot 4 is the sustained-fire spread fraction (Bot._fireSpread,
                // same ADD/DECAY constants as tools/ga/combat_sim.py) and slot
                // 12 the rounds-left fraction (Bot magazine fill vs the sim's
                // ammo+reserve pool). Never change one side alone.
                float[] x = _scratchX;
                x[0] = inp.HpFrac;
                x[1] = inp.EnemyHpFrac;
                x[2] = inp.DistNorm;
                x[3] = inp.CanSee;
                x[4] = inp.SpreadFrac;
                x[5] = inp.WeaponRangeNorm;
                x[6] = inp.PelletsNorm;
                x[7] = inp.AimAcc;
                x[8] = inp.AimSkill;
                x[9] = inp.Aggression;
                x[10] = inp.SelfPreservation;
                x[11] = inp.Camper;
                x[12] = inp.AmmoLeftFrac;
                x[13] = inp.StuckFrac;

                // hidden = tanh(W1*x + b1)
                for (int h = 0; h < _hidden; h++)
                {
                    float s = _b1[h];
                    int row = h * kInputs;
                    for (int i = 0; i < kInputs; i++) s += _w1[row + i] * x[i];
                    _hiddenBuf[h] = (float)Math.Tanh(s);
                }
                // out = W2*hidden + b2
                for (int o = 0; o < _outputs; o++)
                {
                    float s = _b2[o];
                    int row = o * _hidden;
                    for (int h = 0; h < _hidden; h++) s += _w2[row + h] * _hiddenBuf[h];
                    _outBuf[o] = s;
                }
                float camp = Sigmoid(_outBuf[0]);
                float retreat = Sigmoid(_outBuf[1]);
                float aimRaw = (float)Math.Tanh(_outBuf[2]); // [-1,1]
                float fire = Sigmoid(_outBuf[3]);
                float strafe = Sigmoid(_outBuf[4]);

                outs.CampLogit = camp;
                outs.RetreatLogit = retreat;
                outs.WantCamp = camp > 0.5f;
                outs.WantRetreat = retreat > 0.5f;
                outs.AimBiasYaw = aimRaw; // caller scales by 0.45*(1-acc)
                outs.FireGate = fire;
                outs.ShouldFire = fire > 0.5f;
                outs.StrafeDir = strafe > 0.5f ? 1 : -1;
                outs.StrafeLogit = strafe;
                return true;
            }
            catch (Exception ex)
            {
                // Fallback to heuristic stands (advisory brain), but the failure
                // must not be silent: record it so `bot neural status` and the
                // web dashboard's reason field show why evals are falling back.
                _lastReason = "eval failed: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        static readonly float[] _scratchX = new float[32];

        /// <summary>Try to load evolved/best.json. Returns true on success.</summary>
        public static bool TryLoad(string path, out string reason)
        {
            reason = "";
            _loaded = false;
            _lastReason = "not loaded";
            try
            {
                if (string.IsNullOrEmpty(path)) { reason = "path empty"; _lastReason = reason; return false; }
                // Resolve relative to ModPath and CWD so both dedi and headless work
                string resolved = path;
                if (!Path.IsPathRooted(path))
                {
                    string mod = ModRoot;
                    // Multi-segment Combine everywhere: never embed a separator,
                    // so resolution is the platform API's job on every OS.
                    string[] tries = new[]
                    {
                        Path.Combine(mod ?? "", path),
                        Path.Combine(mod ?? "", "evolved", "best.json"),
                        Path.Combine(Directory.GetCurrentDirectory(), path),
                        Path.Combine(Directory.GetCurrentDirectory(), "evolved", "best.json"),
                        path
                    };
                    resolved = null;
                    foreach (var t in tries) if (!string.IsNullOrEmpty(t) && File.Exists(t)) { resolved = t; break; }
                    if (resolved == null) { reason = "file not found: " + path; _lastReason = reason; return false; }
                }
                if (!File.Exists(resolved)) { reason = "file not found: " + resolved; _lastReason = reason; return false; }
                // Explicit UTF-8: weights files are our own UTF-8 JSON artifacts.
                string json = File.ReadAllText(resolved, Encoding.UTF8);
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                int version = obj.Value<int?>("version") ?? 1;
                if (version != kVersion)
                {
                    reason = "version mismatch: got " + version + " want " + kVersion;
                    _lastReason = reason; return false;
                }
                int inputs = obj.Value<int?>("inputs") ?? kInputs;
                if (inputs != kInputs)
                {
                    reason = "unsupported inputs=" + inputs + " (v" + kVersion + " packs " + kInputs + " features)";
                    _lastReason = reason; return false;
                }
                int hidden = obj.Value<int?>("hidden") ?? 16;
                int outputs = obj.Value<int?>("outputs") ?? kOutputs;
                if (outputs != kOutputs)
                {
                    reason = "unsupported outputs=" + outputs + " (v" + kVersion + " exposes " + kOutputs + " action heads)";
                    _lastReason = reason; return false;
                }
                var arr = obj["weights"] as Newtonsoft.Json.Linq.JArray;
                if (arr == null) { reason = "missing weights[]"; _lastReason = reason; return false; }
                int want = hidden * inputs + hidden + outputs * hidden + outputs;
                if (arr.Count != want)
                {
                    reason = "weights length " + arr.Count + " != want " + want + " (inputs=" + inputs + " hidden=" + hidden + " outputs=" + outputs + ")";
                    _lastReason = reason; return false;
                }
                float[] w = new float[want];
                for (int i = 0; i < want; i++) w[i] = (float)arr[i].Value<double>();
                // validate finite
                for (int i = 0; i < want; i++) if (float.IsNaN(w[i]) || float.IsInfinity(w[i])) { reason = "weight NaN/Inf at " + i; _lastReason = reason; return false; }
                int off = 0;
                int w1Len = hidden * inputs;
                int b1Len = hidden;
                int w2Len = outputs * hidden;
                int b2Len = outputs;
                _w1 = new float[w1Len]; Array.Copy(w, off, _w1, 0, w1Len); off += w1Len;
                _b1 = new float[b1Len]; Array.Copy(w, off, _b1, 0, b1Len); off += b1Len;
                _w2 = new float[w2Len]; Array.Copy(w, off, _w2, 0, w2Len); off += w2Len;
                _b2 = new float[b2Len]; Array.Copy(w, off, _b2, 0, b2Len); off += b2Len;
                _weights = w;
                _hidden = hidden; _outputs = outputs;
                if (_hiddenBuf.Length < hidden) _hiddenBuf = new float[Math.Max(hidden, 32)];
                if (_outBuf.Length < outputs) _outBuf = new float[Math.Max(outputs, 8)];
                // scratchX sized to max inputs (32) already
                _loaded = true;
                _loadedPath = resolved;
                _loadedHash = obj.Value<string>("configHash") ?? "";
                _lastReason = "ok: " + resolved + " (" + want + " weights, hidden=" + hidden + ")";
                reason = _lastReason;
                return true;
            }
            catch (Exception ex) { reason = ex.GetType().Name + ": " + ex.Message; _lastReason = reason; return false; }
        }

        public static void Unload()
        {
            _loaded = false;
            _weights = null; _w1 = null; _b1 = null; _w2 = null; _b2 = null;
            _loadedPath = ""; _loadedHash = "";
            _lastReason = "unloaded";
        }
    }
}
