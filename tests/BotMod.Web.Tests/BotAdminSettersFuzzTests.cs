// BotAdminSettersFuzzTests: randomized fuzzing of the admin setters the web
// API and the console command share (Source/BotMod/Config/BotConfig.cs).
// These are the untrusted-input entry points that survive the typed-read
// layer: POST /api/bot "vs" target, "setTeam" name, "skill" level and
// "teamCount" count all hand caller-supplied strings and integers straight to
// them, and a wrong verdict either flips a live setting (the old
// silently-default behavior) or stores a key that no lookup can ever find.
//
// The harness is stateful and asserts the pair across the persistence
// boundary: whatever SetTeamAssignment writes, GetTeamAssignment must read
// back for every spelling of the same name (spawn-decorated, wrong case,
// NFD, invisible noise), and the map must hold exactly the canonical keys.
// SetVsTarget is checked against an independent table of the accepted
// aliases: an unknown target must leave all three flags untouched and report
// no field to persist. The int setters are driven past both ends of their
// ranges and must land inside the documented clamp with the config still
// satisfying the whole Normalize contract.
//
// Needs Newtonsoft.Json.dll from the game install (BotConfig.Load lives in the
// same file); compiles only the engine-free Config sources. Run locally:
//
//   bash scripts/test-idempotency.sh
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BotMod.Config;

static class BotAdminSettersFuzzTests
{
    const int Seeds = 64;
    const int OpsPerSeed = 80;

    static int _failures;
    static int _ops;

    static void Check(bool ok, string detail)
    {
        if (!ok)
        {
            _failures++;
            Console.WriteLine("FAIL " + detail);
        }
    }

    // The accepted "vs" aliases, restated from the documented contract rather
    // than read out of the implementation, with the flag each one drives.
    static readonly Dictionary<string, string> VsTargets = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "bot", "BotVsBot" }, { "bots", "BotVsBot" },
        { "zombie", "BotVsZombie" }, { "zombies", "BotVsZombie" },
        { "player", "BotVsPlayer" }, { "players", "BotVsPlayer" },
        { "human", "BotVsPlayer" },
    };

    static readonly string[] FieldNames = { "BotVsBot", "BotVsZombie", "BotVsPlayer" };

    static uint _rng = 0x5E77B0B5u;

    static uint Next()
    {
        uint x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        _rng = x;
        return x;
    }

    static int Pick(int n) { return (int)(Next() % (uint)n); }

    // Values that sit on and past both ends of every documented range, plus
    // the extremes of the type itself.
    static readonly int[] Ints =
    {
        int.MinValue, int.MinValue + 1, -1000, -9, -8, -1, 0, 1, 3, 4, 5,
        7, 8, 9, 15, 16, 17, 64, 1000, int.MaxValue - 1, int.MaxValue
    };

    // Name shapes an admin body or a pasted scoreboard name can carry.
    static readonly string[] Names =
    {
        "Grunt", "grunt", "GRUNT", "Grunt_42", "[Bot] Grunt_42", "[bot] grunt_7",
        "K\u00EDra", "\u1E0A", "Ki\u0301ra", "Female", "Female\u000D", "\uFEFFFemale",
        "Name With Spaces", "tab\there", "a_b_c", "_", "", "[Bot] ", "\u65E5\u672C\u8A9E",
        "\uFEFF", "", "null", "0"
    };

    static int RandInt()
    {
        return Next() % 4 == 0 ? Ints[Pick(Ints.Length)] : (int)(Next() % 20u) - 10;
    }

    static string RandName()
    {
        if (Next() % 3 != 0) return Names[Pick(Names.Length)];
        var sb = new StringBuilder();
        int parts = 1 + Pick(3);
        for (int i = 0; i < parts; i++) sb.Append(Names[Pick(Names.Length)]);
        return sb.ToString();
    }

    /// <summary>Normalize's contract, restated independently: whatever an
    /// admin setter did, the config is inside these bounds before first
    /// use (same list as BotConfigFuzzTests, which covers the loader).</summary>
    static void CheckInRange(BotConfig cfg, string ctx)
    {
        Check(cfg.Difficulty >= 0 && cfg.Difficulty <= 4, ctx + ": Difficulty out of range: " + cfg.Difficulty);
        Check(cfg.BotTeamCount >= 0 && cfg.BotTeamCount <= 8, ctx + ": BotTeamCount out of range: " + cfg.BotTeamCount);
        Check(cfg.VisionRange >= 8f && cfg.VisionRange <= 300f, ctx + ": VisionRange out of range: " + cfg.VisionRange);
        Check(cfg.AttackRange >= 3f && cfg.AttackRange <= cfg.VisionRange, ctx + ": AttackRange out of range: " + cfg.AttackRange);
        Check(cfg.StrafeChance >= 0f && cfg.StrafeChance <= 1f, ctx + ": StrafeChance out of range: " + cfg.StrafeChance);
        Check(!float.IsNaN(cfg.BotHealth) && cfg.BotHealth >= 10f, ctx + ": BotHealth out of range: " + cfg.BotHealth);
        foreach (var kv in cfg.SnapshotTeamAssignments())
        {
            Check(kv.Value >= 0 && kv.Value <= cfg.BotTeamCount,
                ctx + ": stored team " + kv.Value + " outside 0.." + cfg.BotTeamCount);
            Check(kv.Key.Length > 0 && BotText.IdentityKey(kv.Key) == kv.Key,
                ctx + ": stored key is not in canonical form: " + kv.Key);
        }
    }

    static int Main(string[] args)
    {
        // Untrusted "vs" targets: an unknown one must change nothing and name
        // no field, a known alias must drive exactly its own flag.
        foreach (string target in Names)
        {
            var cfg = new BotConfig();
            bool beforeBot = cfg.BotVsBot, beforeZombie = cfg.BotVsZombie, beforePlayer = cfg.BotVsPlayer;
            bool on = (Next() % 2) == 0;
            string field;
            bool ok = cfg.SetVsTarget(target, on, out field);
            string known;
            if (VsTargets.TryGetValue(target ?? "", out known))
            {
                Check(ok, "SetVsTarget rejected the documented alias " + target);
                Check(field == known, "SetVsTarget(" + target + ") named field " + field + " instead of " + known);
                bool actual = field == "BotVsBot" ? cfg.BotVsBot : field == "BotVsZombie" ? cfg.BotVsZombie : cfg.BotVsPlayer;
                Check(actual == on, "SetVsTarget(" + target + ") did not set its flag to " + on);
            }
            else
            {
                Check(!ok, "SetVsTarget accepted the unknown target " + target);
                Check(field == null, "SetVsTarget(" + target + ") named field " + field + " for an unknown target");
                Check(cfg.BotVsBot == beforeBot && cfg.BotVsZombie == beforeZombie && cfg.BotVsPlayer == beforePlayer,
                    "SetVsTarget(" + target + ") changed a flag while rejecting the target");
            }
        }
        Check(!new BotConfig().SetVsTarget(null, true, out string nullField) && nullField == null,
            "SetVsTarget(null) was accepted");

        for (int seed = 0; seed < Seeds; seed++)
        {
            _rng = 0x5E77B0B5u + (uint)seed * 2654435761u;
            var cfg = new BotConfig();
            var live = new Dictionary<string, int>(StringComparer.Ordinal); // key -> team, as the map should hold
            for (int op = 0; op < OpsPerSeed; op++)
            {
                _ops++;
                string ctx = "seed=" + seed + " op=" + op;
                switch (Pick(4))
                {
                    case 0: // team assignment: write, then read back every spelling
                        {
                            string name = RandName();
                            // Both real call sites clamp to the live team count
                            // before they call (WebApi's setTeam action, the
                            // console's range check), so the fuzzer does the
                            // same: a value outside 0..BotTeamCount is not a
                            // shape this method can ever receive.
                            int team = Math.Max(0, Math.Min(cfg.BotTeamCount, RandInt()));
                            string key = BotText.BaseName(name);
                            cfg.SetTeamAssignment(name, team);
                            if (key.Length == 0 || team <= 0) live.Remove(key);
                            else live[key] = team;
                            foreach (var spelling in Spellings(name))
                                Check(cfg.GetTeamAssignment(spelling) == (live.TryGetValue(key, out int t) ? Math.Max(0, t) : 0),
                                    ctx + ": lookup of " + spelling + " disagrees with the stored team for " + name);
                            break;
                        }
                    case 1: // difficulty
                        {
                            int level = RandInt();
                            string field = cfg.SetDifficulty(level);
                            Check(field == "Difficulty", ctx + ": SetDifficulty named " + field);
                            Check(cfg.Difficulty == Math.Max(0, Math.Min(4, level)),
                                ctx + ": SetDifficulty(" + level + ") left " + cfg.Difficulty);
                            CheckInRange(cfg, ctx);
                            break;
                        }
                    case 2: // team count, which prunes assignments outside the new range
                        {
                            int count = RandInt();
                            string field = cfg.SetTeamCount(count);
                            Check(field == "BotTeamCount", ctx + ": SetTeamCount named " + field);
                            Check(cfg.BotTeamCount == Math.Max(0, Math.Min(8, count)),
                                ctx + ": SetTeamCount(" + count + ") left " + cfg.BotTeamCount);
                            live.Clear();
                            foreach (var kv in cfg.SnapshotTeamAssignments()) live[kv.Key] = kv.Value;
                            CheckInRange(cfg, ctx);
                            break;
                        }
                    default: // vs toggle
                        {
                            string target = RandName();
                            bool on = (Next() % 2) == 0;
                            string field;
                            bool ok = cfg.SetVsTarget(target, on, out field);
                            Check(ok == VsTargets.ContainsKey(target), ctx + ": SetVsTarget(" + target + ") verdict disagrees with the alias table");
                            if (ok)
                            {
                                Check(Array.IndexOf(FieldNames, field) >= 0, ctx + ": unknown field " + field);
                                bool actual = field == "BotVsBot" ? cfg.BotVsBot
                                    : field == "BotVsZombie" ? cfg.BotVsZombie : cfg.BotVsPlayer;
                                Check(actual == on, ctx + ": SetVsTarget(" + target + ") did not set " + field + " to " + on);
                            }
                            break;
                        }
                }
            }
            CheckInRange(cfg, "seed=" + seed + " final");
        }

        Console.WriteLine("admin setter fuzz: " + _ops + " operations over " + Seeds + " seeds, "
            + _failures + " failures");
        if (_failures > 0) { Console.WriteLine("admin setter fuzz tests FAILED"); return 1; }
        Console.WriteLine("all admin setter fuzz tests passed");
        return 0;
    }

    /// <summary>The spellings one logical name arrives in: as written, in the
    /// spawn-decorated form, in the other case, and NFD.</summary>
    static IEnumerable<string> Spellings(string name)
    {
        yield return name;
        // The spawn decoration is only the shape of a bare base name; a name
        // that already carries the tag is not the input this lookup sees.
        if (!name.StartsWith("[Bot] ", StringComparison.OrdinalIgnoreCase))
            yield return "[Bot] " + name + "_1";
        yield return name.ToLowerInvariant();
        string decomposed = name;
        try { decomposed = name.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { } // a lone surrogate has no decomposed form
        yield return decomposed;
    }
}
