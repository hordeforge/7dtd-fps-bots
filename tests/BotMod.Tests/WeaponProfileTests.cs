// WeaponProfileTests: pins per-gun profile selection, the one place the
// "mixed" literal is expanded into a real gun id. BotArgParser.LooksLikeWeapon,
// `bot weapon` and the web spawnNear field all match that literal
// case-insensitively, so a differently cased spelling has to expand to a
// LoadoutPool entry here too: falling through to the pistol default left
// GunId pointing at a gun that does not exist, so the bot held nothing while
// running pistol stats. Pins the passthrough and the empty-pool fallback too,
// so a resolution change cannot silently swap a caller error for a pool pick.
// Needs the same Newtonsoft gate as the other config-layer suites
// (WeaponProfile references BotConfig.DefaultSeed); run with
//   bash scripts/test-idempotency.sh weaponprofile
using System;
using BotMod.Config;

static class WeaponProfileTests
{
    static int _failures;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        if (!ok) _failures++;
    }

    static readonly string[] Pool = { "gunHandgunT1Pistol", "gunMGT1AK47" };

    static bool InPool(string gunId)
    {
        foreach (string g in Pool) if (g == gunId) return true;
        return false;
    }

    public static int Main()
    {
        // The literal, in every casing the argument surfaces accept.
        foreach (string spelling in new[] { "mixed", "Mixed", "MIXED", "mIxEd" })
        {
            var wp = WeaponProfile.ForGun(spelling, Pool);
            Check(InPool(wp.GunId), "ForGun(\"" + spelling + "\") picks a LoadoutPool gun (got " + wp.GunId + ")");
        }
        // An empty or null id is the same request as the literal: it expands
        // through the pool.
        Check(InPool(WeaponProfile.ForGun("", Pool).GunId), "ForGun(\"\") picks a LoadoutPool gun");
        Check(InPool(WeaponProfile.ForGun(null, Pool).GunId), "ForGun(null) picks a LoadoutPool gun");

        // A named gun is returned verbatim, casing included: the item lookup
        // is case-sensitive, so this is a pass-through, not a resolution.
        var ak = WeaponProfile.ForGun("gunMGT1AK47", Pool);
        Check(ak.GunId == "gunMGT1AK47" && ak.Damage == 16 && ak.Range == 55f,
            "ForGun(\"gunMGT1AK47\") keeps the id and the AK profile");
        // An unknown id is a caller error (BotArgParser rejects it first), but
        // it must still come back as itself rather than as a pool entry.
        Check(WeaponProfile.ForGun("gunBogus", Pool).GunId == "gunBogus",
            "ForGun(\"gunBogus\") passes the id through");

        // An empty pool falls back to a real gun instead of throwing on an
        // out-of-range index.
        Check(WeaponProfile.ForGun("mixed", new string[0]).GunId == WeaponProfile.DefaultGun,
            "ForGun(\"mixed\") with an empty pool falls back to the AK");

        Console.WriteLine(_failures == 0
            ? "all weapon profile tests passed"
            : _failures + " weapon profile test(s) failed");
        return _failures == 0 ? 0 : 1;
    }
}
