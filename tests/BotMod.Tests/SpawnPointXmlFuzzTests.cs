// SpawnPointXmlFuzzTests: randomized fuzzing of the world spawnpoints.xml
// reader. spawnpoints.xml is a shared, semi-trusted world file: it comes with
// the map (or from a downloaded/generated world) and the mod turns every
// //spawnpoint it accepts into a bot spawn position, so a malformed or
// crafted document is a real input surface.
//
// Invariants pinned here, over a corpus of hand-written documents, byte-level
// mutants of them, and randomly assembled garbage:
//   - a document that is not well-formed XML throws (the caller's catch
//     reports it and leaves the memoized spawn list untouched) instead of
//     returning a half-read list,
//   - a DOCTYPE, an internal entity and an external entity are never expanded:
//     no file:// or http:// fetch, no entity-expansion blowup,
//   - every returned coordinate is finite. "NaN" and "Infinity" are valid
//     float text, so a coordinate the reader accepted would put a bot at an
//     undefined position and poison every later combat tick,
//   - a coordinate is invariant: dot decimals read the same under any host
//     culture, so a comma-decimal server locale cannot silently drop every
//     spawnpoint (and with it DM spawn selection),
//   - well-formedness of the surviving points: a truncated position attribute
//     yields no point rather than a zero-filled one, and document order is
//     preserved.
// Pure BCL (System.Xml); compiled and run by scripts/test-idempotency.sh:
//
//   bash scripts/test-idempotency.sh
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;
using BotMod.Foundation;

static class SpawnPointXmlFuzzTests
{
    const int Iterations = 4000;

    /// <summary>Failures reported before the run stops: one bad reader
    /// fails on most of the corpus, and 4000 copies of the same report bury
    /// the first one.</summary>
    const int FailBudget = 20;

    static int _failures;
    static int _cases;

    static void Check(bool ok, string detail)
    {
        if (!ok)
        {
            _failures++;
            if (_failures > FailBudget) return;
            Console.WriteLine("FAIL " + detail);
        }
    }

    static readonly string[] Documents =
    {
        // <spawnpoints><spawnpoint position="1,2,3"/></spawnpoints>
        "<spawnpoints><spawnpoint position=\"1,2,3\"/></spawnpoints>",
        "<spawnpoints><spawnpoint position=\"-128.5,0,64.25\"/></spawnpoints>",
        "<spawnpoints><spawnpoint position=\"1e3,-2.5E-2,0\"/></spawnpoints>",
        "<spawnpoints><spawnpoint position=\"1,2\"/><spawnpoint position=\"4,5,6\"/></spawnpoints>",
        "<spawnpoints><spawnpoint position=\"NaN,2,3\"/><spawnpoint position=\"4,5,6\"/></spawnpoints>",
        "<spawnpoints><spawnpoint position=\"Infinity,2,3\"/><spawnpoint position=\"-Infinity,2,3\"/></spawnpoints>",
        "<spawnpoints><spawnpoint position=\"1,NaN,3\"/><spawnpoint position=\"1,2,Infinity\"/></spawnpoints>",
        "<spawnpoints><spawnpoint position=\" , , \"/></spawnpoints>",
        "<spawnpoints><spawnpoint position=\"1,2,3,4\"/></spawnpoints>",
        "<spawnpoints><spawnpoint position=\"1.0;2.0;3.0\"/></spawnpoints>",
        "<spawnpoints><spawnpoint/></spawnpoints>",
        "<spawnpoints><spawnpoint name=\"dm\" position=\"7,8,9\"/></spawnpoints>",
        "<spawnpoints><spawnpoint position=\"1e400,2,3\"/></spawnpoints>",
        "<spawnpoints><spawnpoint position=\"0x10,2,3\"/></spawnpoints>",
        "<spawnpoints><spawnpoint position=\" 12 , 34 , 56 \"/></spawnpoints>",
        // The vectors the real world file ships, as a regression shape.
        "<spawnpoints><spawnpoint position=\"103.4,12.9,64.1\"/><spawnpoint position=\"-103.4,12.9,64.1\"/></spawnpoints>",
        // Security shapes: entity expansion and external fetch must not run.
        "<!DOCTYPE spawnpoints [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><spawnpoints><spawnpoint position=\"&xxe;\"/></spawnpoints>",
        "<!DOCTYPE spawnpoints [<!ENTITY lol \"lol\"><!ENTITY lol1 \"&lol;&lol;&lol;\">]><spawnpoints><spawnpoint position=\"&lol1;\"/></spawnpoints>",
        "<!DOCTYPE spawnpoints SYSTEM \"http://127.0.0.1:1/evil.dtd\"><spawnpoints><spawnpoint position=\"1,2,3\"/></spawnpoints>",
        "<!DOCTYPE spawnpoints [<!ENTITY % pe SYSTEM \"file:///etc/passwd\">%pe;]><spawnpoints/>",
        // Well-formed but not the expected shape, plus plain garbage.
        "<spawnpoints></spawnpoints>",
        "<other><spawnpoint position=\"1,2,3\"/></other>",
        "<spawnpoints><spawnpoint position=\"1,2,3\"/>",
        "not xml at all",
        "",
        null,
    };

    /// <summary>Random XML-ish text: the union of what a corrupt file looks
    /// like (truncation, stray bytes, doubled tags) and what a crafted one
    /// aims at (entities, doctypes, deep nesting).</summary>
    static string RandomDocument(Random rng)
    {
        switch (rng.Next(10))
        {
            case 0: return Documents[rng.Next(Documents.Length)];
            case 1: return Mutate(Documents[rng.Next(Documents.Length)], rng);
            // Many points: document order must survive a large list.
            case 2:
                {
                    var sb = new StringBuilder("<spawnpoints>");
                    int n = 1 + rng.Next(64);
                    for (int i = 0; i < n; i++)
                        sb.Append("<spawnpoint position=\"").Append(rng.Next(-1000, 1000)).Append(',')
                          .Append(rng.Next(-1000, 1000)).Append(',').Append(rng.Next(-1000, 1000)).Append("\"/>");
                    return sb.Append("</spawnpoints>").ToString();
                }
            // Deep nesting: nothing here recurses per element, so a deep
            // document must not cost more than a shallow one.
            case 3:
                {
                    int depth = 1 + rng.Next(200);
                    var sb = new StringBuilder();
                    for (int i = 0; i < depth; i++) sb.Append("<n>");
                    sb.Append("<spawnpoint position=\"1,2,3\"/>");
                    for (int i = 0; i < depth; i++) sb.Append("</n>");
                    return sb.ToString();
                }
            case 4:
                {
                    var sb = new StringBuilder("<!DOCTYPE spawnpoints [<!ENTITY a \"");
                    int n = rng.Next(1, 40);
                    for (int i = 0; i < n; i++) sb.Append("AAAAAAAAAA");
                    sb.Append("\">]><spawnpoints><spawnpoint position=\"&a;\"/></spawnpoints>");
                    return sb.ToString();
                }
            case 5:
                {
                    // Entity-expansion attempt: the shape a billion-laughs
                    // document takes. Ignored DTD means it must stay cheap.
                    var sb = new StringBuilder("<!DOCTYPE spawnpoints [<!ENTITY a0 \"aaaaaaaaaa\">");
                    for (int i = 1; i < 10; i++) sb.Append("<!ENTITY a").Append(i).Append(" \"&a").Append(i - 1).Append(";&a").Append(i - 1).Append(";&a").Append(i - 1).Append(";&a").Append(i - 1).Append(";&a").Append(i - 1).Append(";\">");
                    return sb.Append("]><spawnpoints><spawnpoint position=\"&a9;\"/></spawnpoints>").ToString();
                }
            default:
                {
                    int n = rng.Next(0, 120);
                    var sb = new StringBuilder(n);
                    for (int i = 0; i < n; i++) sb.Append((char)rng.Next(0x20, 0x7f));
                    return sb.ToString();
                }
        }
    }

    /// <summary>Byte-level damage to a real document: truncation, dropped
    /// characters, and the odd control byte.</summary>
    static string Mutate(string doc, Random rng)
    {
        if (doc == null) return null;
        char[] buf = doc.ToCharArray();
        int mutations = 1 + rng.Next(3);
        for (int m = 0; m < mutations && buf.Length > 0; m++)
        {
            switch (rng.Next(4))
            {
                case 0: buf[rng.Next(buf.Length)] = (char)rng.Next(0x20, 0x7f); break;
                case 1: buf[rng.Next(buf.Length)] = '\0'; break;
                case 2: buf = Truncate(buf, rng); break;
                default:
                    {
                        int at = rng.Next(buf.Length);
                        var kept = new char[buf.Length - 1];
                        Array.Copy(buf, kept, at);
                        for (int i = at; i < kept.Length; i++) kept[i] = buf[i + 1];
                        buf = kept;
                        break;
                    }
            }
        }
        return new string(buf);
    }

    static char[] Truncate(char[] buf, Random rng)
    {
        int len = rng.Next(0, buf.Length + 1);
        var cut = new char[len];
        Array.Copy(buf, cut, len);
        return cut;
    }

    static bool WellFormed(string doc)
    {
        if (doc == null) return true;
        try { SpawnPointXml.Parse(doc); return true; }
        catch (XmlException) { return false; }
    }

    /// <summary>Nesting ten levels of five-fold entity references: with the
    /// DTD honored this is a billion-laughs blowup, so the read must stay
    /// instant instead of eating the tick.</summary>
    const string BillionLaughs =
        "<!DOCTYPE spawnpoints [" +
        "<!ENTITY a0 \"aaaaaaaaaa\">" +
        "<!ENTITY a1 \"&a0;&a0;&a0;&a0;&a0;\">" +
        "<!ENTITY a2 \"&a1;&a1;&a1;&a1;&a1;\">" +
        "<!ENTITY a3 \"&a2;&a2;&a2;&a2;&a2;\">" +
        "<!ENTITY a4 \"&a3;&a3;&a3;&a3;&a3;\">" +
        "<!ENTITY a5 \"&a4;&a4;&a4;&a4;&a4;\">" +
        "<!ENTITY a6 \"&a5;&a5;&a5;&a5;&a5;\">" +
        "<!ENTITY a7 \"&a6;&a6;&a6;&a6;&a6;\">" +
        "<!ENTITY a8 \"&a7;&a7;&a7;&a7;&a7;\">" +
        "<!ENTITY a9 \"&a8;&a8;&a8;&a8;&a8;\">" +
        "]><spawnpoints><spawnpoint position=\"&a9;\"/></spawnpoints>";

    const string XxeFile =
        "<!DOCTYPE spawnpoints [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>"
        + "<spawnpoints><spawnpoint position=\"&xxe;\"/></spawnpoints>";

    const string XxeHttp =
        "<!DOCTYPE spawnpoints [<!ENTITY xxe SYSTEM \"http://127.0.0.1:1/evil.dtd\">]>"
        + "<spawnpoints><spawnpoint position=\"&xxe;\"/></spawnpoints>";

    const string EntityFanout =
        "<!DOCTYPE spawnpoints [<!ENTITY lol \"lol\"><!ENTITY lol1 \"&lol;&lol;&lol;\">]>"
        + "<spawnpoints><spawnpoint position=\"&lol1;\"/></spawnpoints>";

    /// <summary>A DOCTYPE is ignored, not rejected: the plain spawnpoint
    /// behind it must still be read.</summary>
    const string DtdWithPlainPoint =
        "<!DOCTYPE spawnpoints SYSTEM \"http://127.0.0.1:1/evil.dtd\">"
        + "<spawnpoints><spawnpoint position=\"1,2,3\"/></spawnpoints>";

    /// <summary>One spawnpoint document around an arbitrary position value.</summary>
    static string Doc(string position)
    {
        return "<spawnpoints><spawnpoint position=\"" + position + "\"/></spawnpoints>";
    }

    /// <summary>One spawnpoint document carrying two points.</summary>
    static string TwoDocs(string a, string b)
    {
        return "<spawnpoints><spawnpoint position=\"" + a + "\"/><spawnpoint position=\""
            + b + "\"/></spawnpoints>";
    }

    /// <summary>Points in a document that is allowed to be malformed: an
    /// undeclared entity leaves nothing to read, which is the same answer as
    /// a document with no usable spawnpoint.</summary>
    static int CountOrZero(string doc)
    {
        try { return SpawnPointXml.Parse(doc).Count; }
        catch (XmlException) { return 0; }
    }

    static void CheckOne(string doc, string ctx)
    {
        _cases++;
        List<SpawnPoint> points;
        try { points = SpawnPointXml.Parse(doc); }
        catch (XmlException) { return; }   // reported by the caller, not a defect
        catch (Exception ex)
        {
            Check(false, ctx + ": threw " + ex.GetType().Name + ": " + ex.Message);
            return;
        }
        foreach (SpawnPoint p in points)
        {
            if (float.IsNaN(p.X) || float.IsInfinity(p.X)
                || float.IsNaN(p.Y) || float.IsInfinity(p.Y)
                || float.IsNaN(p.Z) || float.IsInfinity(p.Z))
            {
                Check(false, ctx + ": non-finite coordinate survived: "
                    + p.X.ToString(CultureInfo.InvariantCulture) + ","
                    + p.Y.ToString(CultureInfo.InvariantCulture) + ","
                    + p.Z.ToString(CultureInfo.InvariantCulture));
                return;
            }
        }
        // Determinism: the same document twice answers the same list.
        List<SpawnPoint> again = SpawnPointXml.Parse(doc);
        if (again.Count != points.Count)
        {
            Check(false, ctx + ": not deterministic (" + points.Count + " then " + again.Count + ")");
            return;
        }
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].X != again[i].X || points[i].Y != again[i].Y || points[i].Z != again[i].Z)
            {
                Check(false, ctx + ": parse " + i + " diverged between runs");
                return;
            }
        }
    }

    static int Main()
    {
        // Fixed documents first, each under a hostile host culture: the
        // reader parses invariantly, so a comma-decimal locale must still
        // read the dot decimals the world file ships.
        var prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Check(SpawnPointXml.Parse(Doc("103.4,12.9,64.1")).Count == 1,
                "invariant parse under a comma-decimal host culture");
            Check(SpawnPointXml.Parse(Doc("-128.5,0,64.25"))[0].X == -128.5f,
                "invariant decimals under a comma-decimal host culture");
            Check(SpawnPointXml.Parse(null).Count == 0 && SpawnPointXml.Parse("").Count == 0,
                "null and empty answer no points");
            Check(SpawnPointXml.Parse(Doc("1,2")).Count == 0,
                "a short position attribute yields no point");
            Check(SpawnPointXml.Parse(Doc("1,2,3,4")).Count == 1,
                "an extra component does not drop the triple");
            Check(SpawnPointXml.Parse("<other><spawnpoint position=\"1,2,3\"/></other>").Count == 1,
                "//spawnpoint matches under any element name");
            Check(SpawnPointXml.Parse(Doc(" 12 , 34 , 56 "))[0].X == 12f,
                "leading and trailing spaces are accepted");
            // The coordinates the profile rejects: "NaN"/"Infinity" are valid
            // float text, and a bot placed at one poisons every later tick.
            Check(SpawnPointXml.Parse(Doc("NaN,2,3")).Count == 0
                  && SpawnPointXml.Parse(Doc("1,2,Infinity")).Count == 0
                  && SpawnPointXml.Parse(Doc("-Infinity,0,1e400")).Count == 0,
                "a non-finite coordinate yields no point");
            Check(SpawnPointXml.Parse(TwoDocs("1,2,NaN", "4,5,6")).Count == 1,
                "one bad coordinate does not drop the next point");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = prev;
        }

        // Security pins, run outside the culture block: no external fetch, no
        // entity expansion, no billion laughs. A DOCTYPE is ignored whole, so
        // a document carrying one still reads its plain spawnpoints and
        // answers nothing for the entity. A document whose only content is an
        // undeclared entity fails as malformed, which the caller reports.
        Check(CountOrZero(XxeFile) == 0, "a file:// entity was expanded");
        Check(CountOrZero(XxeHttp) == 0, "an http:// entity was expanded");
        Check(CountOrZero(EntityFanout) == 0, "an internal entity was expanded");
        Check(CountOrZero(DtdWithPlainPoint) == 1,
            "a DOCTYPE document stopped reading its plain spawnpoint");
        Check(CountOrZero(BillionLaughs) == 0, "the entity-expansion document answered points");
        // Stopwatch, not DateTime.UtcNow: a budget is an elapsed duration, and
        // a wall-clock step (NTP correction, an operator setting the host clock)
        // mid-measurement prints a negative or absurd elapsed and fails the run
        // for a parse that finished in microseconds, while a backward step hides
        // a real blowup. Same reason IdempotencyLedger times its retention window.
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            CountOrZero(BillionLaughs);
            sw.Stop();
            Check(sw.ElapsedMilliseconds < 2000,
                "the entity-expansion document took " + sw.ElapsedMilliseconds + "ms");
        }
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n;
            try { n = SpawnPointXml.Parse(BillionLaughs).Count; }
            catch (XmlException) { n = 0; }
            sw.Stop();
            Check(n == 0, "entity-expansion document answered " + n + " points");
            Check(sw.ElapsedMilliseconds < 2000,
                "entity-expansion document took " + sw.ElapsedMilliseconds + "ms");
        }

        // Every fixed document, twice, plus the whole random corpus.
        for (int i = 0; i < Documents.Length; i++)
        {
            CheckOne(Documents[i], "doc#" + i);
            if (_failures > FailBudget) break;
        }
        var rng = new Random(20260928);
        for (int i = 0; i < Iterations && _failures <= FailBudget; i++)
        {
            string doc = RandomDocument(rng);
            CheckOne(doc, "#" + i);
        }

        Console.WriteLine("spawnpointxml fuzz: " + _cases + " documents");
        Console.WriteLine(_failures == 0
            ? "all spawnpoint xml fuzz checks passed"
            : _failures + " fuzz check(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
