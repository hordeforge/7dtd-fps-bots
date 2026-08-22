// IdempotencyLedgerTests — proves the twice-execution property of the
// POST /api/bot replay ledger: running the same keyed request twice yields
// one execution and the same response. Pure BCL; compiled and run by
// scripts/test-idempotency.sh (needs mcs + mono, not part of `make check`).
//
//   bash scripts/test-idempotency.sh
using System;
using System.Threading;
using BotMod.Web;

static class IdempotencyLedgerTests
{
    static int _failures;

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

        // 4. Key validation.
        {
            string longKey = new string('k', IdempotencyLedger.MaxKeyLength + 1);
            Check("empty/null/oversized keys rejected",
                !IdempotencyLedger.IsValidKey(null) && !IdempotencyLedger.IsValidKey("")
                && !IdempotencyLedger.IsValidKey(longKey) && IdempotencyLedger.IsValidKey("ok"));
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

        // 6. Retention window: an expired entry no longer replays.
        {
            IdempotencyLedger.Retention = TimeSpan.FromMilliseconds(50);
            try
            {
                string k = "expiry-1";
                Try(k);
                IdempotencyLedger.Complete(k, "{}");
                Thread.Sleep(120);
                Check("expired key executes again instead of replaying",
                    Try(k) == IdempotencyLedger.BeginResult.Fresh);
            }
            finally { IdempotencyLedger.Retention = TimeSpan.FromMinutes(10); }
        }

        // 7. Completion refreshes the window: replay horizon counts from completion.
        {
            IdempotencyLedger.Retention = TimeSpan.FromMilliseconds(100);
            try
            {
                string k = "refresh-1";
                Try(k);                                            // t=0
                Thread.Sleep(60);                                  // t=60
                IdempotencyLedger.Complete(k, "{}");               // window restarts here
                Thread.Sleep(60);                                  // t=120 > 100 since start, < 100 since completion
                Check("replay window measured from completion",
                    Try(k) == IdempotencyLedger.BeginResult.Replay);
            }
            finally { IdempotencyLedger.Retention = TimeSpan.FromMinutes(10); }
        }

        Console.WriteLine(_failures == 0 ? "all idempotency ledger tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
