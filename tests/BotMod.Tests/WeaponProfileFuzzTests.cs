// WeaponProfileFuzzTests: randomized fuzzing of the gun-id classifier that
// produces every bot's combat stats. The id arrives untrusted: the web
// spawnNear "weapon" field, `bot player <name> [weapon]` and `bot weapon` all
// hand a client-supplied string to ForGun, and the answer decides fire rate,
// burst shape, damage, range, magazine pacing and reload pause for the bots
// this tick spawns. A profile that came back with a zero or non-finite field
// would divide by it or loop on it downstream in Bot.TryShootBurst.
//
// Invariants pinned here, over random ids and random LoadoutPools:
//   - ForGun never throws (null, empty, lone surrogates, 100k-char ids),
//   - the returned GunId is either the caller's id verbatim, a LoadoutPool
//     member, or the documented DefaultGun fallback. Nothing else: a mutated
//     id that came back as a pool entry would make bots hold a gun the client
//     never asked for,
//   - "mixed" in any casing expands only when a pool is present, and the pick
//     is always inside the pool (an out-of-range index would throw),
//   - every stat is finite and positive with 1 <= BurstMin <= BurstMax, so no
//     burst can stall or fire at a negative rate,
//   - the mixed counter is a pure function of the seed: the same seed and the
//     same call sequence produce the same picks (BotSpawner.Reseed owns the
//     reseed and relies on that to keep spawns world-reproducible).
// Complements the fixed-vector pins in WeaponProfileTests. Pure BCL; compiled
// and run by scripts/test-idempotency.sh:
//
//   bash scripts/test-idempotency.sh
using System;
using BotMod.Config;
using BotMod.Foundation;

static class WeaponProfileFuzzTests
{
    const int Iterations = 20000;

    /// <summary>Failures reported before the run stops. A broken classifier
    /// fails on nearly every input, so printing all of them buries the first
    /// (most specific) report under thousands of copies of itself.</summary>
    const int FailBudget = 20;

    static int _failures;
    static int _cases;

    static void Check(bool ok, string detail)
    {
        if (!ok)
        {
            _failures++;
            if (_failures > FailBudget) return;
            Console.WriteLine("FAIL " + detail);
        }
    }

    // ---- input generation ----

    /// <summary>Substrings the classifier branches on, plus the id prefixes
    /// callers actually send, so a random concatenation reaches the shotgun,
    /// sniper, smg, rifle, magnum and pistol arms of the chain.</summary>
    static readonly string[] Fragments =
    {
        "gun", "Gun", "GUN", "MGT1AK47", "ak", "AK", "smg", "SMG", "pipe",
        "Pipe", "machine", "shotgun", "Shotgun", "auto", "sniper", "hunting",
        "lever", "m60", "tactical", "magnum", "desert", "mixed", "Mixed",
        "MIXED", "mIxEd", " ", "-", "0", "9", "z", "\u00e9", "\u4e2d",
        "\ud83d", "\ude00", "\u200b", "\u0000", "\u202e"
    };

    /// <summary>How many of every <see cref="Iterations"/> ids are the
    /// 100k-char shape. One in two hundred keeps the linear-cost check
    /// present without turning the suite into a string benchmark.</summary>
    const int LongIdEvery = 200;

    static string RandomGunId(Random rng)
    {
        // Long id: the classifier lowercases and runs ~8 Contains passes over
        // the string, so a 100k-char id pins the cost as linear rather than
        // quadratic in a spawn tick.
        if (rng.Next(LongIdEvery) == 0) return Repeat("gunMGT1AK47", 9100);
        switch (rng.Next(6))
        {
            case 0: return null;
            case 1: return "";
            case 2: return "mixed";
            case 3: return "MIXED";
            case 4: return Repeat("mixed", 1 + rng.Next(4));
            default: break;
        }
        int parts = 1 + rng.Next(5);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < parts; i++) sb.Append(Fragments[rng.Next(Fragments.Length)]);
        return sb.ToString();
    }

    static string Repeat(string s, int times)
    {
        var sb = new System.Text.StringBuilder(s.Length * times);
        for (int i = 0; i < times; i++) sb.Append(s);
        return sb.ToString();
    }

    static string[] RandomPool(Random rng)
    {
        switch (rng.Next(6))
        {
            case 0: return null;
            case 1: return new string[0];
            case 2: return new[] { "gunHandgunT1Pistol" };
            default: break;
        }
        int n = 1 + rng.Next(4);
        var pool = new string[n];
        for (int i = 0; i < n; i++) pool[i] = RandomGunId(rng) ?? "";
        return pool;
    }

    // ---- independent restatements of the classifier's contract ----

    static bool IsMixedSpelling(string gunId)
    {
        return string.IsNullOrEmpty(gunId)
            || string.Equals(gunId, WeaponProfile.Mixed, StringComparison.OrdinalIgnoreCase);
    }

    static bool InPool(string[] pool, string gunId)
    {
        if (pool == null) return false;
        foreach (string g in pool) if (g == gunId) return true;
        return false;
    }

    static void CheckStats(WeaponProfile p, string ctx)
    {
        Check(!float.IsNaN(p.FireRate) && !float.IsInfinity(p.FireRate) && p.FireRate > 0f,
            ctx + ": FireRate " + p.FireRate);
        Check(!float.IsNaN(p.Range) && !float.IsInfinity(p.Range) && p.Range > 0f,
            ctx + ": Range " + p.Range);
        Check(!float.IsNaN(p.BurstPause) && !float.IsInfinity(p.BurstPause) && p.BurstPause > 0f,
            ctx + ": BurstPause " + p.BurstPause);
        Check(!float.IsNaN(p.ReloadSec) && !float.IsInfinity(p.ReloadSec) && p.ReloadSec > 0f,
            ctx + ": ReloadSec " + p.ReloadSec);
        Check(p.Damage > 0, ctx + ": Damage " + p.Damage);
        Check(p.MagSize > 0, ctx + ": MagSize " + p.MagSize);
        Check(p.Pellets > 0, ctx + ": Pellets " + p.Pellets);
        Check(p.BurstMin >= 1, ctx + ": BurstMin " + p.BurstMin);
        Check(p.BurstMax >= p.BurstMin, ctx + ": BurstMax " + p.BurstMax);
    }

    static void CheckOne(string gunId, string[] pool, string ctx)
    {
        _cases++;
        WeaponProfile p;
        try { p = WeaponProfile.ForGun(gunId, pool); }
        catch (Exception ex)
        {
            Check(false, ctx + ": threw " + ex.GetType().Name);
            return;
        }
        Check(p.GunId != null, ctx + ": null GunId");

        if (IsMixedSpelling(gunId))
        {
            // The literal is the only id that may be rewritten, and only into
            // the pool or the documented fallback.
            bool expanded = pool != null && pool.Length > 0;
            if (expanded)
                Check(InPool(pool, p.GunId), ctx + ": mixed pick " + Show(p.GunId) + " left the pool");
            else
                Check(p.GunId == WeaponProfile.DefaultGun,
                    ctx + ": mixed without a pool answered " + Show(p.GunId));
        }
        else
        {
            // A named gun is a pass-through: the item lookup is
            // case-sensitive, so a rewritten id would be a different item.
            Check(string.Equals(p.GunId, gunId, StringComparison.Ordinal),
                ctx + ": id rewritten to " + Show(p.GunId));
        }
        CheckStats(p, ctx);
    }

    static string Show(string s)
    {
        if (s == null) return "<null>";
        if (s.Length > 40) return "\"" + s.Substring(0, 20) + "...(" + s.Length + ")\"";
        return "\"" + s + "\"";
    }

    static int Main()
    {
        var rng = new Random(20260928);

        // Fixed anchors first: the exact shapes the web and console send.
        var akPool = new[] { "gunHandgunT1Pistol", "gunMGT1AK47" };
        CheckOne(null, akPool, "anchor null id");
        CheckOne("", akPool, "anchor empty id");
        CheckOne("mixed", akPool, "anchor mixed");
        CheckOne("MIXED", akPool, "anchor MIXED");
        CheckOne("mixed", null, "anchor mixed null pool");
        CheckOne("mixed", new string[0], "anchor mixed empty pool");
        CheckOne("gunMGT1AK47", akPool, "anchor ak");
        CheckOne("gunBogus", null, "anchor unknown id");

        for (int i = 0; i < Iterations && _failures <= FailBudget; i++)
        {
            string id = RandomGunId(rng);
            string[] pool = RandomPool(rng);
            CheckOne(id, pool, "#" + i + " id=" + Show(id) + " poolLen=" + (pool == null ? -1 : pool.Length));
        }

        // The mixed counter is a pure function of the seed: BotSpawner.Reseed
        // restarts it per world so the same seed spawns the same loadouts.
        {
            var pool = new[] { "gunA", "gunB", "gunC" };
            string[] first = MixedSequence(pool, 12345u);
            string[] second = MixedSequence(pool, 12345u);
            Check(Join(first) == Join(second), "mixed picks differ for the same seed");
            string[] other = MixedSequence(pool, 999u);
            Check(Join(first) != Join(other), "mixed picks ignore the seed");
            // Every pool entry is reachable: a counter stuck on one index
            // would hand the whole loadout pool a single gun.
            Check(Seen(first, pool), "mixed picks never reach every pool entry");
        }

        Console.WriteLine("weaponprofile fuzz: " + _cases + " inputs");
        Console.WriteLine(_failures == 0
            ? "all weapon profile fuzz checks passed"
            : _failures + " fuzz check(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    static string[] MixedSequence(string[] pool, uint seed)
    {
        WeaponProfile.ReseedPickCounter(seed);
        var picks = new string[32];
        for (int i = 0; i < picks.Length; i++) picks[i] = WeaponProfile.ForGun("mixed", pool).GunId;
        return picks;
    }

    static bool Seen(string[] picks, string[] pool)
    {
        foreach (string g in pool)
        {
            bool found = false;
            foreach (string p in picks) if (p == g) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }

    static string Join(string[] a)
    {
        return string.Join(",", a);
    }
}
