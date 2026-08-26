// BotTextTests: pins the Unicode identity contract for names: NFC
// canonicalization on both sides of every comparison and at ingestion into
// stored keys, so an NFD spelling ("K" + combining acute) matches the NFC
// form the server holds, and case folding is ordinal (no host-locale traps).
// Pure BCL; compiled and run by scripts/test-idempotency.sh.
//
//   bash scripts/test-idempotency.sh
using System;
using BotMod.Config;

static class BotTextTests
{
    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
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
        Check("control characters stripped from key", BotText.IdentityKey("Gru\r\n\tnt") == "Grunt");
        Check("C1 control stripped from key", BotText.IdentityKey("G\u009frunt") == "Grunt");
        Check("ZWSP-pasted assignment hits clean lookup",
            BotText.BaseName("Grunt\u200b") == "Grunt" && BotText.IdentityKey("Grunt\u200b") == BotText.IdentityKey("Grunt"));
        Check("visible non-ASCII preserved in key", BotText.IdentityKey("K\u00edra\u2603") == "K\u00edra\u2603");
        Check("WithoutInvisible leaves clean text alone", BotText.WithoutInvisible(nfcKira) == nfcKira);
        Check("WithoutInvisible null is empty", BotText.WithoutInvisible(null) == "");

        Console.WriteLine(_failures == 0 ? "all bot text tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
