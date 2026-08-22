using System;
using System.Collections.Generic;

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

        /// <summary>Clock for retention/pruning decisions. Production reads wall
        /// time; deterministic tests substitute a virtual clock so replay-window
        /// boundaries are exercised exactly, with no sleeps.</summary>
        internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

        sealed class Entry
        {
            public DateTime StartedUtc;
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
                PruneLocked(UtcNow() - Retention);
                Entry e;
                if (Entries.TryGetValue(key, out e))
                {
                    cachedBody = e.Body;
                    return e.Done ? BeginResult.Replay : BeginResult.InProgress;
                }
                Entries[key] = new Entry { StartedUtc = UtcNow() };
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
                e.StartedUtc = UtcNow(); // replay window counts from completion
            }
        }

        /// <summary>Release a claim after failed or rejected execution so a retry with the same key can run.</summary>
        internal static void Fail(string key)
        {
            lock (Gate) { Entries.Remove(key); }
        }

        static void PruneLocked(DateTime cutoff)
        {
            List<string> dead = null;
            foreach (var kv in Entries)
            {
                if (kv.Value.StartedUtc < cutoff)
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
                DateTime oldest = DateTime.MaxValue;
                foreach (var kv in Entries)
                {
                    if (kv.Value.StartedUtc < oldest) { oldest = kv.Value.StartedUtc; oldestKey = kv.Key; }
                }
                if (oldestKey == null) break;
                Entries.Remove(oldestKey);
            }
        }
    }
}
