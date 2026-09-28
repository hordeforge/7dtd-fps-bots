// BotTextTests: pins the Unicode identity contract for names: NFC
// canonicalization on both sides of every comparison and at ingestion into
// stored keys, so an NFD spelling ("K" + combining acute) matches the NFC
// form the server holds, and case folding is ordinal (no host-locale traps).
// Pure BCL; compiled and run by scripts/test-idempotency.sh.
//
//   bash scripts/test-idempotency.sh
using System;
using BotMod.Foundation;

static class BotTextTests
{
    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    static string Repeat(string s, int n)
    {
        var sb = new System.Text.StringBuilder(s.Length * n);
        for (int i = 0; i < n; i++) sb.Append(s);
        return sb.ToString();
    }

    static int Main()
    {
        // NFD input canonicalizes to NFC: base letter + combining acute ->
        // precomposed á (single code point, the form game/Steam names use).
        string nfdKira = "Ki\u0301ra";
        string nfcKira = "K\u00edra";
        Check("NFD input differs from NFC by bytes", nfdKira != nfcKira);
        Check("Canon maps NFD to NFC", BotText.Canon(nfdKira) == nfcKira);
        Check("Canon is idempotent", BotText.Canon(BotText.Canon(nfdKira)) == nfcKira);
        Check("Canon leaves ASCII alone", BotText.Canon("Grunt_42") == "Grunt_42");
        Check("null Canon is empty", BotText.Canon(null) == "");
        Check("empty Canon is empty", BotText.Canon("") == "");

        // Name matching bridges the forms in both directions.
        Check("NFC name found via NFD ident", BotText.NameMatches(nfcKira, nfdKira));
        Check("NFD name found via NFC ident", BotText.NameMatches(nfdKira, nfcKira));
        Check("exact ascii match", BotText.NameMatches("Kira", "Kira"));
        Check("case-insensitive match", BotText.NameMatches("KirA", "kira"));
        Check("substring match (bot player Kir)", BotText.NameMatches("Kira", "Kir"));
        Check("distinct names do not match", !BotText.NameMatches("Kira", "Zara"));
        Check("empty ident never matches", !BotText.NameMatches("Kira", ""));
        Check("empty name never matches", !BotText.NameMatches("", "Kira"));

        // Case folding is ordinal/invariant, not host-culture: a tr-TR server
        // must not fold dotted/dotless I differently than any other host.
        Check("ascii I/i fold ordinally", BotText.NameMatches("ISTANBUL", "istanbul"));
        Check("dotless i is a distinct letter", !BotText.NameMatches("I\u0131DIR", "Igdir"));
        Check("dotted capital I keeps its dot", !BotText.NameMatches("\u0130stanbul", "Istanbul"));

        // BaseName: strip tag + _NN suffix, NFC-canonical output.
        Check("base name strips tag and suffix", BotText.BaseName("[Bot] Grunt_42") == "Grunt");
        Check("tag match is case-insensitive", BotText.BaseName("[bot] Grunt_42") == "Grunt");
        Check("non-ASCII base name canonicalizes", BotText.BaseName("[Bot] " + nfdKira + "_7") == nfcKira);
        Check("name without tag passes through", BotText.BaseName("Dozer_11") == "Dozer");
        Check("null base name is empty", BotText.BaseName(null) == "");

        // Invisible characters must not fork identity keys: a name pasted from
        // a web page can carry zero-width spaces, bidi controls or variation
        // selectors, and a key holding them silently never matches the clean
        // spelling every live lookup derives.
        Check("zero-width space stripped from key", BotText.BaseName("[Bot] Grunt\u200b_42") == "Grunt");
        Check("bidi override stripped from key", BotText.BaseName("[Bot] Do\u202ezer") == "Dozer");
        Check("variation selector stripped from key", BotText.BaseName("[Bot] Visor\ufe0f") == "Visor");
        Check("BOM stripped from key", BotText.BaseName("\ufeffGrunt") == "Grunt");
        // The whole Unicode Bidi_Control set, not just the embeddings and
        // overrides: U+061C (ARABIC LETTER MARK) and the isolates
        // U+2066..U+2069 reorder or hide text in a terminal like U+202E does,
        // so "Gr\u2066unt" must not be a second key next to "Grunt".
        Check("bidi isolate U+2066 stripped from key", BotText.BaseName("[Bot] Gr\u2066unt_3") == "Grunt");
        Check("bidi isolate U+2069 stripped from key", BotText.IdentityKey("Grunt\u2069") == "Grunt");
        Check("ARABIC LETTER MARK stripped from key", BotText.IdentityKey("Gr\u061cunt") == "Grunt");
        Check("every Bidi_Control character is scrubbed",
            BotText.WithoutInvisible("\u061c\u200e\u200f\u202a\u202b\u202c\u202d\u202e\u2066\u2067\u2068\u2069") == "");
        Check("control characters stripped from key", BotText.IdentityKey("Gru\r\n\tnt") == "Grunt");
        Check("C1 control stripped from key", BotText.IdentityKey("G\u009frunt") == "Grunt");
        Check("line/paragraph separators stripped from key",
            BotText.IdentityKey("Gru\u2028nt\u2029") == "Grunt");
        Check("ZWSP-pasted assignment hits clean lookup",
            BotText.BaseName("Grunt\u200b") == "Grunt" && BotText.IdentityKey("Grunt\u200b") == BotText.IdentityKey("Grunt"));
        Check("visible non-ASCII preserved in key", BotText.IdentityKey("K\u00edra\u2603") == "K\u00edra\u2603");
        Check("WithoutInvisible leaves clean text alone", BotText.WithoutInvisible(nfcKira) == nfcKira);
        Check("WithoutInvisible null is empty", BotText.WithoutInvisible(null) == "");

        // Strip before canon, not after: an invisible character sitting
        // between a base letter and its combining mark blocks their
        // composition, so canon-then-strip leaves two uncombined marks in the
        // stored key and the lookup key never matches it.
        {
            // "e" + ZWNJ (U+200C, scrubbed) + combining acute.
            string split = "e\u200c\u0301";
            Check("invisible char between base and mark still composes",
                BotText.IdentityKey(split) == "\u00e9");
            // Two marks with the invisible character wedged between them.
            // The contract is that wedging a scrubbed character in changes
            // nothing, so compare against the same name without it.
            Check("invisible char between base and mark still composes",
                BotText.IdentityKey(split) == BotText.IdentityKey("e\u0301"));
            Check("two invisible chars between base and mark still compose",
                BotText.IdentityKey("e\u200c\u200d\u0301") == BotText.IdentityKey("e\u0301"));
            Check("invisible char between two letters does not fork the key",
                BotText.IdentityKey("Gr\u200cunt") == "Grunt");
            Check("stripping first is a fixed point",
                BotText.IdentityKey(BotText.IdentityKey(split)) == BotText.IdentityKey(split));
        }

        // CharCount is the unit for every character limit: a surrogate pair is
        // one character, not the two string.Length reports, so a 128-char limit
        // is not silently a 64-emoji limit.
        {
            string emoji = "\ud83d\ude00"; // U+1F600, one scalar, two code units
            Check("CharCount counts a surrogate pair once", BotText.CharCount(emoji) == 1);
            Check("CharCount differs from string.Length above the BMP", emoji.Length == 2);
            Check("CharCount of a 100-emoji key is 100", BotText.CharCount(Repeat(emoji, 100)) == 100);
            Check("CharCount counts unpaired surrogates as one each",
                BotText.CharCount("a\ud83db") == 3);
            Check("CharCount is zero for null and empty",
                BotText.CharCount(null) == 0 && BotText.CharCount("") == 0);
            Check("CharCount agrees with Length in the BMP",
                BotText.CharCount("Grunt_42\u00e9") == "Grunt_42\u00e9".Length);
        }

        // NameMatches must not strip invisibles: player names legitimately
        // carry U+200D inside emoji sequences, so the stored-key rule above
        // cannot be applied to a match.
        {
            string zwj = "a\ud83d\ude00\ud83d\udc69\u200d\ud83d\ude80b";
            Check("NameMatches keeps an emoji ZWJ sequence intact", BotText.NameMatches(zwj, zwj));
            Check("NameMatches differs from IdentityKey on ZWJ names",
                BotText.IdentityKey(zwj) != zwj);
        }

        Console.WriteLine(_failures == 0 ? "all bot text tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
