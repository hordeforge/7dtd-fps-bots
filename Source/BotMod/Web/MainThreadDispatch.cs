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
        /// <summary>Host-side sink for outcomes of dispatches whose caller already
        /// timed out. Receives (op, error); error is null when the late work
        /// completed successfully. Null in headless unit runs; WebApi wires it to
        /// the server log. Exceptions thrown by the sink are swallowed: the
        /// abandoned task must never break the main-thread loop that runs it.</summary>
        internal static Action<string, Exception> Abandoned = null;

        /// <summary>Hand <paramref name="work"/> to the main thread via
        /// <paramref name="enqueue"/> and block for at most
        /// <paramref name="timeout"/>, then surface the work's result or its
        /// exception. Throws TimeoutException when the work does not complete
        /// in time (<paramref name="op"/> names it in the message); the
        /// enqueued work still runs later and reports through
        /// <see cref="Abandoned"/>.</summary>
        public static T Execute<T>(Func<T> work, Action<Action> enqueue, TimeSpan timeout, string op)
        {
            T result = default(T);
            Exception error = null;
            var done = new System.Threading.ManualResetEventSlim(false);
            // Volatile flag: set by the caller only after Wait returned false
            // (timeout won). A worker that completed before the timeout reads it
            // as false; one completing after reads true. The window where the
            // worker finished its body but not yet its finally while the caller
            // times out still reports correctly: the caller cannot have seen a
            // result at that point, so from its side the outcome was lost.
            bool abandoned = false;
            try
            {
                enqueue(() =>
                {
                    try { result = work(); }
                    catch (Exception ex) { error = ex; }
                    finally
                    {
                        if (System.Threading.Volatile.Read(ref abandoned) && Abandoned != null)
                            try { Abandoned(op, error); } catch (Exception) { }
                        // A timed-out caller has already disposed this event while
                        // this queued task still holds it; Set must not throw into
                        // the main-thread loop on that abandoned-dispatch path.
                        try { done.Set(); } catch (Exception) { }
                    }
                });
                if (!done.Wait(timeout))
                {
                    System.Threading.Volatile.Write(ref abandoned, true);
                    throw new TimeoutException("main-thread dispatch timeout after " + timeout.TotalSeconds + "s: " + op);
                }
            }
            finally { done.Dispose(); }
            if (error != null) throw error;
            return result;
        }
    }
}
