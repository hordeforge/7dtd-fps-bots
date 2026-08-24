// BotArgParserTests — pins the positional grammar of `bot spawn` /
// `bot player`: optional count, optional dot-decimal x z pair, trailing
// weapon token, and a named usage error for every leftover token. Guards
// the regression where "bot spawn 163 818" ate 163 as a bot count and
// silently dropped the 818, spawning at a random position instead.
// Pure BCL; compiled and run by scripts/test-idempotency.sh.
//
//   bash scripts/test-idempotency.sh
using System;
using System.Collections.Generic;
using BotMod.Commands;

static class BotArgParserTests
{
    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    static bool Spawn(string[] tail, out int count, out float x, out float z,
        out bool hasPos, out string weapon, out string error)
    {
        var args = new List<string>(tail);
        args.Insert(0, "spawn");
        return BotArgParser.TryParseSpawn(args, 1, out count, out x, out z, out hasPos, out weapon, out error);
    }

    static bool Player(string[] tail, out int count, out string weapon, out string error)
    {
        var args = new List<string>(tail);
        args.Insert(0, "Kira");
        return BotArgParser.TryParsePlayer(args, 1, out count, out weapon, out error);
    }

    static int Main()
    {
        // Defaults: bare subcommand spawns one bot, nothing else set.
        Check("no args -> count 1", Spawn(new string[0], out int c1, out _, out _, out bool p1, out string w1, out _) && c1 == 1 && !p1 && w1 == null);

        // Count forms, including the historical clamp window 1..16.
        Spawn(new[] { "4" }, out int c2, out _, out _, out _, out _, out _);
        Check("count 4", c2 == 4);
        Spawn(new[] { "0" }, out int c3, out _, out _, out _, out _, out _);
        Check("count 0 clamps to 1", c3 == 1);
        Spawn(new[] { "-3" }, out int c4, out _, out _, out _, out _, out _);
        Check("negative count clamps to 1", c4 == 1);
        Spawn(new[] { "99" }, out int c5, out _, out _, out _, out _, out _);
        Check("count 99 clamps to 16", c5 == 16);
        // Exact clamp edges: 16 is the last accepted count, 17 is clamped.
        Spawn(new[] { "16" }, out int c5a, out _, out _, out _, out _, out _);
        Check("count 16 kept at max", c5a == 16);
        Spawn(new[] { "17" }, out int c5b, out _, out _, out _, out _, out _);
        Check("count 17 clamps to 16", c5b == 16);

        // The documented coordinate form: two numbers are x z with count 1,
        // never "count plus dangling junk".
        bool ok6 = Spawn(new[] { "1200", "-1300" }, out int c6, out float x6, out float z6, out bool p6, out _, out _);
        Check("bare coords -> position, count 1", ok6 && p6 && c6 == 1 && x6 == 1200f && z6 == -1300f);

        // Full forms.
        bool ok7 = Spawn(new[] { "2", "10", "20" }, out int c7, out float x7, out float z7, out bool p7, out _, out _);
        Check("count + coords", ok7 && c7 == 2 && p7 && x7 == 10f && z7 == 20f);
        bool ok8 = Spawn(new[] { "2", "10.25", "-20.75" }, out _, out float x8, out float z8, out bool p8, out _, out _);
        Check("invariant dot-decimal coords", ok8 && p8 && x8 == 10.25f && z8 == -20.75f);
        // The complete advertised usage line: [count] [x z] [weapon] together,
        // and the shorter [x z] [weapon] tail.
        bool okFull = Spawn(new[] { "2", "10", "20", "gunMGT1AK47" }, out int cFull, out float xFull, out float zFull, out bool pFull, out string wFull, out _);
        Check("count + coords + weapon", okFull && cFull == 2 && pFull && xFull == 10f && zFull == 20f && wFull == "gunMGT1AK47");
        bool okCW = Spawn(new[] { "-5.5", "300", "mixed" }, out int cCW, out float xCW, out float zCW, out bool pCW, out string wCW, out _);
        Check("coords + weapon", okCW && pCW && cCW == 1 && xCW == -5.5f && zCW == 300f && wCW == "mixed");
        // mono's float.TryParse accepts "NaN"/"Infinity" spellings that
        // desktop .NET rejects; a non-finite coordinate would place a bot at
        // an unusable position, so the grammar must reject them (found by
        // BotArgParserFuzzTests).
        Check("NaN coord rejected", !Spawn(new[] { "NaN", "3" }, out _, out _, out _, out _, out _, out string e8a) && e8a.Contains("'NaN'"));
        Check("Infinity coord rejected", !Spawn(new[] { "2", "-Infinity" }, out _, out _, out _, out _, out _, out string e8b) && e8b.Contains("'-Infinity'"));
        Spawn(new[] { "gunMGT1AK47" }, out int c9, out _, out _, out _, out string w9, out _);
        Check("weapon only", c9 == 1 && w9 == "gunMGT1AK47");
        Spawn(new[] { "mixed" }, out _, out _, out _, out _, out string w10, out _);
        Check("mixed weapon", w10 == "mixed");
        Spawn(new[] { "3", "mixed" }, out int c11, out _, out _, out _, out string w11, out _);
        Check("count + weapon", c11 == 3 && w11 == "mixed");
        Spawn(new[] { "GUNx" }, out _, out _, out _, out _, out string w12, out _);
        Check("weapon match is case-insensitive", w12 == "GUNx");

        // Errors name the offending token and carry the usage line.
        Check("junk token rejected", !Spawn(new[] { "abc" }, out _, out _, out _, out _, out _, out string e13) && e13.Contains("'abc'") && e13.Contains("Usage: bot spawn"));
        Check("bad second token rejected", !Spawn(new[] { "2", "abc" }, out _, out _, out _, out _, out _, out string e14) && e14.Contains("'abc'"));
        Check("bad count before coords rejected", !Spawn(new[] { "abc", "10", "20" }, out _, out _, out _, out _, out _, out string e15) && e15.Contains("'abc'"));
        // Two plain numbers are the coords form, never "count plus dangling junk".
        Spawn(new[] { "5", "10" }, out int c17, out float x17, out float z17, out bool p17, out _, out _);
        Check("two numbers are coords, not count+junk", p17 && c17 == 1 && x17 == 5f && z17 == 10f);
        Check("too many tokens rejected", !Spawn(new[] { "1", "2", "3", "4" }, out _, out _, out _, out _, out _, out string e18) && e18.Contains("Too many"));
        Check("non-weapon fourth token rejected", !Spawn(new[] { "1", "2", "3", "extra" }, out _, out _, out _, out _, out _, out string e19) && e19.Contains("'extra'"));

        // Player tail: [count] [weapon] after <nameOrId>.
        Player(new string[0], out int pc1, out string pw1, out _);
        Check("player default count 1", pc1 == 1 && pw1 == null);
        Player(new[] { "3" }, out int pc2, out _, out _);
        Check("player count 3", pc2 == 3);
        Player(new[] { "3", "gunX" }, out int pc3, out string pw3, out _);
        Check("player count + weapon", pc3 == 3 && pw3 == "gunX");
        Player(new[] { "gunX" }, out int pc4, out string pw4, out _);
        Check("player weapon only", pc4 == 1 && pw4 == "gunX");
        Check("player junk rejected", !Player(new[] { "xyz" }, out _, out _, out string pe1) && pe1.Contains("'xyz'") && pe1.Contains("Usage: bot player"));
        Check("player junk after count rejected", !Player(new[] { "2", "xyz" }, out _, out _, out string pe2) && pe2.Contains("'xyz'"));

        // Weapon-token classifier shared by both commands.
        Check("null is not a weapon", !BotArgParser.LooksLikeWeapon(null));
        Check("MIXED matches case-insensitively", BotArgParser.LooksLikeWeapon("MIXED"));
        Check("non-gun word is not a weapon", !BotArgParser.LooksLikeWeapon("rifle"));

        if (_failures > 0) { Console.WriteLine(_failures + " bot arg parser tests FAILED"); return 1; }
        Console.WriteLine("all bot arg parser tests passed");
        return 0;
    }
}
