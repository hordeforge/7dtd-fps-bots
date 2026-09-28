using System;

namespace BotMod.Web
{
    /// <summary>
    /// Wait-handle lifecycle for web -> main-thread dispatches, extracted from
    /// WebApi.RunOnMain so the pure-BCL suite can pin it without game DLLs.
    /// Every dashboard poll and world-touching POST dispatches one of these,
    /// so the signal event must be disposed on every exit path (result, error,
    /// timeout): Wait(TimeSpan) falls back to a lazily created kernel handle,
    /// and an undisposed event per request accumulates OS handles until
    /// finalization. A queued task that runs after its caller timed out
    /// signals an already-disposed event, so its Set is guarded.
    ///
    /// Abandoned dispatches stay observable: once Execute has thrown
    /// TimeoutException the queued work's outcome (it still runs later) would
    /// otherwise be lost in a dead stack frame. <see cref="Abandoned"/> is
    /// invoked exactly then, with the operation name and the work's exception
    /// (null when the late run succeeded), so the host can log that an action
    /// took effect after its 500 was sent, or failed after it.
    /// </summary>
    internal static class MainThreadDispatch
    {
        static Action<string, Exception> _abandoned;

        /// <summary>Host-side sink for outcomes of dispatches whose caller already
        /// timed out. Receives (op, error); error is null when the late work
        /// completed successfully. Null in headless unit runs; WebApi wires it to
        /// the server log. Exceptions thrown by the sink are swallowed: the
        /// abandoned task must never break the main-thread loop that runs it.
        /// Written by the web thread that constructs the REST API and read by the
        /// main thread that runs the late work, so the field is volatile.</summary>
        internal static Action<string, Exception> Abandoned
        {
            get { return System.Threading.Volatile.Read(ref _abandoned); }
            set { System.Threading.Volatile.Write(ref _abandoned, value); }
        }

        /// <summary>Hand <paramref name="work"/> to the main thread via
        /// <paramref name="enqueue"/> and block for at most
        /// <paramref name="timeout"/>, then surface the work's result or its
        /// exception. Throws TimeoutException when the work has not completed by
        /// the time the wait expires (<paramref name="op"/> names it in the
        /// message); a work that completes in the same race returns its result
        /// instead of timing out. A timed-out task still runs later and reports
        /// through <see cref="Abandoned"/>.</summary>
        public static T Execute<T>(Func<T> work, Action<Action> enqueue, TimeSpan timeout, string op)
        {
            T result = default(T);
            Exception error = null;
            var done = new System.Threading.ManualResetEventSlim(false);
            // Completion and abandonment are settled under one gate, so exactly
            // one of them happens and neither can be observed before the other:
            // the task sets finished under the gate once its body (and therefore
            // result/error) is written, the caller sets abandoned under the same
            // gate when its wait expires. A bare flag + volatile read could not
            // order them - Wait(timeout) may return false even for a task that
            // signalled microseconds earlier - which lost the report whenever
            // the work finished in that window: the caller had already thrown
            // its 500, the flag still read false, and an action that took effect
            // (or failed) after that 500 was never logged.
            var gate = new object();
            bool finished = false, abandoned = false;
            try
            {
                enqueue(() =>
                {
                    try { result = work(); }
                    catch (Exception ex) { error = ex; }
                    finally
                    {
                        bool callerGone;
                        lock (gate)
                        {
                            finished = true;
                            callerGone = abandoned;
                        }
                        // A timed-out caller has already disposed this event while
                        // this queued task still holds it; Set must not throw into
                        // the main-thread loop on that abandoned-dispatch path.
                        try { done.Set(); } catch (Exception) { }
                        if (callerGone && Abandoned != null)
                            try { Abandoned(op, error); } catch (Exception) { }
                    }
                });
                if (!done.Wait(timeout))
                {
                    bool stillRunning;
                    lock (gate)
                    {
                        // The work finished inside the deadline race: its result
                        // (or its exception) is already there, so hand it back
                        // instead of reporting a timeout for an action that ran.
                        stillRunning = !finished;
                        if (stillRunning) abandoned = true;
                    }
                    if (stillRunning)
                        throw new TimeoutException("main-thread dispatch timeout after " + timeout.TotalSeconds + "s: " + op);
                }
            }
            finally { done.Dispose(); }
            // ExceptionDispatchInfo, not a bare `throw error`: rethrowing a
            // caught exception restamps its stack at this line, so the work's
            // own frames (where it actually failed) would be gone from every
            // log line above that prints the exception.
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            return result;
        }
    }
}
