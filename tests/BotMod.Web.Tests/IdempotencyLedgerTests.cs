// IdempotencyLedgerTests — proves the twice-execution property of the
// POST /api/bot replay ledger: running the same keyed request twice yields
// one execution and the same response. Pure BCL; compiled and run by
// scripts/test-idempotency.sh (needs mcs + mono, not part of `make check`).
//
//   bash scripts/test-idempotency.sh
using System;
using BotMod.Web;

static class IdempotencyLedgerTests
{
    static int _failures;
    // Virtual clock substituted for IdempotencyLedger.UtcNow: retention tests
    // advance it instead of sleeping, so boundaries are exact and reproducible.
    static DateTime _now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    // Begin without caring about the cached body.
    static IdempotencyLedger.BeginResult Try(string key)
    {
        string ignored;
        return IdempotencyLedger.TryBegin(key, out ignored);
    }

    static int Main()
    {
        // One monotonic virtual clock drives the entire run, installed once:
        // scenario times only ever move forward, so retention boundaries are
        // exact and capacity-eviction order never depends on the host clock
        // (a backward jump would make new entries the oldest and let the
        // capacity cap eat them mid-scenario).
        IdempotencyLedger.UtcNow = () => _now;

        // 1. Run twice == run once: the second Begin replays the recorded body.
        {
            string k = "replay-1";
            var first = IdempotencyLedger.TryBegin(k, out string b1);
            Check("first execution claims key", first == IdempotencyLedger.BeginResult.Fresh && b1 == null);
            IdempotencyLedger.Complete(k, "{\"spawned\":4}");
            var second = IdempotencyLedger.TryBegin(k, out string b2);
            Check("duplicate replays recorded response",
                second == IdempotencyLedger.BeginResult.Replay && b2 == "{\"spawned\":4}");
        }

        // 2. Concurrent duplicate: same key while the first is still executing.
        {
            string k = "concurrent-1";
            var a = Try(k);
            var b = IdempotencyLedger.TryBegin(k, out string cached);
            Check("in-flight duplicate is rejected, not executed",
                a == IdempotencyLedger.BeginResult.Fresh
                && b == IdempotencyLedger.BeginResult.InProgress && cached == null);
            IdempotencyLedger.Complete(k, "{}");
            Check("late retry after completion replays", Try(k) == IdempotencyLedger.BeginResult.Replay);
        }

        // 3. Failed execution releases the key so the retry can run.
        {
            string k = "failure-1";
            Try(k);
            IdempotencyLedger.Fail(k);
            Check("retry after failure executes again", Try(k) == IdempotencyLedger.BeginResult.Fresh);
        }

        // 4. Key validation, including the exact boundary: max length is the
        //    last accepted length, one past it is rejected.
        {
            string maxKey = new string('k', IdempotencyLedger.MaxKeyLength);
            string longKey = new string('k', IdempotencyLedger.MaxKeyLength + 1);
            Check("empty/null keys rejected",
                !IdempotencyLedger.IsValidKey(null) && !IdempotencyLedger.IsValidKey(""));
            Check("key at exactly MaxKeyLength accepted", IdempotencyLedger.IsValidKey(maxKey));
            Check("key one past MaxKeyLength rejected", !IdempotencyLedger.IsValidKey(longKey));
            Check("ordinary key accepted", IdempotencyLedger.IsValidKey("ok"));
        }

        // 5. Bounded state: capacity cap holds under more keys than Capacity.
        {
            for (int i = 0; i < IdempotencyLedger.Capacity * 3; i++)
            {
                string k = "cap-" + i.ToString();
                if (Try(k) == IdempotencyLedger.BeginResult.Fresh)
                    IdempotencyLedger.Complete(k, "{}");
            }
            Check("ledger never exceeds capacity", IdempotencyLedger.Count <= IdempotencyLedger.Capacity);
        }

        // 6. Retention window, driven by the virtual clock: exact boundaries,
        // no sleeps. An entry replays up to the last instant of the window and
        // executes again one tick past it. First jump forward past test 5's
        // capacity-fill entries so they age out: boundary assertions must not
        // share a full ledger, where the arbitrary oldest-tie eviction could
        // remove the entry under test instead of a filler.
        {
            _now += TimeSpan.FromMinutes(11);
            IdempotencyLedger.Retention = TimeSpan.FromSeconds(10);
            try
            {
                string k = "expiry-1";
                Try(k);
                IdempotencyLedger.Complete(k, "{}");
                _now += IdempotencyLedger.Retention - TimeSpan.FromTicks(1);
                Check("entry inside retention still replays",
                    Try(k) == IdempotencyLedger.BeginResult.Replay);
                _now += TimeSpan.FromTicks(2); // one tick past the window
                Check("expired key executes again instead of replaying",
                    Try(k) == IdempotencyLedger.BeginResult.Fresh);
            }
            finally { IdempotencyLedger.Retention = TimeSpan.FromMinutes(10); }
        }

        // 7. Completion refreshes the window: replay horizon counts from
        // completion, so a key begun long ago still replays for a full window
        // after its request finishes.
        {
            IdempotencyLedger.Retention = TimeSpan.FromSeconds(10);
            try
            {
                string k = "refresh-1";
                Try(k);                                            // t=0
                _now += TimeSpan.FromSeconds(60);                  // t=60
                IdempotencyLedger.Complete(k, "{}");               // window restarts here
                _now += IdempotencyLedger.Retention;               // exactly retention since completion
                Check("replay window measured from completion (exact boundary)",
                    Try(k) == IdempotencyLedger.BeginResult.Replay);
                _now += TimeSpan.FromTicks(1);                     // first instant past the window
                Check("replay ends one tick past retention-since-completion",
                    Try(k) == IdempotencyLedger.BeginResult.Fresh);
            }
            finally { IdempotencyLedger.Retention = TimeSpan.FromMinutes(10); }
        }

        // 8. A claimed-but-never-completed entry (crash between Begin and
        // Complete/Fail) unblocks when it ages out, deterministically.
        {
            IdempotencyLedger.Retention = TimeSpan.FromSeconds(10);
            try
            {
                string k = "stale-claim-1";
                Check("in-flight claim holds", Try(k) == IdempotencyLedger.BeginResult.Fresh);
                Check("duplicate while in flight is rejected",
                    Try(k) == IdempotencyLedger.BeginResult.InProgress);
                _now += TimeSpan.FromSeconds(9);
                Check("claim still held near end of retention",
                    Try(k) == IdempotencyLedger.BeginResult.InProgress);
                _now += TimeSpan.FromSeconds(2);
                Check("stale claim ages out and the retry can run",
                    Try(k) == IdempotencyLedger.BeginResult.Fresh);
            }
            finally { IdempotencyLedger.Retention = TimeSpan.FromMinutes(10); }
        }

        Console.WriteLine(_failures == 0 ? "all idempotency ledger tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
