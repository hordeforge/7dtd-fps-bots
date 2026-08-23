// MainThreadDispatchTests — pins the web -> main-thread dispatch wait-handle
// lifecycle: the signal event is released on every exit path (result, error,
// timeout) and a queued task that runs after its caller timed out (abandoned
// dispatch) must be able to signal the disposed event without throwing.
// Pure BCL; compiled and run by scripts/test-idempotency.sh.
//
//   bash scripts/test-idempotency.sh
using System;
using System.Diagnostics;
using System.Threading;
using BotMod.Web;

static class MainThreadDispatchTests
{
    static int _failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    static int Main()
    {
        // 1. Success path: enqueued work runs, its result comes back, no throw.
        {
            int r = MainThreadDispatch.Execute(() => 42, task => task(),
                TimeSpan.FromSeconds(5), "success");
            Check("work result is returned", r == 42);
        }

        // 2. Error propagation: a throwing work item surfaces its exception
        //    on the caller instead of being swallowed at the queue boundary.
        {
            Exception caught = null;
            try
            {
                MainThreadDispatch.Execute<int>(
                    () => { throw new InvalidOperationException("boom"); },
                    task => task(), TimeSpan.FromSeconds(5), "error");
            }
            catch (Exception ex) { caught = ex; }
            Check("work exception propagates to caller",
                caught is InvalidOperationException && caught.Message == "boom");
        }

        // 3. Timeout path: the queued work never runs within the window, so
        //    Execute throws TimeoutException naming the operation. The
        //    abandoned task then still runs later (the engine drains its
        //    queue) and signals an already-disposed event: that late Set must
        //    not throw into the main-thread loop. The work itself DOES run on
        //    that late invocation — the caller has already seen TimeoutException,
        //    so from the retry-safety side the outcome is ambiguous, and
        //    WebApi.HandleRestPost must keep the idempotency key claimed for
        //    exactly this case instead of releasing it for a re-run.
        {
            Action abandoned = null;
            Exception timeoutEx = null;
            bool lateWorkRan = false;
            try
            {
                MainThreadDispatch.Execute<string>(() => { lateWorkRan = true; return "late"; },
                    task => { abandoned = task; },
                    TimeSpan.FromMilliseconds(200), "spawn");
            }
            catch (TimeoutException ex) { timeoutEx = ex; }
            Check("timed-out dispatch throws TimeoutException", timeoutEx != null);
            Check("timeout names the operation",
                timeoutEx != null && timeoutEx.Message.Contains("spawn"));
            Check("work did not run before the timeout fired", !lateWorkRan);
            try
            {
                abandoned?.Invoke();
                Check("abandoned task signals disposed event safely", true);
            }
            catch (Exception ex)
            {
                Console.WriteLine("     late Set threw: " + ex.GetType().Name + ": " + ex.Message);
                Check("abandoned task signals disposed event safely", false);
            }
            Check("abandoned task still executes its work after the timeout", lateWorkRan);
        }

        // 4. Enqueue failure surfaces immediately (nothing waits out the full
        //    window behind a broken queue).
        {
            var sw = Stopwatch.StartNew();
            Exception caught = null;
            try
            {
                MainThreadDispatch.Execute<int>(() => 1,
                    task => { throw new ApplicationException("queue down"); },
                    TimeSpan.FromSeconds(30), "enqueue-fail");
            }
            catch (ApplicationException ex) { caught = ex; }
            Check("enqueue failure surfaces immediately",
                caught != null && caught.Message == "queue down");
            Check("enqueue failure does not wait out the window", sw.ElapsedMilliseconds < 5000);
        }

        // 5. Slow-but-completing work inside the window returns normally
        //    (real cross-thread shape: the queue signals from another thread).
        {
            int r = MainThreadDispatch.Execute(() => 7, task =>
                {
                    var t = new Thread(() => { Thread.Sleep(50); task(); });
                    t.Start();
                }, TimeSpan.FromSeconds(5), "cross-thread");
            Check("cross-thread completion returns work result", r == 7);
        }

        Console.WriteLine(_failures == 0 ? "all main-thread dispatch tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
