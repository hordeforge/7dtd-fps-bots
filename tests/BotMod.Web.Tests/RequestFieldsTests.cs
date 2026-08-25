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
        Check("fuzz: " + reads + " adversarial field reads without throw or nondeterminism", true);

        return Finish();
    }

    static int Finish()
    {
        Console.WriteLine(_failures == 0 ? "all request-fields tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
