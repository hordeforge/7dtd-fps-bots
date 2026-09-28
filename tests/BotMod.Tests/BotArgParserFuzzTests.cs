// BotArgParserFuzzTests: randomized fuzzing of the `bot spawn` / `bot player`
// positional-tail grammar (BotArgParser). Tokens arrive from the admin console
// as free text; whatever the list, TryParseSpawn/TryParsePlayer must never
// throw, either fail with a non-empty usage error or succeed with a clamped
// count, a weapon token only when it looks like one, coordinates only where
// the grammar allows them, and identical results on re-parse. Complements the
// deterministic grammar pins in BotArgParserTests with adversarial shapes
// (empty strings, NUL/unicode junk, numeric overflow, misplaced weapons).
//
// Pure BCL; compiled and run by scripts/test-idempotency.sh:
//
//   bash scripts/test-idempotency.sh
using System;
using System.Collections.Generic;
using BotMod.Commands;

static class BotArgParserFuzzTests
{
    const int Iterations = 4000;

    static int _failures;
    static int _cases;

    static void Check(bool ok, string detail)
    {
        if (!ok)
        {
            _failures++;
            Console.WriteLine("FAIL " + detail);
        }
    }

    // ---- token generation ----

    static readonly string[] TokenPool =
    {
        "3", "0", "-2", "16", "17", "99999999999999999999", "-99999999999999",
        "1200.5", "-45.25", ".5", "+7", "1e10", "0x10", "NaN", "Infinity",
        "gunMGT1AK47", "mixed", "GUN", "gun", "gunShotgunT1DoubleBarrel",
        "abc", "", " ", "  ", "--count", "-", "_", "[Bot] Kira_42",
        "\u0000", "\t", "\u00e9\u4e2d\u6587", "\ud83d\ude00", new string('9', 40)
    };

    static string RandomToken(Random rng)
    {
        const string alphabet = "0123456789.-+egunmixcd \t";
        if (rng.Next(8) == 0)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = rng.Next(1, 12); i > 0; i--)
                sb.Append(alphabet[rng.Next(alphabet.Length)]);
            return sb.ToString();
        }
        return TokenPool[rng.Next(TokenPool.Length)];
    }

    /// <summary>Pin every documented postcondition of one parse call.</summary>
    static void CheckParse(bool spawn, IReadOnlyList<string> args, int start, string ctx)
    {
        _cases++;
        // Identical sentinel values on both calls: out params are always
        // written by the parser (the player wrapper discards coordinates),
        // so any divergence must come from the parser itself. Floats compare
        // via Equals: NaN coordinates must round-trip identically.
        int count = -1, count2 = -1;
        float x = float.MinValue, z = float.MinValue, x2 = float.MinValue, z2 = float.MinValue;
        bool hasPos = false, hasPos2 = false, ok, ok2;
        string weapon = "sentinel", weapon2 = "sentinel", error = "sentinel", error2 = "sentinel";

        try
        {
            ok = spawn
                ? BotArgParser.TryParseSpawn(args, start, out count, out x, out z, out hasPos, out weapon, out error)
                : BotArgParser.TryParsePlayer(args, start, out count, out weapon, out error);
            // Determinism: the same input must re-parse to identical results.
            ok2 = spawn
                ? BotArgParser.TryParseSpawn(args, start, out count2, out x2, out z2, out hasPos2, out weapon2, out error2)
                : BotArgParser.TryParsePlayer(args, start, out count2, out weapon2, out error2);
        }
        catch (Exception ex)
        {
            Check(false, ctx + ": parse threw " + ex.GetType().Name + ": " + ex.Message);
            return;
        }

        // Determinism covers what each wrapper writes: ok/count/weapon/error
        // for both, plus coordinates only for the spawn grammar.
        bool deterministic = ok == ok2 && count == count2 && weapon == weapon2 && error == error2
            && (!spawn || (x.Equals(x2) && z.Equals(z2) && hasPos == hasPos2));
        Check(deterministic,
            ctx + ": re-parse diverged: ok " + ok + "/" + ok2 + " count " + count + "/" + count2);

        if (ok)
        {
            Check(error == null, ctx + ": success carried an error: '" + error + "'");
            Check(count >= BotArgParser.MinSpawnCount && count <= BotArgParser.MaxSpawnCount,
                ctx + ": unclamped count " + count);
            Check(weapon == null || BotArgParser.LooksLikeWeapon(weapon),
                ctx + ": accepted non-weapon token as weapon: '" + weapon + "'");
            if (!spawn) Check(!hasPos, ctx + ": player grammar produced coordinates");
        }
        else
        {
            Check(!string.IsNullOrEmpty(error), ctx + ": failure without an error message");
            Check(error != null && error.Contains("Usage:"), ctx + ": failure lacks usage hint: '" + error + "'");
        }
    }

    static string Show(IReadOnlyList<string> args, int start)
    {
        var parts = new List<string>();
        for (int i = start; i < args.Count; i++) parts.Add(args[i] == null ? "<null>" : "'" + args[i] + "'");
        return "[" + string.Join(", ", parts) + "] start=" + start;
    }

    static int Main()
    {
        var rng = new Random(20260823);

        // Grammar cross-pins the fuzzer relies on for its success invariants.
        Check(BotArgParser.LooksLikeWeapon("gunX") && BotArgParser.LooksLikeWeapon("MIXED"),
            "LooksLikeWeapon misses gun-prefix/mixed");
        Check(!BotArgParser.LooksLikeWeapon(null) && !BotArgParser.LooksLikeWeapon("mix"),
            "LooksLikeWeapon accepts non-weapon");

        for (int i = 0; i < Iterations; i++)
        {
            int len = rng.Next(0, 7);
            var args = new List<string>(len + 2);
            for (int j = 0; j < len; j++)
                args.Add(rng.Next(12) == 0 ? null : RandomToken(rng));
            // Vary the start offset too: callers pass 1 or 2 past subcommand;
            // len and past-len cover the empty-tail boundary.
            int start = len == 0 ? rng.Next(3) : rng.Next(Math.Min(4, len + 1) + 1);

            string ctx = "#" + i + " " + Show(args, start);
            CheckParse(true, args, start, "spawn " + ctx);
            CheckParse(false, args, start, "player " + ctx);
        }

        Console.WriteLine("argparser fuzz: " + _cases + " parses across " + Iterations + " token lists");
        Console.WriteLine(_failures == 0 ? "all bot arg parser fuzz checks passed" : _failures + " fuzz check(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
