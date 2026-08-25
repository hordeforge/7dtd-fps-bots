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

        // 6. Abandoned-dispatch reporting: once the caller timed out, the late
        //    work's outcome must reach the Abandoned sink instead of dying in
        //    a dead stack frame (a failing spawn would otherwise look like a
        //    clean 500 while the action actually ran or failed afterwards).
        {
            Action abandoned = null;
            string seenOp = null;
            Exception seenError = null;
            int calls = 0;
            MainThreadDispatch.Abandoned = (op, error) => { calls++; seenOp = op; seenError = error; };
            try
            {
                try
                {
                    MainThreadDispatch.Execute<int>(() => throw new InvalidOperationException("late boom"),
                        task => { abandoned = task; }, TimeSpan.FromMilliseconds(200), "spawnNear");
                }
                catch (TimeoutException) { }
                abandoned?.Invoke();
                Check("abandoned failing work reports through the sink",
                    calls == 1 && seenOp == "spawnNear" && seenError is InvalidOperationException);
            }
            finally { MainThreadDispatch.Abandoned = null; }
        }

        // 7. Abandoned success reports a null error (action took effect after
        //    the 500: the "lost response after the server acted" case).
        {
            Action abandoned = null;
            Exception seenError = new ApplicationException("sentinel");
            int calls = 0;
            MainThreadDispatch.Abandoned = (op, error) => { calls++; seenError = error; };
            try
            {
                try
                {
                    MainThreadDispatch.Execute<int>(() => 9,
                        task => { abandoned = task; }, TimeSpan.FromMilliseconds(200), "status");
                }
                catch (TimeoutException) { }
                abandoned?.Invoke();
                Check("abandoned successful work reports a null error",
                    calls == 1 && seenError == null);
            }
            finally { MainThreadDispatch.Abandoned = null; }
        }

        // 8. In-window completion must not touch the sink (no false alarms).
        {
            int calls = 0;
            MainThreadDispatch.Abandoned = (op, error) => calls++;
            try
            {
                int r = MainThreadDispatch.Execute(() => 5, task => task(),
                    TimeSpan.FromSeconds(5), "in-window");
                Check("in-window completion returns normally", r == 5);
                Check("in-window completion never reports abandonment", calls == 0);
            }
            finally { MainThreadDispatch.Abandoned = null; }
        }

        // 9. A throwing sink must not break the abandoned task (it runs on
        //    the main-thread loop).
        {
            Action abandoned = null;
            MainThreadDispatch.Abandoned = (op, error) => throw new ApplicationException("sink down");
            try
            {
                try
                {
                    MainThreadDispatch.Execute<int>(() => 1,
                        task => { abandoned = task; }, TimeSpan.FromMilliseconds(200), "throwing-sink");
                }
                catch (TimeoutException) { }
                bool threw = false;
                try { abandoned?.Invoke(); }
                catch (Exception ex) { threw = ex is ApplicationException; }
                Check("throwing sink cannot break the abandoned task", !threw);
            }
            finally { MainThreadDispatch.Abandoned = null; }
        }

        Console.WriteLine(_failures == 0 ? "all main-thread dispatch tests passed" : _failures + " test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
