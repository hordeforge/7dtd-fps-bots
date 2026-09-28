// WebApiAuthzTests - pins the deny side of the mod's authorization matrix,
// and the record that backs it up.
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
// Authorization is only half the matrix: an action nobody can be attributed
// for is a privileged action with no escalation trail. CheckConsoleAudit-
// Classification therefore pins the console surface's audit rule (every
// subcommand not declared read-only leaves an issuer-attributed line), which
// is what keeps a mutation added later from shipping unlogged. CheckCsrfOrigin
// pins the third leg: the browser session cookie rides along on a cross-origin
// POST, so the Origin comparison is what actually keeps another site from
// driving these mutations.
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

    /// <summary>True when the API's response-header table carries this exact
    /// name/value pair. A class method, not a local function: mcs (the build
    /// backend for the suites) has no C# 7 local functions.</summary>
    static bool HasHeader(string[][] headers, string name, string value)
    {
        foreach (string[] h in headers)
            if (h[0] == name && h[1] == value) return true;
        return false;
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

        // Response headers the API sets on every answer. The stock webserver
        // sends none of them itself, so dropping one here is silent: a body an
        // admin can fetch becomes a body a browser may cache or sniff as
        // script. Asserted against the table MarkNoStore applies, because a
        // live RequestContext only exists inside a running server.
        var headers = BotMod.Web.Bot.ResponseHeaders;
        Check("every response is uncacheable", HasHeader(headers, "Cache-Control", "no-store"));
        Check("every response forbids content-type sniffing",
            HasHeader(headers, "X-Content-Type-Options", "nosniff"));
        for (int i = 0; i < headers.Length; i++)
            Check("response header " + headers[i][0] + " carries a value", headers[i].Length == 2 && headers[i][1] != "");

        CheckConsoleAuditClassification();
        CheckCsrfOrigin();

        Console.WriteLine(_failures == 0 ? "all web api authz matrix tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>The third leg of the matrix: the dashboard authenticates with
    /// the stock `sid` session cookie, which a browser attaches to a
    /// cross-origin POST exactly as it does to a same-origin one, so cookie
    /// auth alone proves nothing about who sent the request. Every POST is a
    /// mutation, so the Origin comparison is what keeps another origin from
    /// driving them. The direction that costs security is the refusing side:
    /// a bypass here is an attacker page, a same-origin page is the dashboard.
    ///
    /// The prefix cases matter most. "https://admin.example.evil.test" starts
    /// with "https://admin.example", so any check that compared the Origin to
    /// the Host as a string prefix would wave it through; a parsed host
    /// comparison does not.</summary>
    static void CheckCsrfOrigin()
    {
        var origin = BotMod.Web.Bot.OriginAllowed;
        Check("no Origin header is allowed (scripted/API-token callers)", origin(null, "admin.example:26900"));
        Check("empty Origin header is allowed", origin("", "admin.example:26900"));
        Check("same origin over http is allowed", origin("http://admin.example:26900", "admin.example:26900"));
        Check("same origin over https is allowed", origin("https://admin.example", "admin.example"));
        Check("same host, default port spelled out, is allowed", origin("http://admin.example:80", "admin.example"));

        Check("a foreign origin is refused", !origin("https://evil.test", "admin.example:26900"));
        Check("a foreign origin with a matching port is refused", !origin("http://evil.test:26900", "admin.example:26900"));
        Check("a same-host different-port origin is refused", !origin("http://admin.example:8080", "admin.example:26900"));
        // The two prefix bypasses, one per side.
        Check("a host that starts with the server's host is refused", !origin("https://admin.example.evil.test", "admin.example"));
        Check("a host the server's host starts with is refused", !origin("https://admin.ex", "admin.example.attacker.test"));
        Check("a userinfo trick is refused", !origin("https://admin.example@evil.test", "admin.example"));
        // What a sandboxed iframe, a data: document or a malformed value sends.
        Check("the literal null origin is refused", !origin("null", "admin.example"));
        Check("a relative origin is refused", !origin("/api/bot", "admin.example"));
        Check("a non-http scheme is refused", !origin("file://admin.example", "admin.example"));
        // A throw in the header store must refuse, never admit.
        Check("a missing Host header refuses a present Origin", !origin("https://admin.example", null));
    }

    /// <summary>Second half of the matrix: who may act is only half of it,
    /// the record of who did is the other half. The console command audits
    /// every invocation it does not classify as read-only, so the question a
    /// subcommand added later has to answer is "is this really a read?". These
    /// checks fail when a subcommand is classified by neither list (added to the
    /// dispatch switch, never judged) or by both, and pin the read-only set to
    /// the commands that genuinely only read, so the default cannot be widened
    /// to silence a mutation. Aliases are included: `bot rm` and `bot remove`
    /// are the same mutation, and an audit that only covers the canonical name
    /// leaves the shorter spelling unrecorded.</summary>
    static void CheckConsoleAuditClassification()
    {
        string[] known = BotMod.Commands.ConsoleCmdBot.KnownSubcommands();
        Check("the console dispatches at least one subcommand", known.Length > 0);

        // Read-only, as documented in `bot help`. Anything reading live world
        // state is a read; anything writing config, spawning or despawning is
        // not, however cheap the command looks.
        string[] readOnly =
        {
            "help", "?", "h", "status", "config", "cfg", "list", "ls", "players", "who"
        };
        for (int i = 0; i < readOnly.Length; i++)
        {
            string sub = readOnly[i];
            bool listed = false;
            for (int k = 0; k < known.Length; k++)
                if (string.Equals(known[k], sub, StringComparison.OrdinalIgnoreCase)) { listed = true; break; }
            Check("read-only subcommand '" + sub + "' is in the dispatch list", listed);
            Check("read-only subcommand '" + sub + "' is not audited", !BotMod.Commands.ConsoleCmdBot.IsAuditedSubcommand(sub));
        }

        // Every mutating subcommand and alias, from the switch in
        // ConsoleCmdBot.Execute, must be audited. A name here that Execute
        // stopped dispatching is dead weight in the table, not a failure, so
        // this asserts the direction that costs security: audited, not silent.
        string[] mutating =
        {
            "spawn", "add", "player", "near", "at", "remove", "rm", "kick", "clear",
            "count", "set", "weapon", "gun", "skill", "difficulty", "neural", "vs", "shoot",
            "team", "squad", "teams", "reload", "enable", "disable"
        };
        for (int i = 0; i < mutating.Length; i++)
            Check("mutating subcommand '" + mutating[i] + "' is audited",
                BotMod.Commands.ConsoleCmdBot.IsAuditedSubcommand(mutating[i]));

        // Classification is total over the dispatch list: an unclassified name
        // is one whose audit behavior nobody decided, which is the state this
        // check exists to make loud. (It currently audits by default, so the
        // assertion below holds for a known name either way; the value is that
        // a new subcommand cannot be added to one list and quietly dropped
        // from the other without this failing first.)
        for (int k = 0; k < known.Length; k++)
        {
            string sub = known[k];
            bool inReadOnly = false;
            for (int i = 0; i < readOnly.Length; i++)
                if (string.Equals(readOnly[i], sub, StringComparison.OrdinalIgnoreCase)) { inReadOnly = true; break; }
            bool inMutating = false;
            for (int i = 0; i < mutating.Length; i++)
                if (string.Equals(mutating[i], sub, StringComparison.OrdinalIgnoreCase)) { inMutating = true; break; }
            Check("subcommand '" + sub + "' is classified exactly once",
                inReadOnly ^ inMutating);
        }

        // An unknown name audits rather than staying silent: the failure worth
        // avoiding is a mutation nobody logged, so the default has to err that
        // way.
        Check("an unclassified subcommand name is still audited",
            BotMod.Commands.ConsoleCmdBot.IsAuditedSubcommand("nosuchsubcommand"));
    }
}
