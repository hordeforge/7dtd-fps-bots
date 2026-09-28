// BotConfigFuzzTests: fuzzing of the operator-maintained Config/botmod.json
// loader. BotConfig.Load parses a hand-editable file reachable at every mod
// startup (primary + .bak recovery) and its output drives spawn counts,
// combat tuning and team maps on every tick. Mutants (byte-level and
// structure-aware JSON tampering of the shipped config/botmod.json) must
// either fail cleanly back to defaults/.bak or load into a state that
// satisfies Normalize's full documented range contract; Load itself must
// never throw and never return null. Includes regressions for the depth DoS
// guard (Json.NET MaxDepth) and load determinism.
//
// Needs Newtonsoft.Json.dll from the game install (same gate as the neural
// weights fuzzer); compiles only the engine-free Config sources. Run locally:
//
//   bash scripts/test-idempotency.sh
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using BotMod.Config;

static class BotConfigFuzzTests
{
    const int Mutants = 500;

    static int _failures;
    static int _warns;
    static int _docs;

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
        string dir = Path.Combine(Path.GetTempPath(), "botmod-configfuzz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Normalize's contract, restated independently: whatever came
    /// off disk, a loaded config is inside these bounds before first use.</summary>
    static void CheckLoadedContract(BotConfig cfg, string ctx)
    {
        Check(cfg != null, ctx + ": Load returned null");
        if (cfg == null) return;
        Check(cfg.TargetBotCount >= 0 && cfg.TargetBotCount <= 64, ctx + ": TargetBotCount out of range: " + cfg.TargetBotCount);
        Check(cfg.MaxBots >= cfg.TargetBotCount && cfg.MaxBots <= 64, ctx + ": MaxBots " + cfg.MaxBots + " vs TargetBotCount " + cfg.TargetBotCount);
        Check(cfg.Difficulty >= 0 && cfg.Difficulty <= 4, ctx + ": Difficulty out of range: " + cfg.Difficulty);
        Check(cfg.BotAmmoCount >= 0 && cfg.BotAmmoCount <= 10000, ctx + ": BotAmmoCount out of range: " + cfg.BotAmmoCount);
        Check(cfg.BotHealth >= 10f && cfg.BotHealth <= 10000f, ctx + ": BotHealth out of range: " + cfg.BotHealth);
        Check(cfg.VisionRange >= 8f && cfg.VisionRange <= 300f, ctx + ": VisionRange out of range: " + cfg.VisionRange);
        Check(cfg.LoseTargetRange >= cfg.VisionRange && cfg.LoseTargetRange <= 400f, ctx + ": LoseTargetRange " + cfg.LoseTargetRange + " vs VisionRange " + cfg.VisionRange);
        Check(cfg.AttackRange >= 3f && cfg.AttackRange <= cfg.VisionRange, ctx + ": AttackRange " + cfg.AttackRange + " vs VisionRange " + cfg.VisionRange);
        Check(cfg.HeadshotChance >= 0f && cfg.HeadshotChance <= 1f, ctx + ": HeadshotChance out of range: " + cfg.HeadshotChance);
        // Multiplier feeds Mathf.RoundToInt(dmg*mult): must be clamped and
        // finite or the int cast overflows (unspecified result can heal targets).
        Check(!float.IsNaN(cfg.HeadshotMultiplier) && !float.IsInfinity(cfg.HeadshotMultiplier)
            && cfg.HeadshotMultiplier >= 1f && cfg.HeadshotMultiplier <= 10f,
            ctx + ": HeadshotMultiplier out of range: " + cfg.HeadshotMultiplier);
        Check(cfg.BotTeamCount >= 0 && cfg.BotTeamCount <= 8, ctx + ": BotTeamCount out of range: " + cfg.BotTeamCount);
        Check(cfg.StrafeChance >= 0f && cfg.StrafeChance <= 1f, ctx + ": StrafeChance out of range: " + cfg.StrafeChance);
        Check(cfg.DodgeOnHitChance >= 0f && cfg.DodgeOnHitChance <= 1f, ctx + ": DodgeOnHitChance out of range: " + cfg.DodgeOnHitChance);
        Check(cfg.SpawnNearPlayerChance >= 0f && cfg.SpawnNearPlayerChance <= 1f, ctx + ": SpawnNearPlayerChance out of range: " + cfg.SpawnNearPlayerChance);
        Check(cfg.BotNames != null && cfg.BotNames.Length > 0, ctx + ": BotNames empty after Normalize");
        Check(cfg.LoadoutPool != null && cfg.LoadoutPool.Length > 0, ctx + ": LoadoutPool empty after Normalize");

        // Every float field is finite after load: bare NaN/Infinity literals
        // parse cleanly (Newtonsoft) and survive Max/Min clamp chains, so
        // Normalize replaces them with defaults instead of letting them reach
        // divisor math and the neural obs vector.
        float[] floats =
        {
            cfg.BotHealth, cfg.VisionRange, cfg.VisionAngle, cfg.LoseTargetRange,
            cfg.LoseTargetTimeSec, cfg.AttackRange,
            cfg.HeadshotChance, cfg.HeadshotMultiplier,
            cfg.ReactionTimeSec, cfg.PathRecalcIntervalSec, cfg.StuckTimeoutSec,
            cfg.RandomWanderRadius, cfg.RandomWanderIntervalSec, cfg.SpawnRadius,
            cfg.SpawnProtectionSec, cfg.SpawnNearPlayerChance, cfg.StrafeChance,
            cfg.DodgeOnHitChance
        };
        for (int i = 0; i < floats.Length; i++)
            Check(!float.IsNaN(floats[i]) && !float.IsInfinity(floats[i]), ctx + ": float field #" + i + " not finite after Normalize: " + floats[i]);

        // Team map: canonical keys only, values inside the loaded team range.
        Dictionary<string, int> snap = cfg.SnapshotTeamAssignments();
        foreach (KeyValuePair<string, int> kv in snap)
        {
            Check(kv.Key.Length > 0, ctx + ": empty team-assignment key survived load");
            Check(kv.Value >= 0 && kv.Value <= cfg.BotTeamCount, ctx + ": assignment '" + kv.Key + "'=" + kv.Value + " outside 0.." + cfg.BotTeamCount);
        }

        // The locked helpers must work against whatever was loaded (a null or
        // wrongly-typed TeamAssignments repaired by Normalize): set then get.
        cfg.SetTeamAssignment("Grunt", 2);
        Check(cfg.GetTeamAssignment("Grunt") == 2, ctx + ": Set/GetTeamAssignment round-trip failed");
        Check(cfg.GetTeamAssignment("") == 0, ctx + ": empty lookup not free-for-all");
    }

    /// <summary>Write mutant, load it, pin the no-throw/non-null/determinism/
    /// range contract. Warn output is counted, not printed.</summary>
    static void FuzzLoad(string path, string ctx)
    {
        _docs++;
        BotConfig.Warn = _ => _warns++;
        BotConfig cfg = null, again = null;
        try
        {
            cfg = BotConfig.Load(path);
            // Load determinism: the same bytes twice give the same core state.
            // Both instances are untouched here (the contract check below
            // mutates via SetTeamAssignment).
            again = BotConfig.Load(path);
        }
        catch (Exception ex)
        {
            BotConfig.Warn = null;
            Check(false, ctx + ": Load threw " + ex.GetType().Name + ": " + ex.Message);
            return;
        }
        BotConfig.Warn = null;
        Check(cfg != null && again != null, ctx + ": Load returned null");
        if (cfg == null || again == null) return;
        Check(cfg.TargetBotCount == again.TargetBotCount
            && cfg.MaxBots == again.MaxBots
            && cfg.Difficulty == again.Difficulty
            && cfg.BotTeamCount == again.BotTeamCount
            && cfg.SnapshotTeamAssignments().Count == again.SnapshotTeamAssignments().Count,
            ctx + ": reload not deterministic");
        CheckLoadedContract(cfg, ctx);
    }

    // ---- mutant generation ----

    static readonly string[] NumericFields =
    {
        "TargetBotCount", "MaxBots", "BotAmmoCount", "BotHealth", "Difficulty",
        "VisionRange", "VisionAngle", "LoseTargetRange", "LoseTargetTimeSec",
        "AttackRange", "HeadshotChance", "HeadshotMultiplier",
        "ReactionTimeSec",
        "PathRecalcIntervalSec", "StuckTimeoutSec", "RandomWanderRadius",
        "RandomWanderIntervalSec", "SpawnRadius", "SpawnNearPlayerChance",
        "SpawnProtectionSec", "StrafeChance", "DodgeOnHitChance", "BotTeamCount"
    };

    /// <summary>Structure-aware tamper: returns mutated JSON text, or null when
    /// the strategy needs the base to parse and it does not.</summary>
    static string MutateStructure(string json, Random rng)
    {
        JObject obj;
        try { obj = JObject.Parse(json); }
        catch (Exception) { return null; }
        switch (rng.Next(7))
        {
            case 0: // numeric field gets an extreme / wrong-typed value
                object v = MutantBytes.ExtremeValues[rng.Next(MutantBytes.ExtremeValues.Length)];
                obj[NumericFields[rng.Next(NumericFields.Length)]] = v as JToken ?? new JValue(v);
                break;
            case 1: // team map attacks: wrong container, hostile keys/values
                switch (rng.Next(5))
                {
                    case 0: obj["TeamAssignments"] = null; break;
                    case 1: obj["TeamAssignments"] = new JArray("Grunt", 1); break;
                    case 2: obj["TeamAssignments"] = new JObject { ["\u0000ctl \u200bzwsp"] = -5 }; break;
                    case 3: obj["TeamAssignments"] = new JObject { ["Grunt"] = int.MaxValue, ["Ranger"] = "three" }; break;
                    default: obj["TeamAssignments"] = new JObject { ["deep"] = new JObject { ["deeper"] = new JArray(1) } }; break;
                }
                break;
            case 2: // name/loadout pools: wrong type, empty, hostile entries
                switch (rng.Next(4))
                {
                    case 0: obj["BotNames"] = null; break;
                    case 1: obj["BotNames"] = new JArray(); break;
                    case 2: obj["LoadoutPool"] = "gunMGT1AK47"; break;
                    default: obj["LoadoutPool"] = new JArray { "", "\u0000", "\ud83d\ude00 gun" }; break;
                }
                break;
            case 3: // bool fields get non-bool values
                object b = MutantBytes.ExtremeValues[rng.Next(MutantBytes.ExtremeValues.Length)];
                obj[new[] { "Enabled", "DedicatedOnly", "AllowSyntheticAuthBypass", "UseNeuralBrain", "BotVsBot" }[rng.Next(5)]] =
                    b as JToken ?? new JValue(b);
                break;
            case 4: // duplicate key with different casing: last-wins binding
                obj["targetbotcount"] = rng.Next(-10, 100);
                obj["TARGETBOTCOUNT"] = rng.Next(-10, 100);
                break;
            case 5: // typo keys (unknown-key warn path) incl. control characters
                obj["TagetBotCoun\u0007t"] = 1;
                obj["\u00fcml\u00e4ut"] = new JObject();
                break;
            default: // weight path / entity class strings go hostile
                obj[new[] { "BotNeuralWeightPath", "BotEntityClass", "BotWeapon", "BotAmmo" }[rng.Next(4)]] =
                    new[] { "../../etc/passwd", "\u0000\u001f", new string('x', 4096), "" }[rng.Next(4)];
                break;
        }
        return obj.ToString(Newtonsoft.Json.Formatting.None);
    }

    // ---- driver ----

    static int Main(string[] args)
    {
        string root = args != null && args.Length > 0 ? args[0] : ".";
        string goldenPath = null;
        foreach (string cand in new[]
        {
            Path.Combine(root, "config", "botmod.json"),
            Path.Combine("config", "botmod.json"),
            "../config/botmod.json"
        })
            if (File.Exists(cand)) { goldenPath = cand; break; }
        if (goldenPath == null)
        {
            Console.WriteLine("config fuzz: config/botmod.json not found under '" + root + "', skipping");
            return 0;
        }

        byte[] goldenBytes = File.ReadAllBytes(goldenPath);
        string goldenText = Encoding.UTF8.GetString(goldenBytes);
        var rng = new Random(20260823);
        string dir = TempDir();
        try
        {
            // Deterministic regressions first.

            // 1. The shipped config loads into the documented contract.
            FuzzLoad(goldenPath, "golden");
            Check(_warns == 0, "golden: shipped config raised " + _warns + " warning(s), expected clean load");

            // 2. Depth DoS guard: deeply nested junk must be rejected by the
            //    reader (Json.NET MaxDepth) and fall back to defaults, not
            //    overflow the stack or hang.
            {
                string deep = "{\"junk\":" + new string('[', 512) + new string(']', 512) + "}";
                string p = Path.Combine(dir, "deep.json");
                File.WriteAllText(p, deep);
                FuzzLoad(p, "depth-512");
            }

            // 3. Not an object at all: arrays and scalars as the root document.
            string[] rootDocs = { "[1,2,3]", "\"str\"", "42", "true", "null", "{", "", "   ", "\ufeff{}" };
            for (int d = 0; d < rootDocs.Length; d++)
            {
                string p = Path.Combine(dir, "root-" + d + ".json");
                File.WriteAllText(p, rootDocs[d]);
                FuzzLoad(p, "root-doc<" + (rootDocs[d].Length == 0 ? "empty" : rootDocs[d]) + ">");
            }

            // 4. Bare NaN/Infinity float literals parse into float properties
            //    (Newtonsoft) and must come out as finite defaults, not survive
            //    the Max/Min clamp chains (NaN is absorbing for both).
            {
                string nan = "{\"BotHealth\": NaN, \"VisionRange\": Infinity, \"HeadshotMultiplier\": -Infinity}";
                string p = Path.Combine(dir, "nan-literals.json");
                File.WriteAllText(p, nan);
                FuzzLoad(p, "nan-literals");
                BotConfig cfg = BotConfig.Load(p);
                Check(cfg.BotHealth == 100f && cfg.VisionRange == 70f && cfg.HeadshotMultiplier == 2f,
                    "nan-literals: non-finite values did not fall back to defaults ("
                    + cfg.BotHealth + "/" + cfg.VisionRange + "/" + cfg.HeadshotMultiplier + ")");
            }

            // 5. HeadshotMultiplier magnitude: an out-of-range value used to
            //    reach Mathf.RoundToInt(dmg*mult) unclamped and overflow the
            //    int damage cast.
            {
                string big = "{\"HeadshotMultiplier\": 3e8}";
                string p = Path.Combine(dir, "headshot-mult.json");
                File.WriteAllText(p, big);
                BotConfig cfg = BotConfig.Load(p);
                Check(cfg.HeadshotMultiplier >= 1f && cfg.HeadshotMultiplier <= 10f,
                    "headshot-mult: 3e8 survived unclamped (" + cfg.HeadshotMultiplier + ")");
            }

            // Randomized mutants.
            for (int i = 0; i < Mutants; i++)
            {
                byte[] content;
                double roll = rng.NextDouble();
                if (roll < 0.40) content = MutantBytes.Mutate(goldenBytes, rng);
                else
                {
                    string mutated = MutateStructure(goldenText, rng);
                    if (mutated == null) content = MutantBytes.Mutate(goldenBytes, rng);
                    else content = Encoding.UTF8.GetBytes(mutated);
                }
                string p = Path.Combine(dir, "mut-" + i + ".json");
                File.WriteAllBytes(p, content);
                FuzzLoad(p, "mutant-" + i);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        Console.WriteLine("config fuzz: " + _docs + " documents, " + _warns + " warnings surfaced");
        Console.WriteLine(_failures == 0 ? "all bot config fuzz checks passed" : _failures + " fuzz check(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
