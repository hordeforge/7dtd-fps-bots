using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace BotMod.Web
{
    /// <summary>
    /// Bounded replay ledger for POST /api/bot idempotency keys ("requestId").
    ///
    /// World-touching actions (spawn/spawnNear) are not naturally idempotent:
    /// a retried POST (lost response after the server acted, proxy retry,
    /// double event) would execute twice. Clients generate one requestId per
    /// logical request and reuse it across retries; the ledger records the
    /// first response so a duplicate replays it instead of executing again.
    ///
    /// State is capped: at most Capacity keys are remembered and each entry
    /// ages out after Retention, so the ledger cannot grow without bound. The
    /// retention window bounds how long a retry can arrive and still be
    /// deduplicated; entries claimed but never completed (crash between Begin
    /// and Complete/Fail) unblock when they age out. All access is
    /// thread-safe: web handlers run on thread pool threads.
    /// </summary>
    internal static class IdempotencyLedger
    {
        internal enum BeginResult { Fresh, InProgress, Replay }

        internal const int Capacity = 256;
        internal const int MaxKeyLength = 128;

        /// <summary>Replay window. Must exceed the retry horizon callers use.</summary>
        internal static TimeSpan Retention = TimeSpan.FromMinutes(10);

        /// <summary>Monotonic elapsed-time source for retention/pruning
        /// decisions. Retention is a pure duration, so it must not ride the
        /// wall clock: an NTP step or manual change forward by more than the
        /// window would prune every entry at once (a retried POST would then
        /// execute again instead of replaying) and a backward step would
        /// stretch the window arbitrarily. Production reads Stopwatch time;
        /// deterministic tests substitute a virtual clock so replay-window
        /// boundaries are exercised exactly, with no sleeps.</summary>
        internal static Func<TimeSpan> ElapsedNow = DefaultElapsedNow;

        static readonly long StartStamp = Stopwatch.GetTimestamp();

        static TimeSpan DefaultElapsedNow()
        {
            long delta = Stopwatch.GetTimestamp() - StartStamp;
            // When no high-resolution counter exists GetTimestamp counts
            // DateTime ticks, so convert like-for-like per mode.
            return Stopwatch.IsHighResolution
                ? TimeSpan.FromSeconds(delta / (double)Stopwatch.Frequency)
                : TimeSpan.FromTicks(delta);
        }

        sealed class Entry
        {
            public TimeSpan StartedAt;
            public string Body;
            public bool Done;
        }

        static readonly object Gate = new object();
        static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        internal static int Count { get { lock (Gate) { return Entries.Count; } } }

        public static bool IsValidKey(string key)
        {
            return !string.IsNullOrEmpty(key) && key.Length <= MaxKeyLength;
        }

        /// <summary>Claim a key. Fresh: caller executes and must finish with
        /// Complete or Fail. InProgress: another thread holds the claim
        /// (concurrent duplicate). Replay: already executed; cachedBody is the
        /// recorded response to resend verbatim.</summary>
        internal static BeginResult TryBegin(string key, out string cachedBody)
        {
            lock (Gate)
            {
                TimeSpan now = ElapsedNow();
                PruneLocked(now - Retention);
                Entry e;
                if (Entries.TryGetValue(key, out e))
                {
                    cachedBody = e.Body;
                    return e.Done ? BeginResult.Replay : BeginResult.InProgress;
                }
                Entries[key] = new Entry { StartedAt = now };
                cachedBody = null;
                return BeginResult.Fresh;
            }
        }

        /// <summary>Record the successful response so retries with the same key replay it.</summary>
        internal static void Complete(string key, string body)
        {
            lock (Gate)
            {
                Entry e;
                if (!Entries.TryGetValue(key, out e)) return;
                e.Done = true;
                e.Body = body ?? "";
                e.StartedAt = ElapsedNow(); // replay window counts from completion
            }
        }

        /// <summary>Release a claim after failed or rejected execution so a retry with the same key can run.</summary>
        internal static void Fail(string key)
        {
            lock (Gate) { Entries.Remove(key); }
        }

        static void PruneLocked(TimeSpan cutoff)
        {
            List<string> dead = null;
            foreach (var kv in Entries)
            {
                if (kv.Value.StartedAt < cutoff)
                {
                    if (dead == null) dead = new List<string>();
                    dead.Add(kv.Key);
                }
            }
            if (dead != null) for (int i = 0; i < dead.Count; i++) Entries.Remove(dead[i]);
            // Hard cap independent of age: drop oldest until one slot is free.
            while (Entries.Count >= Capacity)
            {
                string oldestKey = null;
                TimeSpan oldest = TimeSpan.MaxValue;
                foreach (var kv in Entries)
                {
                    if (kv.Value.StartedAt < oldest) { oldest = kv.Value.StartedAt; oldestKey = kv.Key; }
                }
                if (oldestKey == null) break;
                Entries.Remove(oldestKey);
            }
        }
    }
}
