// IdempotencyLedgerFuzzTests — randomized differential fuzzing of the POST
// /api/bot replay ledger against an independent spec model. The ledger takes
// untrusted "requestId" strings straight from HTTP POST bodies; this harness
// drives TryBegin/Complete/Fail with adversarial keys, a jittering virtual
// clock and capacity/retention pressure, then asserts after every operation
// that the implementation matches the model (result kind, cached body, entry
// count) plus the global bounded-state invariant. Deterministic: fixed seed
// list, virtual clock only, no wall time. Pure BCL; compiled and run by
// scripts/test-idempotency.sh (needs mcs + mono, not part of `make check`).
//
//   bash scripts/test-idempotency.sh
using System;
using System.Collections.Generic;
using BotMod.Web;

static class IdempotencyLedgerFuzzTests
{
    const int Seeds = 64;
    const int OpsPerSeed = 160;

    static int _failures;
    static int _ops;

    // Virtual clock shared with the ledger via IdempotencyLedger.UtcNow; the
    // driver moves it between operations (forward, sometimes backward, to
    // exercise retention expiry, completion-refresh and clock-skew paths).
    static DateTime _now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    static void Check(bool ok, string detail)
    {
        if (!ok)
        {
            _failures++;
            Console.WriteLine("FAIL " + detail);
        }
    }

    // ---- spec model: an exact, independent restatement of the ledger ----

    sealed class EntryModel { public DateTime StartedUtc; public bool Done; public string Body; }

    sealed class LedgerModel
    {
        public readonly Dictionary<string, EntryModel> Entries =
            new Dictionary<string, EntryModel>(StringComparer.Ordinal);

        public void Prune(DateTime cutoff, int capacity)
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
            while (Entries.Count >= capacity)
            {
                string oldestKey = null;
                DateTime oldest = DateTime.MaxValue;
                foreach (var kv in Entries)
                    if (kv.Value.StartedUtc < oldest) { oldest = kv.Value.StartedUtc; oldestKey = kv.Key; }
                if (oldestKey == null) break;
                Entries.Remove(oldestKey);
            }
        }

        public IdempotencyLedger.BeginResult TryBegin(
            string key, DateTime now, TimeSpan retention, int capacity, out string body)
        {
            body = null;
            Prune(now - retention, capacity);
            EntryModel e;
            if (Entries.TryGetValue(key, out e))
            {
                body = e.Body;
                return e.Done ? IdempotencyLedger.BeginResult.Replay : IdempotencyLedger.BeginResult.InProgress;
            }
            Entries[key] = new EntryModel { StartedUtc = now };
            return IdempotencyLedger.BeginResult.Fresh;
        }

        public void Complete(string key, string body, DateTime now)
        {
            EntryModel e;
            if (!Entries.TryGetValue(key, out e)) return;
            e.Done = true;
            e.Body = body ?? "";
            e.StartedUtc = now;
        }

        public void Fail(string key) { Entries.Remove(key); }
    }

    // ---- adversarial input generation ----

    static string[] KeyPool()
    {
        return new[]
        {
            "replay-1",
            "",                                             // invalid: empty
            new string('k', IdempotencyLedger.MaxKeyLength),        // boundary: exactly max
            new string('k', IdempotencyLedger.MaxKeyLength + 1),    // invalid: one past max
            "ключ-\u00e9\u4e2d\u6587",                      // unicode
            "\u0000ctl\u0001\u001f",                        // control characters
            "\uD83D\uDE00-astral",                          // surrogate pair
            "Alpha", "alpha",                               // distinct under Ordinal
            " a ", "\t",                                    // whitespace variants
            "cap-1", "cap-10",                              // prefix collisions
            "{\"action\":\"spawn\"}",                       // JSON-looking junk
            "../../etc/passwd"                              // path-shaped junk
        };
    }

    static string RandomKey(Random rng)
    {
        const string alphabet = "abz09-_.{}/\\\"\u00e9 ";
        int len = rng.Next(0, 40);
        char[] c = new char[len];
        for (int i = 0; i < len; i++)
            c[i] = rng.Next(12) == 0 ? '\u0000' : alphabet[rng.Next(alphabet.Length)];
        return new string(c);
    }

    static string PickKey(Random rng, string[] pool)
    {
        return rng.Next(3) == 0 ? RandomKey(rng) : pool[rng.Next(pool.Length)];
    }

    /// <summary>Move the virtual clock: mostly small forward steps, sometimes
    /// minutes ahead (expiry) or backward (host clock skew). Backward steps
    /// give fresh entries older stamps than survivors, which flips capacity
    /// eviction order; the model mirrors every transition, so any divergence
    /// between the two is a real ledger bug.</summary>
    static DateTime NextInstant(Random rng)
    {
        double roll = rng.NextDouble();
        TimeSpan step;
        if (roll < 0.70) step = TimeSpan.FromMilliseconds(rng.Next(0, 500));
        else if (roll < 0.85) step = TimeSpan.FromSeconds(rng.Next(1, 60));
        else if (roll < 0.95) step = TimeSpan.FromMinutes(rng.Next(1, 30));       // expire things
        else step = -TimeSpan.FromSeconds(rng.Next(1, 600));                      // skew backward
        return _now + step;
    }

    static void CheckCounts(LedgerModel model, int seed, int op, string what)
    {
        Check(IdempotencyLedger.Count == model.Entries.Count,
            "seed=" + seed + " op=" + op + " " + what + ": count " + IdempotencyLedger.Count + " != model " + model.Entries.Count);
        Check(IdempotencyLedger.Count <= IdempotencyLedger.Capacity,
            "seed=" + seed + " op=" + op + " " + what + ": capacity exceeded: " + IdempotencyLedger.Count);
    }

    static string Show(string s)
    {
        if (s == null) return "<null>";
        string vis = s.Replace("\u0000", "\\0").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        return "\"" + vis + "\"(len=" + s.Length + ")";
    }

    static int Main()
    {
        string[] keys = KeyPool();
        string[] bodies = { null, "", "{}", "{\"spawned\":3}" };

        for (int seed = 1; seed <= Seeds; seed++)
        {
            var rng = new Random(seed * 7919);
            TimeSpan retention = new[]
            {
                TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)
            }[rng.Next(3)];
            IdempotencyLedger.Retention = retention;
            IdempotencyLedger.UtcNow = delegate { return _now; };

            // Drain the previous seed: jump past its retention window; the
            // mandatory leading Begin then prunes everything stale on both
            // sides, so each seed starts from a matched near-empty state.
            _now += TimeSpan.FromDays(2);
            var model = new LedgerModel();

            for (int op = 0; op < OpsPerSeed; op++)
            {
                _now = NextInstant(rng);
                string key = PickKey(rng, keys);
                _ops++;

                // IsValidKey is the web layer's gate: its verdict must follow
                // the documented rule for every input shape generated here.
                Check(IdempotencyLedger.IsValidKey(key) == (!string.IsNullOrEmpty(key) && key.Length <= IdempotencyLedger.MaxKeyLength),
                    "seed=" + seed + " op=" + op + " IsValidKey disagrees with rule for " + Show(key));

                // Op 0 must be a Begin: only TryBegin prunes, and pruning is
                // what drains the previous seed's stale entries on the real
                // side (the model starts empty).
                double roll = op == 0 ? 0.0 : rng.NextDouble();
                if (roll < 0.50)
                {
                    string realBody, modelBody;
                    var real = IdempotencyLedger.TryBegin(key, out realBody);
                    var expected = model.TryBegin(key, _now, retention, IdempotencyLedger.Capacity, out modelBody);
                    Check(real == expected && string.Equals(realBody, modelBody, StringComparison.Ordinal),
                        "seed=" + seed + " op=" + op + " begin " + Show(key)
                        + ": expected " + expected + "/" + Show(modelBody)
                        + " got " + real + "/" + Show(realBody));
                }
                else if (roll < 0.75)
                {
                    string body = bodies[rng.Next(bodies.Length)];
                    IdempotencyLedger.Complete(key, body);
                    model.Complete(key, body, _now);
                }
                else
                {
                    IdempotencyLedger.Fail(key);
                    model.Fail(key);
                }
                CheckCounts(model, seed, op, "key=" + Show(key));
            }
        }

        IdempotencyLedger.Retention = TimeSpan.FromMinutes(10);
        Console.WriteLine("ledger fuzz: " + _ops + " operations across " + Seeds + " seeds");
        Console.WriteLine(_failures == 0 ? "all idempotency ledger fuzz checks passed" : _failures + " fuzz check(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
