// WebApiAuthzTests - pins the deny side of the mod's authorization matrix.
//
// Every /api/bot operation (status read included) must stay behind permission
// level 0, enforced by the stock webserver from the levels declared in
// WebApi.DefaultMethodPermissionLevels; nothing in the mod re-checks or
// relaxes them. Likewise the `bot` console command relies on
// ConsoleCmdAbstract's default level 0. A refactor that widens either
// declaration (or that misaligns the array with the game's ERequestMethod
// slot order) would silently hand bot control to lower-privileged callers,
// so this suite asserts the hostile-path side: non-admins are denied for
// every method slot, not just that admins succeed.
//
// Slot semantics (verified against the game binary): AdminWebModules'
// WebModule ctor normalizes a declared array shorter than ERequestMethod.Count
// to length 7, padding HEAD/OPTIONS with MethodLevelNotSupported (0x80000001),
// which AbsRestApi.Authorized treats as an unconditional deny. Level 0 means
// "requires the highest permission level"; Authorized allows only callers
// whose PermissionLevel is <= the declared level.
//
// Instances are created without running constructors: the Bot ctor registers
// itself with the live AdminTools singleton, which exists only inside a
// running server. The asserted members return constants and touch no
// instance state, so uninitialized objects are safe here.
using System;
using System.Runtime.Serialization;
using Webserver;

static class WebApiAuthzTests
{
    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    static int Main()
    {
        // The dispatch contract the declaration indexes into. If a game
        // update reorders these values, the per-method array would silently
        // grant/deny the wrong verbs - this must fail loudly instead.
        Check("ERequestMethod slots unchanged (Other..DELETE = 0..4, Count = 7)",
            (int)ERequestMethod.Other == 0 && (int)ERequestMethod.GET == 1 &&
            (int)ERequestMethod.POST == 2 && (int)ERequestMethod.PUT == 3 &&
            (int)ERequestMethod.DELETE == 4 && (int)ERequestMethod.Count == 7);

        var api = (BotMod.Web.Bot)FormatterServices.GetUninitializedObject(typeof(BotMod.Web.Bot));

        int[] levels = api.DefaultMethodPermissionLevels();
        Check("web api declares one level per real request-method slot", levels.Length == 5);
        for (int i = 0; i < levels.Length; i++)
            Check("web api method slot " + i + " requires level 0 (admin-only)", levels[i] == 0);

        // Global fallback used when an operator marks a method "inherit" in
        // webpermissions.xml: must inherit an admin-only level, not a public one.
        Check("web api global fallback level is 0", api.DefaultPermissionLevel() == 0);

        var cmd = (BotMod.Commands.ConsoleCmdBot)FormatterServices.GetUninitializedObject(
            typeof(BotMod.Commands.ConsoleCmdBot));
        Check("console command keeps default permission level 0", cmd.DefaultPermissionLevel == 0);

        Console.WriteLine(_failures == 0 ? "all web api authz matrix tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
