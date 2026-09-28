// IdempotencyLedgerTests: proves the twice-execution property of the
// POST /api/bot replay ledger: running the same keyed request twice yields
// one execution and the same response. Pure BCL; compiled and run by
// scripts/test-idempotency.sh (needs mcs + mono, not part of `make check`).
//
//   bash scripts/test-idempotency.sh
using System;
using System.Text;
using BotMod.Web;

static class IdempotencyLedgerTests
{
    static int _failures;
    // Virtual clock substituted for IdempotencyLedger.ElapsedNow (monotonic
    // elapsed time, like production): retention tests advance it instead of
    // sleeping, so boundaries are exact and reproducible.
    static TimeSpan _t = TimeSpan.Zero;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    // Begin without caring about the cached body. Body-less helper for the
    // scenarios that do not vary the request; the key-reuse cases below pass
    // their own fingerprint.
    static IdempotencyLedger.BeginResult Try(string key)
    {
        string ignored;
        return IdempotencyLedger.TryBegin(key, "fp", out ignored);
    }

    static IdempotencyLedger.BeginResult Try(string key, string fingerprint)
    {
        string ignored;
        return IdempotencyLedger.TryBegin(key, fingerprint, out ignored);
    }

    static int Main()
    {
        // One virtual clock drives the entire run, installed once: scenario
        // times only ever move forward (as a monotonic clock does), so
        // retention boundaries are exact and capacity-eviction order never
        // depends on stamp jitter mid-scenario.
        IdempotencyLedger.ElapsedNow = () => _t;

        // 1. Run twice == run once: the second Begin replays the recorded body.
        {
            string k = "replay-1";
            var first = IdempotencyLedger.TryBegin(k, "fp", out string b1);
            Check("first execution claims key", first == IdempotencyLedger.BeginResult.Fresh && b1 == null);
            IdempotencyLedger.Complete(k, "{\"spawned\":4}");
            var second = IdempotencyLedger.TryBegin(k, "fp", out string b2);
            Check("duplicate replays recorded response",
                second == IdempotencyLedger.BeginResult.Replay && b2 == "{\"spawned\":4}");
        }

        // 2. Concurrent duplicate: same key while the first is still executing.
        {
            string k = "concurrent-1";
            var a = Try(k);
            var b = IdempotencyLedger.TryBegin(k, "fp", out string cached);
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

        // 3b. Complete/Fail on a key that was never begun (already aged out,
        //     evicted by the capacity cap, or released by an earlier Fail):
        //     documented silent no-ops. The POST handler calls both on error
        //     and client-rejection paths where the entry may be long gone, so
        //     a throw here would turn every rejected retry into a 500.
        {
            string k = "never-begun-1";
            IdempotencyLedger.Complete(k, "{\"spawned\":0}");
            IdempotencyLedger.Fail(k);
            Check("complete/fail on unknown key are silent no-ops",
                Try(k) == IdempotencyLedger.BeginResult.Fresh);
        }

        // 4. Key validation, including the exact boundary: max length is the
        //    last accepted length, one past it is rejected. The unit is
        //    characters, so an astral-plane key of half the limit in code
        //    units is still accepted.
        {
            string maxKey = new string('k', IdempotencyLedger.MaxKeyChars);
            string longKey = new string('k', IdempotencyLedger.MaxKeyChars + 1);
            Check("empty/null keys rejected",
                !IdempotencyLedger.IsValidKey(null) && !IdempotencyLedger.IsValidKey(""));
            Check("key at exactly MaxKeyChars accepted", IdempotencyLedger.IsValidKey(maxKey));
            Check("key one past MaxKeyChars rejected", !IdempotencyLedger.IsValidKey(longKey));
            Check("ordinary key accepted", IdempotencyLedger.IsValidKey("ok"));
            var emoji = new StringBuilder();
            for (int i = 0; i < IdempotencyLedger.MaxKeyChars; i++) emoji.Append("\uD83D\uDE00");
            string emojiKey = emoji.ToString();
            Check("emoji key at exactly MaxKeyChars accepted (128 chars, 256 code units)",
                emojiKey.Length == IdempotencyLedger.MaxKeyChars * 2
                && IdempotencyLedger.IsValidKey(emojiKey));
            Check("emoji key one past MaxKeyChars rejected",
                !IdempotencyLedger.IsValidKey(emojiKey + "\uD83D\uDE00"));
        }

        // 5. Bounded state: capacity cap holds under more keys than Capacity,
        //    and every hard-cap eviction is reported through the host sink so
        //    dedup loss for the evicted keys is not silent.
        {
            int evictedTotal = 0;
            int sinkCalls = 0;
            IdempotencyLedger.CapacityEvicted = n => { sinkCalls++; evictedTotal += n; };
            try
            {
                for (int i = 0; i < IdempotencyLedger.Capacity * 3; i++)
                {
                    string k = "cap-" + i.ToString();
                    if (Try(k) == IdempotencyLedger.BeginResult.Fresh)
                        IdempotencyLedger.Complete(k, "{}");
                }
                Check("ledger never exceeds capacity", IdempotencyLedger.Count <= IdempotencyLedger.Capacity);
                Check("capacity overflow reported through the sink",
                    sinkCalls > 0 && evictedTotal >= IdempotencyLedger.Capacity * 2 - IdempotencyLedger.Capacity);
            }
            finally { IdempotencyLedger.CapacityEvicted = null; }
        }

        // 6. Retention window, driven by the virtual clock: exact boundaries,
        // no sleeps. An entry replays up to the last instant of the window and
        // executes again one tick past it. First jump forward past test 5's
        // capacity-fill entries so they age out: boundary assertions must not
        // share a full ledger, where the arbitrary oldest-tie eviction could
        // remove the entry under test instead of a filler.
        {
            _t += TimeSpan.FromMinutes(11);
            IdempotencyLedger.Retention = TimeSpan.FromSeconds(10);
            try
            {
                string k = "expiry-1";
                Try(k);
                IdempotencyLedger.Complete(k, "{}");
                _t += IdempotencyLedger.Retention - TimeSpan.FromTicks(1);
                Check("entry inside retention still replays",
                    Try(k) == IdempotencyLedger.BeginResult.Replay);
                _t += TimeSpan.FromTicks(2); // one tick past the window
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
                _t += TimeSpan.FromSeconds(60);                  // t=60
                IdempotencyLedger.Complete(k, "{}");               // window restarts here
                _t += IdempotencyLedger.Retention;               // exactly retention since completion
                Check("replay window measured from completion (exact boundary)",
                    Try(k) == IdempotencyLedger.BeginResult.Replay);
                _t += TimeSpan.FromTicks(1);                     // first instant past the window
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
                _t += TimeSpan.FromSeconds(9);
                Check("claim still held near end of retention",
                    Try(k) == IdempotencyLedger.BeginResult.InProgress);
                _t += TimeSpan.FromSeconds(2);
                Check("stale claim ages out and the retry can run",
                    Try(k) == IdempotencyLedger.BeginResult.Fresh);
            }
            finally { IdempotencyLedger.Retention = TimeSpan.FromMinutes(10); }
        }

        // 9. The production clock's units. ElapsedNow must convert the raw
        // Stopwatch reading through Stopwatch.Frequency, on hosts with and
        // without a high-resolution counter. Reading a low-resolution
        // counter's ticks as DateTime ticks runs the clock ~10000x slow, and
        // the virtual-clock scenarios above cannot see that: they replace
        // the source entirely.
        {
            var real = (Func<TimeSpan>)Delegate.CreateDelegate(
                typeof(Func<TimeSpan>),
                typeof(IdempotencyLedger).GetMethod("DefaultElapsedNow",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            TimeSpan t0 = real();
            System.Threading.Thread.Sleep(60);
            TimeSpan t1 = real();
            sw.Stop();
            TimeSpan reported = t1 - t0;
            Check("production clock advances with the monotonic counter",
                reported >= TimeSpan.FromMilliseconds(40));
            Check("production clock does not outrun the monotonic counter",
                reported <= sw.Elapsed + TimeSpan.FromMilliseconds(50));
        }

        // 10. Real threads on one key. The ledger's contract is "one execution"
        // and the class doc states access is thread-safe because web handlers
        // run on thread pool threads; scenario 2 only walks the state machine
        // sequentially, so an unsynchronized Dictionary would still pass it.
        // Racing TryBegin on one key must elect exactly one executor: every
        // other caller sees InProgress, and the losers' cached bodies stay
        // null (they must not execute and must not be handed a body).
        {
            const int racers = 16;
            var errors = new System.Collections.Generic.List<string>();
            var results = new IdempotencyLedger.BeginResult[racers];
            var bodies = new string[racers];
            var start = new System.Threading.ManualResetEvent(false);
            var done = new System.Threading.ManualResetEvent(false);
            int remaining = racers;
            for (int i = 0; i < racers; i++)
            {
                int id = i;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        start.WaitOne(30000);
                        results[id] = IdempotencyLedger.TryBegin("race-1", "fp", out bodies[id]);
                    }
                    catch (Exception ex) { lock (errors) errors.Add("racer" + id + ": " + ex); }
                    finally { if (System.Threading.Interlocked.Decrement(ref remaining) == 0) done.Set(); }
                });
            }
            string k = "race-1";
            IdempotencyLedger.Retention = TimeSpan.FromSeconds(10);
            try
            {
                // Clear any earlier entry so the race starts from empty.
                IdempotencyLedger.Fail(k);
                start.Set();
                bool finished = done.WaitOne(30000);
                Check("key race finished within timeout", finished);
                int fresh = 0, inProgress = 0, other = 0, loserBody = 0;
                for (int i = 0; i < racers; i++)
                {
                    if (results[i] == IdempotencyLedger.BeginResult.Fresh) fresh++;
                    else if (results[i] == IdempotencyLedger.BeginResult.InProgress) inProgress++;
                    else other++;
                    if (results[i] != IdempotencyLedger.BeginResult.Fresh && bodies[i] != null) loserBody++;
                }
                Check("exactly one racer executes the key (fresh=" + fresh + ")", fresh == 1);
                Check("every loser is told the key is in progress", inProgress == racers - 1 && other == 0);
                Check("no loser was handed a cached body", loserBody == 0);
                Check("race raised no exceptions (" + errors.Count + ")", errors.Count == 0);
                foreach (string e in errors) Console.WriteLine("     " + e);
                // The winner completes; every later caller now replays that
                // body instead of executing, which is the whole point of the
                // ledger under a real retry storm.
                IdempotencyLedger.Complete(k, "{\"spawned\":1}");
                string replayed;
                Check("post-race duplicate replays the winner's body",
                    IdempotencyLedger.TryBegin(k, "fp", out replayed) == IdempotencyLedger.BeginResult.Replay
                    && replayed == "{\"spawned\":1}");
            }
            finally { IdempotencyLedger.Retention = TimeSpan.FromMinutes(10); }
        }

        // 11. Key reuse for a different request. A key identifies one logical
        //     request, so a body that differs under the same key must neither
        //     execute nor replay: replaying would answer the new request with
        //     the old response and silently drop the operation it asked for.
        {
            string k = "reuse-1";
            Check("first request claims key",
                Try(k, "action=spawn\ncount=2") == IdempotencyLedger.BeginResult.Fresh);
            IdempotencyLedger.Complete(k, "{\"spawned\":2}");
            string cached;
            var reused = IdempotencyLedger.TryBegin(k, "action=spawn\ncount=8", out cached);
            Check("same key + different body is rejected, not replayed",
                reused == IdempotencyLedger.BeginResult.Mismatched && cached == null);
            // The original claim survives the rejection, so the request that
            // owns the key still replays for the rest of the window.
            Check("original request still replays after a reuse attempt",
                Try(k, "action=spawn\ncount=2") == IdempotencyLedger.BeginResult.Replay);
        }

        // 12. Reuse while the first request is still in flight takes the same
        //     path: the concurrent duplicate is told the key is taken, never
        //     handed the in-flight operation's response.
        {
            string k = "reuse-2";
            Check("first request claims key",
                Try(k, "a") == IdempotencyLedger.BeginResult.Fresh);
            Check("in-flight key reused for another body is rejected",
                Try(k, "b") == IdempotencyLedger.BeginResult.Mismatched);
            Check("in-flight duplicate of the same body is still InProgress",
                Try(k, "a") == IdempotencyLedger.BeginResult.InProgress);
        }

        // 13. A null fingerprint is a body-less request, not "any body": a
        //     key claimed with one never matches a different fingerprint, and
        //     a retry that sends the same null still replays. Null and the
        //     empty string are the same body-less request (Fingerprint of a
        //     null body is ""), so they must match each other.
        {
            string k = "reuse-3";
            string ignored;
            Check("null fingerprint claims key",
                IdempotencyLedger.TryBegin(k, null, out ignored) == IdempotencyLedger.BeginResult.Fresh);
            IdempotencyLedger.Complete(k, "{}");
            Check("null fingerprint replayed by the same request",
                IdempotencyLedger.TryBegin(k, null, out ignored) == IdempotencyLedger.BeginResult.Replay);
            Check("null fingerprint does not match a real body",
                IdempotencyLedger.TryBegin(k, "action=spawn", out ignored) == IdempotencyLedger.BeginResult.Mismatched);
            Check("empty and null fingerprints are the same request",
                IdempotencyLedger.TryBegin("reuse-4", "", out ignored) == IdempotencyLedger.BeginResult.Fresh
                && IdempotencyLedger.TryBegin("reuse-4", null, out ignored) == IdempotencyLedger.BeginResult.InProgress);
        }

        Console.WriteLine(_failures == 0 ? "all idempotency ledger tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
