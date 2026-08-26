// LogSanitizerFuzzTests: randomized fuzzing of the log-injection guard
// applied to every request-supplied string echoed into server log lines
// ("action", "requestId", spawnNear player names inside response bodies).
// Whatever the input, Clean must never throw, must return the input verbatim
// when nothing is scrubbable, must substitute (never drop) characters so log
// line structure stays attributable, must leave no control or invisible-format
// character in the output, and must be idempotent and deterministic.
// Complements the fixed-vector pins in LogSanitizerTests with random mixes of
// C0/C1 controls, bidi overrides, zero-width characters, surrogates and long
// printable runs. Pure BCL; compiled and run by scripts/test-idempotency.sh:
//
//   bash scripts/test-idempotency.sh
using System;
using System.Text;
using BotMod.Config;

static class LogSanitizerFuzzTests
{
    const int Iterations = 20000;

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

    /// <summary>The scrub predicate, restated independently from its two
    /// documented sources.</summary>
    static bool Scrubbable(char c)
    {
        return char.IsControl(c) || BotText.IsInvisible(c);
    }

    static bool AnyScrubbable(string s)
    {
        foreach (char c in s)
            if (Scrubbable(c)) return true;
        return false;
    }

    static string Show(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
            sb.Append(Scrubbable(c) ? "\\u" + ((int)c).ToString("x4") : c.ToString());
        return sb.ToString();
    }

    // ---- input generation ----

    static readonly char[] HotChars =
    {
        '\u0000', '\u0007', '\u001b', '\t', '\n', '\r', '\x7f', '\u0085', '\u009b',
        '\u200b', '\u200c', '\u200e', '\u202a', '\u202e', '\u2060', '\ufeff', '\ufe0f',
        'a', 'Z', '?', ' ', '\u00e9', '\u2014', '\u4e2d', '\ud83d', '\ude00'
    };

    static string RandomString(Random rng)
    {
        int len = rng.Next(0, 40);
        var sb = new StringBuilder(len);
        double roll = rng.NextDouble();
        for (int i = 0; i < len; i++)
        {
            if (roll < 0.45) sb.Append(HotChars[rng.Next(HotChars.Length)]);
            else if (roll < 0.70) sb.Append((char)rng.Next(0x10000));
            else sb.Append((char)(rng.Next(2) == 0 ? rng.Next(0x20) : rng.Next(0x2028, 0x2066)));
        }
        return sb.ToString();
    }

    static void CheckOne(string raw, string ctx)
    {
        _cases++;
        string clean, cleanAgain, cleanThird;
        try { clean = LogSanitizer.Clean(raw); }
        catch (Exception ex) { Check(false, ctx + ": threw " + ex.GetType().Name); return; }

        // Documented null handling: the empty string.
        if (raw == null)
        {
            Check(clean != null && clean.Length == 0, ctx + ": null did not become empty");
            return;
        }

        // Length stability: substitution, never deletion or insertion.
        Check(clean.Length == raw.Length,
            ctx + ": length " + clean.Length + " != input " + raw.Length);

        // Output purity: nothing scrubbable survives.
        Check(!AnyScrubbable(clean), ctx + ": scrubbable char survived: " + Show(clean));

        // Verbatim passthrough when nothing needed cleaning.
        if (!AnyScrubbable(raw))
            Check(string.Equals(clean, raw, StringComparison.Ordinal),
                ctx + ": clean input not returned verbatim");

        // Idempotence and determinism: a sanitized value is a fixed point,
        // and re-sanitizing never diverges.
        try
        {
            cleanAgain = LogSanitizer.Clean(raw);
            cleanThird = LogSanitizer.Clean(clean);
        }
        catch (Exception ex) { Check(false, ctx + ": repeat call threw " + ex.GetType().Name); return; }
        Check(string.Equals(clean, cleanAgain, StringComparison.Ordinal), ctx + ": not deterministic");
        Check(string.Equals(clean, cleanThird, StringComparison.Ordinal), ctx + ": not idempotent");
    }

    static int Main()
    {
        var rng = new Random(20260826);

        // Fixed anchors first: the exact shapes callers rely on.
        CheckOne(null, "null");
        CheckOne("", "empty");
        CheckOne("web api action=spawn req=abc-123 ok", "plain");
        CheckOne(new string('\n', 64), "crlf-run");
        CheckOne(new string('?', 4096), "long-printable");
        CheckOne("\u202e\u200b\ufeff\u0000", "all-hot");

        for (int i = 0; i < Iterations; i++)
        {
            string raw = RandomString(rng);
            CheckOne(raw, "#" + i + " <" + Show(raw) + ">");
        }

        Console.WriteLine("logsanitizer fuzz: " + _cases + " inputs");
        Console.WriteLine(_failures == 0 ? "all log sanitizer fuzz checks passed" : _failures + " fuzz check(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
