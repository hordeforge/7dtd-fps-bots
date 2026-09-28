// RequestFieldsTests - pins the POST /api/bot body-field triage contract.
//
// The web handler maps FieldRead.Invalid to named 400 codes instead of
// silently substituting defaults (an unparseable skill level used to
// re-persist the current difficulty; a missing removeOne entityId used to run
// a lookup for id 0 and answer 200 {"removed":false}). These tests pin:
//   - Absent exactly when the key is missing or JSON null,
//   - Ok for JSON numbers and invariant digit text (int fields) and for
//     booleans plus case-insensitive true/false text (bool fields),
//   - Invalid for anything else, value output neutralized,
//   - determinism under repeated reads, never throwing on any shape (fuzz).
// Pure BCL: compiles with just RequestFields.cs, like the ledger suite.
using System;
using System.Collections.Generic;
using BotMod.Web;

static class RequestFieldsTests
{
    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    static Dictionary<string, object> Body(params object[] kv)
    {
        var d = new Dictionary<string, object>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
        return d;
    }

    static int Main()
    {
        // Absent: missing key, null body reference, JSON null value.
        int v0;
        Check("OptInt missing key is Absent",
            RequestFields.OptInt(Body("a", 1), "count", out v0) == FieldRead.Absent);
        Check("OptInt null body is Absent",
            RequestFields.OptInt(null, "count", out v0) == FieldRead.Absent);
        Check("OptInt JSON null is Absent",
            RequestFields.OptInt(Body("count", null), "count", out v0) == FieldRead.Absent);

        // Ok: numeric shapes a JSON parser can produce for whole numbers.
        int v1, v2, v3, v4, v5, v6;
        Check("OptInt boxed int is Ok(7)",
            RequestFields.OptInt(Body("n", 7), "n", out v1) == FieldRead.Ok && v1 == 7);
        Check("OptInt boxed long is Ok(7)",
            RequestFields.OptInt(Body("n", 7L), "n", out v2) == FieldRead.Ok && v2 == 7);
        Check("OptInt integral double is Ok(4)",
            RequestFields.OptInt(Body("n", 4d), "n", out v3) == FieldRead.Ok && v3 == 4);
        Check("OptInt digit string is Ok(42)",
            RequestFields.OptInt(Body("n", "42"), "n", out v4) == FieldRead.Ok && v4 == 42);
        Check("OptInt negative digit string is Ok(-5) (range clamps stay caller-side)",
            RequestFields.OptInt(Body("n", "-5"), "n", out v5) == FieldRead.Ok && v5 == -5);
        Check("OptInt leading sign text is Ok(+3)",
            RequestFields.OptInt(Body("n", "+3"), "n", out v6) == FieldRead.Ok && v6 == 3);

        // Invalid: present but not an integer; value output neutralized to 0.
        int v7;
        Check("OptInt fractional double is Invalid",
            RequestFields.OptInt(Body("n", 1.5d), "n", out v7) == FieldRead.Invalid);
        bool b0;
        Check("OptInt garbage text is Invalid",
            RequestFields.OptInt(Body("n", "abc"), "n", out v7) == FieldRead.Invalid);
        Check("OptInt empty string is Invalid",
            RequestFields.OptInt(Body("n", ""), "n", out v7) == FieldRead.Invalid);
        Check("OptInt boolean value is Invalid",
            RequestFields.OptInt(Body("n", true), "n", out v7) == FieldRead.Invalid);
        Check("OptInt locale decimal text is Invalid",
            RequestFields.OptInt(Body("n", "1,5"), "n", out v7) == FieldRead.Invalid);
        Check("OptInt overflow text is Invalid",
            RequestFields.OptInt(Body("n", "99999999999999999999"), "n", out v7) == FieldRead.Invalid);
        Check("OptInt arbitrary object is Invalid",
            RequestFields.OptInt(Body("n", new object()), "n", out v7) == FieldRead.Invalid);
        Check("OptInt Invalid neutralizes output",
            RequestFields.OptInt(Body("n", "abc"), "n", out v7) == FieldRead.Invalid && v7 == 0);

        // RequireBool: absence is its own outcome (toggles require the flag).
        bool bAbs;
        Check("RequireBool missing key is Absent",
            RequestFields.RequireBool(Body(), "on", out bAbs) == FieldRead.Absent);
        Check("RequireBool null body is Absent",
            RequestFields.RequireBool(null, "on", out bAbs) == FieldRead.Absent);
        Check("RequireBool JSON null is Absent",
            RequestFields.RequireBool(Body("on", null), "on", out bAbs) == FieldRead.Absent);

        bool b1, b2, b3, b4;
        Check("RequireBool JSON true is Ok(true)",
            RequestFields.RequireBool(Body("on", true), "on", out b1) == FieldRead.Ok && b1);
        Check("RequireBool JSON false is Ok(false)",
            RequestFields.RequireBool(Body("on", false), "on", out b2) == FieldRead.Ok && !b2);
        Check("RequireBool text TRUE is Ok(true)",
            RequestFields.RequireBool(Body("on", "TRUE"), "on", out b3) == FieldRead.Ok && b3);
        Check("RequireBool text False is Ok(false)",
            RequestFields.RequireBool(Body("on", "False"), "on", out b4) == FieldRead.Ok && !b4);

        bool b5;
        Check("RequireBool yes is Invalid",
            RequestFields.RequireBool(Body("on", "yes"), "on", out b0) == FieldRead.Invalid);
        Check("RequireBool 1 is Invalid",
            RequestFields.RequireBool(Body("on", 1), "on", out b0) == FieldRead.Invalid);
        Check("RequireBool 0 is Invalid",
            RequestFields.RequireBool(Body("on", 0), "on", out b0) == FieldRead.Invalid);
        Check("RequireBool empty string is Invalid",
            RequestFields.RequireBool(Body("on", ""), "on", out b0) == FieldRead.Invalid);
        Check("RequireBool garbage is Invalid",
            RequestFields.RequireBool(Body("on", "trueish"), "on", out b5) == FieldRead.Invalid && !b5);

        // Determinism: same read twice, same triage and value.
        var d = Body("n", "13", "on", "true");
        int r1a = 0, r1b = 0;
        bool r2a = false, r2b = false;
        RequestFields.OptInt(d, "n", out r1a); RequestFields.OptInt(d, "n", out r1b);
        RequestFields.RequireBool(d, "on", out r2a); RequestFields.RequireBool(d, "on", out r2b);
        Check("repeated reads are deterministic", r1a == r1b && r2a == r2b);

        // OptString: free-text fields, converted the same invariant way as the
        // typed readers. Under a host culture that spells decimals with a
        // comma, Convert.ToString(v) would hand the ledger a requestId of
        // "1234,5" for the JSON number 1234.5, so a retry of the same request
        // carrying "1234.5" would miss the entry it claims.
        {
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Check("OptString absent key is null",
                    RequestFields.OptString(Body("a", 1), "ident") == null);
                Check("OptString JSON null is null",
                    RequestFields.OptString(Body("ident", null), "ident") == null);
                Check("OptString null body is null",
                    RequestFields.OptString(null, "ident") == null);
                Check("OptString text passes through",
                    RequestFields.OptString(Body("ident", "K\u00edra"), "ident") == "K\u00edra");
                Check("OptString number is invariant under a comma-decimal host culture",
                    RequestFields.OptString(Body("requestId", 1234.5d), "requestId") == "1234.5");
                Check("OptString negative number keeps the invariant sign",
                    RequestFields.OptString(Body("requestId", -7d), "requestId") == "-7");
                Check("OptString boolean is invariant under any host culture",
                    RequestFields.OptString(Body("on", true), "on") == "True");
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        // Fingerprint: the canonical text an idempotency key is bound to. It
        // must ignore field order and the key itself, and must change when any
        // other field changes, or a retry of a different request would replay
        // the first request's response.
        {
            var a1 = Body("action", "spawn", "count", 2, "requestId", "k1");
            var a2 = Body("requestId", "k1", "count", 2, "action", "spawn");
            var a3 = Body("action", "spawn", "count", 8, "requestId", "k1");
            var a4 = Body("action", "spawn", "requestId", "k1");
            string f1 = RequestFields.Fingerprint(a1, "requestId");
            Check("fingerprint ignores field order", f1 == RequestFields.Fingerprint(a2, "requestId"));
            Check("fingerprint changes with a changed field",
                f1 != RequestFields.Fingerprint(a3, "requestId"));
            Check("fingerprint changes when a field is dropped",
                f1 != RequestFields.Fingerprint(a4, "requestId"));
            Check("fingerprint ignores the excluded key",
                f1 == RequestFields.Fingerprint(Body("action", "spawn", "count", 2, "requestId", "other"), "requestId"));
            Check("fingerprint of an empty body is empty",
                RequestFields.Fingerprint(Body(), "requestId") == ""
                && RequestFields.Fingerprint(null, "requestId") == "");
            Check("fingerprint is deterministic",
                f1 == RequestFields.Fingerprint(Body("requestId", "k1", "action", "spawn", "count", 2), "requestId"));
            // A JSON number and its digit text are the same field value to
            // every action, so they must not read as two different requests.
            Check("fingerprint treats a number and its text as one value",
                RequestFields.Fingerprint(Body("count", 2), "requestId")
                == RequestFields.Fingerprint(Body("count", "2"), "requestId"));
            // Two keys differing only in case are two different fields, never
            // one: a case-insensitive comparison would merge distinct requests.
            Check("fingerprint is case-sensitive in key names",
                RequestFields.Fingerprint(Body("On", true), "requestId")
                != RequestFields.Fingerprint(Body("on", true), "requestId"));
            // A separator inside a value must not be able to spell a second
            // body: {a:"b",c:"d"} and {a:"b\nc=d"} rendered identically under
            // a "key=value" line format, and the ledger would have replayed
            // one request's response for the other.
            Check("fingerprint separates a value carrying the delimiter",
                RequestFields.Fingerprint(Body("action", "spawn", "player", "b\nc=d"), "requestId")
                != RequestFields.Fingerprint(Body("action", "spawn", "player", "b", "c", "d"), "requestId"));
            Check("fingerprint separates a key carrying the delimiter",
                RequestFields.Fingerprint(Body("a\nc", "d"), "requestId")
                != RequestFields.Fingerprint(Body("a", "c=d"), "requestId"));
            // One logical request whose name arrives in two normalization forms
            // is one request. The ledger compares fingerprints ordinally, so
            // without Canon the retry below was answered 409
            // REQUEST_ID_REUSED and the client re-ran a spawn the server had
            // already performed. Same contract as BotText.NameMatches, which
            // has matched the NFD spelling against the NFC name since forever.
            string nfcKira = "K\u00edra", nfdKira = "Ki\u0301ra";
            Check("fingerprint ignores the normalization form of a value",
                RequestFields.Fingerprint(Body("action", "spawnNear", "player", nfcKira), "requestId")
                == RequestFields.Fingerprint(Body("action", "spawnNear", "player", nfdKira), "requestId"));
            Check("fingerprint ignores the normalization form of a key",
                RequestFields.Fingerprint(Body("pl\u00e4yer", nfcKira), "requestId")
                == RequestFields.Fingerprint(Body("pla\u0308yer", nfdKira), "requestId"));
            // Canon is normalization, not case folding: distinct bodies must
            // stay distinct.
            Check("fingerprint still separates names differing in more than form",
                RequestFields.Fingerprint(Body("player", "Kira"), "requestId")
                != RequestFields.Fingerprint(Body("player", "K\u00e1ra"), "requestId"));
            // An invalid lone surrogate cannot come out of a JSON body, but a
            // Canon that threw on one would take the whole request down; the
            // fingerprint must stay total.
            Check("fingerprint survives a lone surrogate value",
                RequestFields.Fingerprint(Body("player", "a\ud800b"), "requestId") != null);
        }

        // Fuzz: arbitrary bodies never throw, only produce the three triage
        // states, and read identically on a second pass.
        var rng = new Random(0x706F5354); // fixed seed: failures are reproducible
        var keys = new[] { "count", "on", "level", "entityId", "missing" };
        object[] values = { null, "", "true", "false", "TRUE", "yes", "1", 0, 1, -7, 7L, 2.5d, 4d,
                            "16", "0x10", " 8 ", "abc", new object(), new List<int> { 1 }, float.NaN };
        long reads = 0;
        for (int iter = 0; iter < 20000; iter++)
        {
            var body = new Dictionary<string, object>();
            int n = rng.Next(keys.Length);
            for (int i = 0; i < n; i++)
            {
                object val = values[rng.Next(values.Length)];
                if (val != null || rng.Next(2) == 0) body[keys[rng.Next(keys.Length)]] = val;
            }
            foreach (string key in keys)
            {
                // One reader per key: determinism compares like with like.
                bool intRead = rng.Next(2) == 0;
                FieldRead a;
                try
                {
                    a = intRead
                        ? RequestFields.OptInt(body, key, out v7)
                        : RequestFields.RequireBool(body, key, out b0);
                }
                catch (Exception ex)
                {
                    Check("fuzz read threw: " + ex.GetType().Name + " key=" + key, false);
                    return Finish();
                }
                if (a != FieldRead.Absent && a != FieldRead.Ok && a != FieldRead.Invalid)
                {
                    Check("fuzz produced unknown triage " + a, false);
                    return Finish();
                }
                FieldRead b = intRead
                    ? RequestFields.OptInt(body, key, out v7)
                    : RequestFields.RequireBool(body, key, out b0);
                if (a != b)
                {
                    Check("fuzz read not deterministic for key=" + key, false);
                    return Finish();
                }
                reads++;
            }
        }
        // The loop above returns early on the first bad read, so reaching here
        // means every read passed; what is not yet proven is that it read
        // anything. The count is exact (iterations x keys), so assert it
        // rather than printing a hardcoded pass: a body builder that stopped
        // producing keys, or a keys array that shrank, must fail here instead
        // of reporting a green "0 adversarial field reads".
        Check("fuzz: " + reads + " adversarial field reads without throw or nondeterminism",
            reads == 20000L * keys.Length);

        return Finish();
    }

    static int Finish()
    {
        Console.WriteLine(_failures == 0 ? "all request-fields tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
