// BotTextFuzzTests: randomized fuzzing of the canonical identity-text layer
// (Source/BotMod/Config/BotText.cs). Every admin-supplied name reaches the
// mod through here: the web API's setTeam and spawnNear "player"/"name" bodies,
// the console `bot team` / `bot player` arguments, the character table keys
// and the pasted scoreboard names spawnNear resolves. The layer calls
// string.Normalize on them, so the interesting inputs are the ones a name
// never carries on purpose: lone surrogates from a truncated UTF-16 payload,
// overlong and truncated UTF-8, C0/DEL/C1 controls, zero-width and bidi
// controls, variation selectors, combining marks (an NFD spelling of a name
// that arrives NFC) and long strings.
//
// The assertions encode the documented contract, so a violation surfaces as a
// failure rather than as a crash: normalization is total (no throw on any
// input), Canon and IdentityKey are idempotent and land in canonical form,
// WithoutInvisible leaves nothing scrubbable, BaseName is a fixed point that
// strips the spawn-name decoration, and NameMatches stays symmetric and
// agrees with canonical equality. Seed corpus: every string literal in the
// shipped config/characters.json and config/botmod.json, so real bot-name
// spellings drive the run instead of only synthetic bytes.
//
// Non-ASCII characters are written as \uXXXX escapes throughout: this is a
// file about invisible and surrogate code points, and a source that carries
// them literally is unreadable and unreviewable.
//
// Pure BCL: compiled headless with just BotText.cs by
// scripts/test-idempotency.sh (needs mcs + mono, not part of `make check`).
//
//   bash scripts/test-idempotency.sh
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BotMod.Config;

static class BotTextFuzzTests
{
    const int Seeds = 96;
    const int CasesPerSeed = 120;

    static int _failures;
    static int _cases;
    static int _seeds;

    static void Check(bool ok, string detail)
    {
        if (!ok)
        {
            _failures++;
            Console.WriteLine("FAIL " + detail);
        }
    }

    // ---- input generation ----

    // The character classes the policy claims to strip: C0, DEL, C1, NBSP,
    // soft hyphen, ZWSP, ZWJ, LRM, RLO, word joiner, invisible operator, BOM,
    // variation selectors.
    static readonly char[] Noise =
    {
        '\t', '\n', '\r', '\0', '\x1b', ' ', '\x7f', '\x85', '\x9f',
        '\u00A0', '\u00AD', '\u200C', '\u200D', '\u200E', '\u200F',
        '\u202A', '\u202E', '\u2060', '\u206F', '\uFEFF',
        '\uFE00', '\uFE0F',
    };

    // Combining marks, so a generated name can be NFD where a real one is NFC.
    static readonly char[] Combining = { '\u0301', '\u0308', '\u0327', '\u0303', '\u0331' };

    // Visible text that appears in bot and player names on a live server, plus
    // the non-ASCII letters whose case folding differs from ASCII.
    static readonly string[] Fragments =
    {
        "Grunt", "Kira", "K\u00EDra", "[Bot] ", "_42", "Zombie",
        "Female", "Mr. Cop", "Zo\u00EB", "\u65E5\u672C\u8A9E", "\u03A9", "\u01C5",
        "\u0130", "\u0131", "\u00DF", "SS", "\u00E1", "e", "-", "."
    };

    static uint _rng = 0xB07C0DE5u;

    static uint Next()
    {
        // xorshift32: self-contained, no engine types, deterministic per seed.
        uint x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        _rng = x;
        return x;
    }

    static int Pick(int n) { return (int)(Next() % (uint)n); }

    static string RandomName()
    {
        var sb = new StringBuilder();
        int parts = 1 + Pick(4);
        for (int i = 0; i < parts; i++)
        {
            switch (Pick(6))
            {
                case 0: sb.Append(Fragments[Pick(Fragments.Length)]); break;
                case 1: sb.Append(Noise[Pick(Noise.Length)]); break;
                case 2: sb.Append(Combining[Pick(Combining.Length)]); break;
                case 3: sb.Append((char)Pick(0x80)); break; // Latin-1 / C1 boundary
                case 4: sb.Append((char)(0xD800 + Pick(0x800))); break; // lone high surrogate
                case 5: sb.Append((char)(0xDC00 + Pick(0x400))); break; // lone low surrogate
            }
        }
        return sb.ToString();
    }

    /// <summary>Bytes decoded as UTF-8, so overlong forms, truncated
    /// sequences and embedded NULs reach the layer the way a hostile HTTP body
    /// would. Every eighth draw is null, the shape the callers pass through.</summary>
    static string RandomBytes()
    {
        var bytes = new byte[1 + Pick(48)];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)Pick(256);
        string s = new UTF8Encoding(false, false).GetString(bytes);
        return Pick(8) == 0 ? null : s;
    }

    /// <summary>The other normalization form of s, so the NFC/NFD-equality
    /// property is exercised on inputs differing only in composition. A lone
    /// surrogate has no normalized form; that is a result, not a failure.</summary>
    static string OtherForm(string s, bool composed)
    {
        if (s == null) return null;
        try { return s.Normalize(composed ? NormalizationForm.FormC : NormalizationForm.FormD); }
        catch (ArgumentException) { return s; }
    }

    static string Show(string s)
    {
        if (s == null) return "<null>";
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c < ' ' || c > '~')
                sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    static bool HasNoise(string s)
    {
        foreach (char c in s) if (BotText.IsInvisible(c)) return true;
        return false;
    }

    // ---- per-input contract ----

    static void CheckOne(string s, string ctx)
    {
        _cases++;

        string canon = BotText.Canon(s);
        string stripped = BotText.WithoutInvisible(s);
        string key = BotText.IdentityKey(s);
        string baseName = BotText.BaseName(s);

        Check(canon != null && stripped != null && key != null && baseName != null,
            ctx + ": a BotText reader returned null for " + Show(s));
        if (canon == null || stripped == null || key == null || baseName == null) return;

        // Canon: empty and null pass through, output is NFC, re-running is a
        // no-op, and no character is invented.
        Check(canon.Length > 0 || string.IsNullOrEmpty(s), ctx + ": Canon dropped every character of " + Show(s));
        Check(canon.IsNormalized(NormalizationForm.FormC), ctx + ": Canon output is not NFC: " + Show(canon));
        Check(BotText.Canon(canon) == canon, ctx + ": Canon is not idempotent: " + Show(s));
        // Length is not an invariant here: NFC decomposes the composition
        // exclusions (U+0344 and friends) into two marks, so the canonical
        // form of some inputs is longer than the input.

        // WithoutInvisible: total, leaves nothing scrubbable, stable.
        Check(!HasNoise(stripped), ctx + ": WithoutInvisible left a scrubbable char: " + Show(stripped));
        Check(BotText.WithoutInvisible(stripped) == stripped, ctx + ": WithoutInvisible is not idempotent: " + Show(s));
        Check(stripped.Length <= (s ?? "").Length, ctx + ": WithoutInvisible grew " + Show(s));

        // IdentityKey: the stored-key form, so it must be a fixed point of the
        // same policy (one spelling cannot fork into near misses) and equal
        // the documented strip-then-normalize composition.
        Check(BotText.IdentityKey(key) == key, ctx + ": IdentityKey is not a fixed point: " + Show(s) + " -> " + Show(key));
        Check(key == BotText.Canon(stripped), ctx + ": IdentityKey is not Canon(WithoutInvisible(x)): " + Show(s));
        Check(!HasNoise(key), ctx + ": IdentityKey left a scrubbable char: " + Show(s));
        Check(key.IsNormalized(NormalizationForm.FormC), ctx + ": the key is not NFC: " + Show(s));

        // BaseName: drops the spawn decoration, then everything from the first
        // underscore, so the result can never contain one and re-applying it
        // (a lookup on a stored key) cannot change the answer.
        Check(baseName.IndexOf('_') < 0, ctx + ": BaseName kept an underscore: " + Show(baseName));
        Check(BotText.BaseName(baseName) == baseName, ctx + ": BaseName is not idempotent: " + Show(s) + " -> " + Show(baseName));
        Check(BotText.BaseName("") == "", ctx + ": BaseName of the empty string is not empty");

        // NameMatches: an empty operand is never a hit, the canonical form of
        // an input always hits inside that input, and the relation is
        // symmetric (IndexOf in either direction).
        Check(!BotText.NameMatches(s, null) && !BotText.NameMatches(null, s)
            && !BotText.NameMatches("", "x") && !BotText.NameMatches("x", ""),
            ctx + ": an empty operand matched for " + Show(s));
        Check(canon.Length == 0 || BotText.NameMatches(s, canon), ctx + ": the canonical form is not a hit inside its own input: " + Show(s));
        Check(BotText.NameMatches(canon, canon) == (canon.Length > 0), ctx + ": NameMatches is not reflexive for " + Show(canon));
        Check(BotText.NameMatches(s, canon) == BotText.NameMatches(canon, s), ctx + ": NameMatches is asymmetric for " + Show(s));

        // The point of the layer: both spellings of one visible name collapse
        // to one identity. Checked on the composed/decomposed pair, so a
        // normalizer that only handles one form fails here.
        string other = OtherForm(s, false);
        if (other != null && other != s)
        {
            string nKey = BotText.IdentityKey(other);
            Check(nKey == key, ctx + ": the two spellings of " + Show(s) + " gave different keys ("
                + Show(key) + " vs " + Show(nKey) + ")");
            Check(BotText.NameMatches(s, other), ctx + ": the two spellings of " + Show(s) + " do not match");
            Check(BotText.NameMatches(other, s), ctx + ": the two spellings of " + Show(s) + " do not match reversed");
            Check(BotText.BaseName(other) == baseName, ctx + ": BaseName disagrees between the two spellings of " + Show(s));
        }

        // Spawn-name decoration: the exact shape the spawner produces
        // ("[Bot] Grunt_42") must resolve to the bare base name, whatever case
        // the tag was written in and whatever trailing number follows.
        if (key.Length > 0 && key.IndexOf('_') < 0)
        {
            Check(BotText.BaseName("[Bot] " + key + "_42") == key,
                ctx + ": BaseName did not strip the spawn name: " + Show(key));
            Check(BotText.BaseName("[bot] " + key + "_7") == key,
                ctx + ": BaseName did not strip a lower-case spawn tag: " + Show(key));
        }
    }

    // ---- driver ----

    // Corner cases first: they are the inputs least likely to be generated by
    // chance and the ones most likely to break a normalizer. \uD7FF and
    // \uE000 flank the surrogate range, \uFFFD is what a bad UTF-8 decode
    // leaves behind, and the last two are bare combining marks (an NFD string
    // with nothing to compose onto).
    static readonly string[] Corners =
    {
        null, "", " ", "\0", "\uD7FF", "\uE000", "\u00A0", "\uFFFD", "\uFFFD",
        "K\u00EDra", "\u1E0A", "\u1F600", "\u01C5ungla", "\u0130", "i\u0307",
        "\uFB04", "\u00DF", "SS", "\u1F310", "\u202E\uFEFF", "\u0000",
        "[Bot] ", "[Bot] _", "[Bot] Grunt_42", "_", "__", "____42", "\u0301"
    };

    static int Main(string[] args)
    {
        var corpus = LoadCorpus(args.Length > 0 ? args[0] : null);

        foreach (string s in Corners) CheckOne(s, "corner");
        foreach (string s in corpus) CheckOne(s, "corpus");

        for (int seed = 0; seed < Seeds; seed++)
        {
            _seeds++;
            _rng = 0xB07C0DE5u + (uint)seed * 2654435761u;
            for (int i = 0; i < CasesPerSeed; i++)
            {
                _rng ^= (uint)i * 2246822519u;
                CheckOne(i % 2 == 0 ? RandomName() : RandomBytes(), "seed=" + seed + " case=" + i);
            }
        }

        Console.WriteLine("bot text fuzz: " + _cases + " inputs over " + _seeds + " seeds ("
            + corpus.Count + " corpus strings), " + _failures + " failures");
        if (_failures > 0) { Console.WriteLine("bot text fuzz tests FAILED"); return 1; }
        Console.WriteLine("all bot text fuzz tests passed");
        return 0;
    }

    // ---- seed corpus from the shipped configs ----

    /// <summary>Every string literal in the shipped character table and mod
    /// config, so the run starts from real name spellings (and whatever an
    /// operator has typed into them) instead of only synthetic noise.</summary>
    static List<string> LoadCorpus(string root)
    {
        var corpus = new List<string>();
        if (string.IsNullOrEmpty(root)) return corpus;
        foreach (string rel in new[] { "config/characters.json", "config/botmod.json" })
        {
            string path;
            try { path = Path.Combine(root, rel); }
            catch (ArgumentException) { continue; } // invalid characters in root
            if (!File.Exists(path)) continue;
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch (IOException) { continue; }
            foreach (Match m in Regex.Matches(text, "\"(?:[^\"\\\\\\x00-\\x1f]|\\\\.)*\""))
            {
                string literal = Unescape(m.Value);
                if (literal.Length > 0) corpus.Add(literal);
            }
        }
        return corpus;
    }

    /// <summary>JSON string literal (quotes included) to its value, covering
    /// the escapes a config file can carry.</summary>
    static string Unescape(string literal)
    {
        var sb = new StringBuilder();
        for (int i = 1; i + 1 < literal.Length; i++)
        {
            char c = literal[i];
            if (c != '\\') { sb.Append(c); continue; }
            if (++i + 1 > literal.Length) break;
            switch (literal[i])
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (i + 4 < literal.Length)
                    {
                        int code;
                        if (int.TryParse(literal.Substring(i + 1, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                    }
                    break;
                default: sb.Append(literal[i]); break;
            }
        }
        return sb.ToString();
    }
}
