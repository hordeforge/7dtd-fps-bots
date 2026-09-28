using System;
using System.Collections.Generic;
using System.Diagnostics;
using BotMod.Config;

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
    ///
    /// A key is bound to the request it was first used for (the caller's
    /// fingerprint of the body). Reusing one key for a different request is
    /// <see cref="BeginResult.Mismatched"/>, never a replay: replaying would
    /// answer the new request with the old response and silently skip the
    /// operation the caller actually asked for.
    /// </summary>
    internal static class IdempotencyLedger
    {
        internal enum BeginResult { Fresh, InProgress, Replay, Mismatched }

        internal const int Capacity = 256;
        /// <summary>Key limit in characters, not UTF-16 code units: a key of
        /// 100 emoji is 100 characters and must not be rejected for being 200
        /// code units long (BotText.CharCount does the counting).</summary>
        internal const int MaxKeyChars = 128;

        /// <summary>Replay window. Must exceed the retry horizon callers use.</summary>
        internal static TimeSpan Retention = TimeSpan.FromMinutes(10);

        /// <summary>Host-side sink for hard-cap evictions. When more than
        /// Capacity keys are live at once, the oldest entries are dropped and
        /// a retry reusing such a key executes again instead of replaying:
        /// dedup is silently lost for those keys. Null in headless unit runs;
        /// WebApi wires it to the server log so sustained overflow (runaway
        /// client generating unique keys, or abuse) is visible to operators.
        /// Receives the number of entries evicted in one prune pass. Wired on a
        /// web thread and read on any thread that calls TryBegin, so the field
        /// is volatile: a plain write can leave other threads on a stale (null)
        /// sink and the evictions go unreported.</summary>
        internal static Action<int> CapacityEvicted
        {
            get { return System.Threading.Volatile.Read(ref _capacityEvicted); }
            set { System.Threading.Volatile.Write(ref _capacityEvicted, value); }
        }

        static Action<int> _capacityEvicted;

        /// <summary>Warning sink for a throwing eviction sink. Wired to
        /// ModApi.Warn by ModApi.InitMod; the default keeps the failure visible
        /// in headless runs.</summary>
        internal static Action<string> Warn = msg => Console.WriteLine("[BotMod] WARNING: " + msg);

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
            // GetTimestamp counts in Frequency units whether or not the host
            // has a high-resolution counter, so the division is the one
            // conversion. Reading the low-resolution counter as DateTime ticks
            // would run this clock ~10000x slow (a millisecond read as 100 ns),
            // stretching the replay window until only the capacity cap retires
            // entries.
            return TimeSpan.FromSeconds(delta / (double)Stopwatch.Frequency);
        }

        sealed class Entry
        {
            public TimeSpan StartedAt;
            public string Fingerprint;
            public string Body;
            public bool Done;
        }

        static readonly object Gate = new object();
        static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        internal static int Count { get { lock (Gate) { return Entries.Count; } } }

        public static bool IsValidKey(string key)
        {
            return !string.IsNullOrEmpty(key) && BotText.CharCount(key) <= MaxKeyChars;
        }

        /// <summary>Claim a key for one specific request. Fresh: caller
        /// executes and must finish with Complete or Fail. InProgress: another
        /// thread holds the claim (concurrent duplicate). Replay: this exact
        /// request already executed; cachedBody is the recorded response to
        /// resend verbatim. Mismatched: the key is live for a different
        /// request (<paramref name="fingerprint"/> differs), so it neither
        /// executes nor replays; the caller must issue a new key.</summary>
        internal static BeginResult TryBegin(string key, string fingerprint, out string cachedBody)
        {
            lock (Gate)
            {
                TimeSpan now = ElapsedNow();
                PruneLocked(now - Retention);
                Entry e;
                if (Entries.TryGetValue(key, out e))
                {
                    // Key reuse for a different request. Answering it with the
                    // recorded body would report the earlier operation as this
                    // one's result and drop the requested one entirely.
                    if (!string.Equals(e.Fingerprint, fingerprint ?? "", StringComparison.Ordinal))
                    {
                        cachedBody = null;
                        return BeginResult.Mismatched;
                    }
                    cachedBody = e.Body;
                    return e.Done ? BeginResult.Replay : BeginResult.InProgress;
                }
                Entries[key] = new Entry { StartedAt = now, Fingerprint = fingerprint ?? "" };
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
            int evicted = 0;
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
                evicted++;
            }
            // Surface dedup loss: a retry with an evicted key re-executes. The
            // sink is host-wired (server log); a throwing sink must not take
            // PruneLocked's caller (TryBegin) down with it, so report the
            // failure through Warn, the same contract as the rest of the
            // config/web layers (wired to ModApi.Warn in InitMod).
            if (evicted > 0 && CapacityEvicted != null)
                try { CapacityEvicted(evicted); }
                catch (Exception ex) { Warn("idempotency capacity-eviction sink failed after evicting " + evicted + " entry(ies): " + ex.Message); }
        }
    }
}
