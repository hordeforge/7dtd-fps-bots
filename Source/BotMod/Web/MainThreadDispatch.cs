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
    /// </summary>
    internal static class MainThreadDispatch
    {
        /// <summary>Hand <paramref name="work"/> to the main thread via
        /// <paramref name="enqueue"/> and block for at most
        /// <paramref name="timeout"/>, then surface the work's result or its
        /// exception. Throws TimeoutException when the work does not complete
        /// in time (<paramref name="op"/> names it in the message); the
        /// enqueued work still runs later.</summary>
        public static T Execute<T>(Func<T> work, Action<Action> enqueue, TimeSpan timeout, string op)
        {
            T result = default(T);
            Exception error = null;
            var done = new System.Threading.ManualResetEventSlim(false);
            try
            {
                enqueue(() =>
                {
                    try { result = work(); }
                    catch (Exception ex) { error = ex; }
                    finally
                    {
                        // A timed-out caller has already disposed this event while
                        // this queued task still holds it; Set must not throw into
                        // the main-thread loop on that abandoned-dispatch path.
                        try { done.Set(); } catch (Exception) { }
                    }
                });
                if (!done.Wait(timeout))
                    throw new TimeoutException("main-thread dispatch timeout after " + timeout.TotalSeconds + "s: " + op);
            }
            finally { done.Dispose(); }
            if (error != null) throw error;
            return result;
        }
    }
}
