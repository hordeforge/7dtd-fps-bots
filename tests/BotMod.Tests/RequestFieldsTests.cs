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
//   - determinism under repeated reads, never throwing on any shape (fuzz),
//   - Fingerprint round-trips and never merges two different bodies (fuzz).
// Pure BCL: compiles with just RequestFields.cs, like the ledger suite.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BotMod.Foundation;
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

    // ---- Fingerprint collision fuzz ----
    //
    // Fingerprint is the trust boundary the idempotency ledger compares: a
    // retry whose fingerprint matches a live key replays that entry's recorded
    // response instead of executing. A collision there does not merely look
    // odd, it answers one operation with another operation's result. The
    // fixed vectors above pin a handful of shapes by hand; this fuzzes the
    // property the encoding rests on, over bodies built from the characters
    // that reopen it (the '='/':' delimiters, NUL and C0 controls, lone
    // surrogates, combining marks that Canon composes, invariant-decimal
    // numbers that must read as their digit text):
    //
    //   round-trip: the emitted text parses back, with a parser written here
    //     rather than shared with the writer, to exactly the canonical
    //     key/value pairs that went in. This is the strong property: a writer
    //     that drops, truncates, reorders or merges a field cannot also parse
    //     back to the input, so one check covers all of them. A writer/reader
    //     disagreement, a length counted in the wrong unit, or a field
    //     shortened behind its own length prefix surfaces here as a parse
    //     mismatch rather than as a silent merge,
    //   equivalence: over a pool of distinct bodies, two fingerprints are
    //     equal exactly when the model says the pair lists are equal, in both
    //     directions. Exact round-trip already implies this, so the pool is
    //     the cheap backstop that names the offending pair of bodies instead
    //     of just the first mismatching text, and it is what a future change
    //     that loosens the round-trip check would fall back to,
    //   totality: no shape throws and every answer is deterministic.
    //
    // A body carries the invariant text of each value alongside the boxed
    // object, so the model never has to re-derive RequestFields' own
    // conversion; the conversion itself is pinned separately, below, by
    // comparing the table against what OptString reports.

    static void Fail(string detail)
    {
        _failures++;
        Console.WriteLine("FAIL " + detail);
    }

    /// <summary>One boxed JSON value and the invariant text RequestFields.Raw
    /// is specified to render it as. Null text marks a JSON null, which Raw
    /// reports as absent and Fingerprint spells "null".</summary>
    sealed class FuzzValue
    {
        public readonly object Obj;
        public readonly string Text;

        public FuzzValue(object obj, string text)
        {
            Obj = obj;
            Text = text;
        }
    }

    sealed class FuzzBody
    {
        public readonly List<string> Order = new List<string>();
        public readonly Dictionary<string, object> Raw = new Dictionary<string, object>();
        public readonly Dictionary<string, string> Text = new Dictionary<string, string>();
    }

    static readonly FuzzValue[] FuzzValues =
    {
        new FuzzValue(null, null),
        new FuzzValue("", ""),
        new FuzzValue("spawn", "spawn"),
        new FuzzValue("true", "true"),
        new FuzzValue("2", "2"),
        new FuzzValue("16", "16"),
        new FuzzValue("abc", "abc"),
        // Values that spell a second pair under a naive "key=value" format.
        new FuzzValue("b\nc=d", "b\nc=d"),
        new FuzzValue("=;", "=;"),
        new FuzzValue("1:a=1:b", "1:a=1:b"),
        new FuzzValue("a=b;c", "a=b;c"),
        new FuzzValue("a\0b", "a\0b"),
        new FuzzValue("a\tb", "a\tb"),
        // Normalization: Canon composes the NFD spelling, so both must
        // fingerprint alike and must not collapse a third, distinct name.
        new FuzzValue("K\u00edra", "K\u00edra"),
        new FuzzValue("Ki\u0301ra", "Ki\u0301ra"),
        new FuzzValue("K\u00e1ra", "K\u00e1ra"),
        // Malformed UTF-16 a JSON decoder can hand over: Canon must stay
        // total rather than taking the request down.
        new FuzzValue("a\ud800b", "a\ud800b"),
        new FuzzValue("\udfff\ud800", "\udfff\ud800"),
        new FuzzValue(true, "True"),
        new FuzzValue(false, "False"),
        new FuzzValue(0, "0"),
        new FuzzValue(7, "7"),
        new FuzzValue(-7, "-7"),
        new FuzzValue(16, "16"),
        new FuzzValue(7L, "7"),
        new FuzzValue(4d, "4"),
        new FuzzValue(2.5d, "2.5"),
    };

    static readonly string[] FuzzKeys =
    {
        "action", "count", "on", "player", "requestId", "a", "b", "c",
        "a\nc", "a=b", "a:b", "=;", "1:a", "0", "", " ", "On", "on",
        "K\u00ed", "K\u0069\u0301", "\u0000k", "count ",
    };

    /// <summary>Independent decoder for the length-prefixed pair text. Written
    /// from the format description, not from the writer, so the two can
    /// disagree. The text is a run of "len:key=value" fields with nothing
    /// between them, so each field's declared length is the only thing that
    /// says where the next one starts; that is exactly the assumption worth
    /// checking, and a length counted in the wrong unit breaks it.</summary>
    static bool RefParse(string fp, out List<string[]> pairs)
    {
        pairs = new List<string[]>();
        int i = 0;
        while (i < fp.Length)
        {
            string key, value;
            if (!RefField(fp, ref i, out key)) return false;
            if (i >= fp.Length || fp[i] != '=') return false;
            i++;
            if (!RefField(fp, ref i, out value)) return false;
            pairs.Add(new[] { key, value });
        }
        return true;
    }

    /// <summary>Reads one "len:text" field at <paramref name="i"/>, leaving
    /// the index on the first character past the field.</summary>
    static bool RefField(string s, ref int i, out string field)
    {
        field = null;
        int colon = s.IndexOf(':', i);
        if (colon < 0) return false;
        int len;
        // NumberStyles.None: the count is a bare run of digits. A sign or
        // surrounding space here would be a format the writer never emits.
        if (!int.TryParse(s.Substring(i, colon - i), NumberStyles.None, CultureInfo.InvariantCulture, out len)
            || len < 0)
            return false;
        int start = colon + 1;
        if (start + len > s.Length) return false;
        field = s.Substring(start, len);
        i = start + len;
        return true;
    }

    /// <summary>The pairs the fingerprint must carry: every non-excluded key
    /// of the body, ordered the way the writer orders them, each name and
    /// value in its canonical form. Built from the dictionary rather than the
    /// generator's insertion order, because that dictionary is the key set the
    /// writer actually enumerates; reading a different one would compare the
    /// model against a body the writer never saw.</summary>
    static List<string[]> ModelPairs(FuzzBody b, string excludeKey)
    {
        var keys = new List<string>();
        foreach (string k in b.Raw.Keys)
            if (k != excludeKey) keys.Add(k);
        keys.Sort(StringComparer.Ordinal);
        var pairs = new List<string[]>();
        for (int i = 0; i < keys.Count; i++)
        {
            pairs.Add(new[]
            {
                BotText.Canon(keys[i]),
                BotText.Canon(b.Text[keys[i]] ?? "null"),
            });
        }
        return pairs;
    }

    static bool SamePairs(List<string[]> a, List<string[]> c)
    {
        if (a.Count != c.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i][0] != c[i][0] || a[i][1] != c[i][1]) return false;
        return true;
    }

    static FuzzBody RandomBody(Random rng)
    {
        var b = new FuzzBody();
        int n = rng.Next(0, 5);
        var pool = new List<string>(FuzzKeys);
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            string tmp = pool[i]; pool[i] = pool[j]; pool[j] = tmp;
        }
        for (int i = 0; i < n && i < pool.Count; i++)
        {
            FuzzValue v = FuzzValues[rng.Next(FuzzValues.Length)];
            b.Order.Add(pool[i]);
            b.Raw[pool[i]] = v.Obj;
            b.Text[pool[i]] = v.Text;
        }
        return b;
    }

    static FuzzBody Reordered(FuzzBody b, Random rng)
    {
        var r = new FuzzBody();
        var order = new List<string>(b.Order);
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            string tmp = order[i]; order[i] = order[j]; order[j] = tmp;
        }
        foreach (string k in order)
        {
            r.Order.Add(k);
            r.Raw[k] = b.Raw[k];
            r.Text[k] = b.Text[k];
        }
        return r;
    }

    /// <summary>Failure-message rendering. Iterates the dictionary's keys, the
    /// same set the writer sees, sorted so the message is stable.</summary>
    static string Show(FuzzBody b)
    {
        var keys = new List<string>(b.Raw.Keys);
        keys.Sort(StringComparer.Ordinal);
        var sb = new StringBuilder("{");
        for (int i = 0; i < keys.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append('<');
            foreach (char c in keys[i])
                sb.Append(c < 0x20 || c > 0x7e ? "\\x" + ((int)c).ToString("x4") : c.ToString());
            sb.Append('=');
            string t = b.Text[keys[i]];
            if (t == null) sb.Append("null");
            else
                foreach (char c in t)
                    sb.Append(c < 0x20 || c > 0x7e ? "\\x" + ((int)c).ToString("x4") : c.ToString());
            sb.Append('>');
        }
        return sb.Append('}').ToString();
    }

    static int FingerprintFuzz()
    {
        var rng = new Random(0x66697072); // fixed seed: failures are reproducible
        var pool = new List<FuzzBody>();
        var poolFp = new List<string>();
        var poolModel = new List<List<string[]>>();
        const int PoolSize = 12;
        long roundTrips = 0;
        long comparisons = 0;

        for (int iter = 0; iter < 20000; iter++)
        {
            FuzzBody b = RandomBody(rng);
            List<string[]> model = ModelPairs(b, "requestId");

            string fp;
            try
            {
                fp = RequestFields.Fingerprint(b.Raw, "requestId");
            }
            catch (Exception ex)
            {
                Fail("fingerprint fuzz threw " + ex.GetType().Name + " on " + Show(b));
                break;
            }
            if (fp == null)
            {
                Fail("fingerprint fuzz returned null on " + Show(b));
                break;
            }

            // Round-trip through the independent decoder.
            List<string[]> parsed;
            if (!RefParse(fp, out parsed))
            {
                Fail("fingerprint text does not parse: <" + fp + "> for " + Show(b));
                break;
            }
            if (!SamePairs(parsed, model))
            {
                Fail("fingerprint round-trip mismatch: <" + fp + "> for " + Show(b));
                break;
            }
            roundTrips++;

            // Deterministic, order-independent, and blind to the ledger key.
            if (fp != RequestFields.Fingerprint(b.Raw, "requestId"))
                Fail("fingerprint not deterministic on " + Show(b));
            if (fp != RequestFields.Fingerprint(Reordered(b, rng).Raw, "requestId"))
                Fail("fingerprint depends on field order for " + Show(b));
            FuzzBody keyed = Reordered(b, rng);
            keyed.Order.Add("requestId");
            keyed.Raw["requestId"] = "k" + iter;
            keyed.Text["requestId"] = "k" + iter;
            if (fp != RequestFields.Fingerprint(keyed.Raw, "requestId"))
                Fail("fingerprint depends on the excluded key for " + Show(b));

            // Injectivity against the model, in both directions.
            for (int p = 0; p < pool.Count; p++)
            {
                bool fpEqual = string.Equals(fp, poolFp[p], StringComparison.Ordinal);
                bool modelEqual = SamePairs(model, poolModel[p]);
                if (fpEqual && !modelEqual)
                    Fail("fingerprint collision: " + Show(b) + " and " + Show(pool[p])
                        + " both fingerprint to <" + fp + ">");
                if (!fpEqual && modelEqual)
                    Fail("fingerprint split: " + Show(b) + " and " + Show(pool[p])
                        + " are one request but read as <" + fp + "> and <" + poolFp[p] + ">");
                comparisons++;
            }
            if (_failures > 0) break;

            pool.Add(b);
            poolFp.Add(fp);
            poolModel.Add(model);
            if (pool.Count > PoolSize)
            {
                pool.RemoveAt(0);
                poolFp.RemoveAt(0);
                poolModel.RemoveAt(0);
            }
        }

        // The loop breaks on the first failure, so reaching here means nothing
        // tripped. What it does not prove on its own is that it compared
        // anything: a generator that stopped emitting fields, or a pool that
        // never filled, would report green. Assert the exact lower bounds.
        Check("fuzz: " + roundTrips + " fingerprints round-tripped through the independent decoder",
            roundTrips == 20000L);
        Check("fuzz: " + comparisons + " distinct-body fingerprint comparisons, no collision and no split",
            comparisons >= 20000L * (PoolSize - 1));

        // The invariant conversion the model leans on is pinned against the
        // table, so a change to the spelling of a value (a comma-decimal
        // host culture, a round-trip double format) fails here rather than
        // quietly widening the set of bodies the fuzz considers equal.
        int renderings = 0;
        foreach (FuzzValue v in FuzzValues)
        {
            if (v.Obj == null) continue;
            renderings++;
            string got = RequestFields.OptString(Body("f", v.Obj), "f");
            if (!string.Equals(got, v.Text, StringComparison.Ordinal))
                Fail("invariant rendering of <" + v.Text + "> is <" + got + ">");
        }
        Check("fuzz: " + renderings + " invariant value renderings match the table", renderings > 0);

        return 0;
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

        FingerprintFuzz();

        return Finish();
    }

    static int Finish()
    {
        Console.WriteLine(_failures == 0 ? "all request-fields tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
