// LogRedactorTests - pins the personal-data guard for the /api/bot audit
// line. The line records what an action did; it must not record who the
// player was. spawnNear answers {"spawned":N,"found":bool,"player":"<name>"}
// and the name is personal data, so the log copy of the body carries the
// placeholder while the outcome fields survive. Pure BCL; compiled and run
// by scripts/test-idempotency.sh.
//
//   bash scripts/test-idempotency.sh logredact
using System;
using BotMod.Foundation;

static class LogRedactorTests
{
    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    static int Main()
    {
        // 1. The field the audit line actually carries: spawnNear's success
        // body, straight out of WebApi.RespondJson.
        {
            string v = LogRedactor.Redact("{\"spawned\":2,\"found\":true,\"player\":\"Kira\"}");
            Check("player name removed", v == "{\"spawned\":2,\"found\":true,\"player\":\"<player>\"}");
            Check("outcome fields kept", v.Contains("\"spawned\":2") && v.Contains("\"found\":true"));
        }

        // 2. The not-found body echoes the request's ident back. Same field,
        // same redaction: the operator supplied it, it still names a person.
        {
            string v = LogRedactor.Redact("{\"spawned\":0,\"found\":false,\"player\":\"NotOnline\"}");
            Check("not-found echo removed", !v.Contains("NotOnline"));
        }

        // 3. Bodies with no personal field are untouched, byte for byte, so
        // the audit line keeps the outcome it always recorded.
        foreach (string body in new[] {
            "{\"spawned\":1}",
            "{\"removed\":3}",
            "{\"difficulty\":3}",
            "{\"team\":2,\"on\":true}",
            "{\"cleared\":true}",
            "{\"name\":\"Grunt_42\",\"team\":1}",
            "{\"neural\":true,\"loaded\":true,\"reason\":\"ok\"}",
        })
            Check("unchanged: " + body, LogRedactor.Redact(body) == body);

        // 4. A bot name is not personal data. Only the keys that code proves
        // carry a person are redacted, so "name" must survive or the log
        // stops recording which bot a team assignment touched.
        Check("bot name kept",
            LogRedactor.Redact("{\"name\":\"Grunt_42\",\"team\":1}") == "{\"name\":\"Grunt_42\",\"team\":1}");

        // 5. A player name hidden behind an escape in the key is still the
        // same key, and the name still goes.
        {
            string v = LogRedactor.Redact("{\"pl\\u0061yer\":\"Kira\"}");
            Check("escaped key still redacted", !v.Contains("Kira"));
        }

        // 6. A value carrying a colon, a quote or a bracket is one token and
        // must not let the scanner resume mid-value and leak a tail.
        {
            string v = LogRedactor.Redact("{\"player\":\"Ki\\\"ra\",\"found\":true}");
            Check("escaped quote inside value redacted whole", !v.Contains("ra\"") && v.Contains("\"player\":\"<player>\""));
            string w = LogRedactor.Redact("{\"player\":{\"name\":\"Kira\"},\"found\":true}");
            Check("object value redacted whole", !w.Contains("Kira") && w.Contains("\"found\":true"));
        }

        // 7. Whitespace around the colon, and a null value, still redact.
        Check("spaced colon redacted", !LogRedactor.Redact("{\"player\" : \"Kira\"}").Contains("Kira"));
        Check("null value redacted", LogRedactor.Redact("{\"player\":null}") == "{\"player\":\"<player>\"}");

        // 8. Total on hostile input: nothing may throw, because a throwing
        // scrubber would drop the audit line or, worse, log the raw body.
        foreach (string bad in new[] {
            "", null, "\"", "{\"player\"", "{\"player\":\"", "{\"player\":", "{",
            "{\"player\":{", "{\"player\":[", "{\"player\":\"Kira", "{\"\\\":1}",
            "{\"player\":\"\\u00\"}", "{\"player\":\"a\\", "player",
        })
        {
            string v;
            try { v = LogRedactor.Redact(bad); }
            catch (Exception ex) { Check("no throw for: " + bad, false); Console.WriteLine("       " + ex); continue; }
            Check("returns a string for: " + (bad ?? "null"), v != null);
        }

        // 9. Idempotent: redacting a redacted body is a no-op, so a line
        // that is redacted twice (or replayed) does not grow placeholders.
        {
            string once = LogRedactor.Redact("{\"spawned\":2,\"player\":\"Kira\"}");
            Check("idempotent", LogRedactor.Redact(once) == once);
        }

        // 10. The key predicate is the contract the audit line rests on, so
        // it is asserted directly rather than only through Redact.
        Check("player is a personal key", LogRedactor.IsPersonalKey("player"));
        Check("name is not a personal key", !LogRedactor.IsPersonalKey("name"));
        Check("key match is case sensitive", !LogRedactor.IsPersonalKey("Player"));
        Check("prefix is not a personal key", !LogRedactor.IsPersonalKey("playerName"));

        if (_failures > 0)
        {
            Console.WriteLine(_failures + " log redactor test(s) failed");
            return 1;
        }
        Console.WriteLine("all log redactor tests passed");
        return 0;
    }
}
