// LogSanitizerTests: pins the log-injection guard for untrusted request
// fields echoed into server log lines: control characters (C0, DEL, C1) are
// replaced so a crafted "requestId"/"action" cannot forge or restructure
// audit lines; printable text including non-ASCII passes through unchanged.
// Pure BCL; compiled and run by scripts/test-idempotency.sh.
//
//   bash scripts/test-idempotency.sh
using System;
using System.Text;
using BotMod.Config;

static class LogSanitizerTests
{
    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in (s ?? ""))
            sb.Append(c < ' ' || (c >= '\x7f' && c <= '\x9f')
                ? "\\u" + ((int)c).ToString("x4")
                : c.ToString());
        return sb.ToString();
    }

    static bool HasControls(string s)
    {
        foreach (char c in s ?? "")
            if (c < ' ' || (c >= '\x7f' && c <= '\x9f')) return true;
        return false;
    }

    static int Main()
    {
        // 1. Printable ASCII passes through untouched.
        {
            string v = LogSanitizer.Clean("botmod-abc123-xyz");
            Check("printable ascii unchanged", v == "botmod-abc123-xyz");
        }

        // 2. Empty/null inputs yield an empty string.
        Check("empty string stays empty", LogSanitizer.Clean("") == "");
        Check("null becomes empty", LogSanitizer.Clean(null) == "");

        // 3. CR/LF cannot split or forge log lines.
        {
            string v = LogSanitizer.Clean("ok in 0ms\n[BotMod] forged line\r\nanother");
            Check("CR/LF replaced", v == "ok in 0ms?[BotMod] forged line??another");
            Check("no control chars remain after CRLF scrub", !HasControls(v));
        }

        // 4. Terminal escapes are neutralized.
        {
            string v = LogSanitizer.Clean("\x1b[31mred\u009b[0m");
            Check("ANSI CSI/C1 escapes replaced", v == "?[31mred?[0m");
            Check("no control chars remain after escape scrub", !HasControls(v));
        }

        // 5. Tab is also a control character and gets replaced.
        Check("tab replaced", LogSanitizer.Clean("a\tb") == "a?b");

        // 6. Non-ASCII printable text survives.
        {
            string v = LogSanitizer.Clean("spawn near Jäger_42 ✓");
            Check("non-ascii printable preserved", v == "spawn near Jäger_42 ✓");
        }

        // 7. Boundary code points: 0x20 space kept, 0x1f/0x7f replaced, 0xa0 nbsp kept.
        {
            string v = LogSanitizer.Clean(new string(new[] { ' ', '\x1f', '\x7f', '\u00a0' }));
            Check("boundary code points handled", v == " ??\u00a0");
        }

        // 8. Invisible formatting characters are scrubbed: bidi overrides and
        // zero-width characters must not reach the audit trail from
        // request-supplied fields (they reorder or hide text when the log is
        // read in a terminal).
        {
            string v = LogSanitizer.Clean("admin\u202ename\u200b ok\u2060end\ufeff");
            Check("bidi/zero-width chars replaced", v == "admin?name? ok?end?");
            Check("sweep preserves length after invisible scrub", v.Length == "admin\u202ename\u200b ok\u2060end\ufeff".Length);
        }
        // Legit non-ASCII spacing/printables adjacent to that range survive.
        Check("em dash and nbsp survive", LogSanitizer.Clean("a\u2014b\u00a0c") == "a\u2014b\u00a0c");

        // 9. Whole-range invariant: output never contains scrubbable chars.
        {
            var raw = new StringBuilder();
            for (int i = 0; i < 0x2000; i++) raw.Append((char)i);
            raw.Append("tail");
            string v = LogSanitizer.Clean(raw.ToString());
            Check("sweep 0x0000-0x2000 leaves no controls", !HasControls(v));
            Check("sweep preserves length", v.Length == raw.Length);
        }

        Console.WriteLine(_failures == 0 ? "all log sanitizer tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
