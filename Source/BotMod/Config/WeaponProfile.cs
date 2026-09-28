using System;
using BotMod.Foundation;

namespace BotMod.Config
{
    /// <summary>
    /// Per-gun combat profile (fire rate, burst shape, spread, damage,
    /// effective range, magazine pacing). Pure data table: classified from the
    /// gun id in ForGun with no engine types, so it compiles headless alongside
    /// BotConfig (scripts/test-idempotency.sh) and is mirrored by tools/ga.
    /// </summary>
    public struct WeaponProfile
    {
        public string GunId;
        public float FireRate; // sec per shot inside burst
        public int BurstMin, BurstMax;
        public float BurstPause;
        public int Damage;
        public float Range; // effective
        public int Pellets; // shotgun
        public int MagSize; // rounds per magazine (zdtd_bot ammo pacing parity)
        public float ReloadSec; // reload pause on empty (zdtd_bot parity)
        // Salt keeps the mixed-loadout counter off the spawner's own stream, so
        // one more gun pick cannot shift the name/spot picks that follow it.
        const uint LoadoutCounterSalt = 0x5A17B243u;
        static Lcg _pickCtr = Lcg.Seeded((uint)BotConfig.DefaultSeed ^ LoadoutCounterSalt);
        /// <summary>Reseed the mixed-loadout counter. The spawner owns the
        /// single entry point for this (BotSpawner.Reseed), so the gun picks
        /// and the name/spot picks always restart from the same world seed.</summary>
        public static void ReseedPickCounter(uint seed) { _pickCtr = Lcg.Seeded(seed ^ LoadoutCounterSalt); }
        /// <summary>Literal that selects a random LoadoutPool entry. Matched
        /// case-insensitively: every surface that accepts it (BotArgParser
        /// .LooksLikeWeapon, `bot weapon`, the web spawnNear weapon field) is
        /// case-insensitive, so a spelled "Mixed" used to fall through to the
        /// pistol default with GunId="Mixed" - an item that does not exist, so
        /// the bot held no gun while running pistol stats.</summary>
        public const string Mixed = "mixed";
        /// <summary>Gun used when the pool is empty; also BotConfig.Normalize's
        /// documented LoadoutPool fallback.</summary>
        public const string DefaultGun = "gunMGT1AK47";
        public static WeaponProfile ForGun(string gunId, BotConfig cfg)
        {
            return ForGun(gunId, cfg != null ? cfg.LoadoutPool : null);
        }
        /// <summary>Profile for one gun id, with the "mixed" literal expanded
        /// against <paramref name="loadoutPool"/>.</summary>
        public static WeaponProfile ForGun(string gunId, string[] loadoutPool)
        {
            if (string.IsNullOrEmpty(gunId) || gunId.Equals(Mixed, StringComparison.OrdinalIgnoreCase))
            {
                if (loadoutPool != null && loadoutPool.Length > 0)
                {
                    // Deterministic per-call LCG counter (zdtd parity: no wall-clock noise)
                    // so mixed spawns in the same tick still pick distinct entries.
                    gunId = loadoutPool[_pickCtr.Index(loadoutPool.Length)];
                }
                else gunId = DefaultGun;
            }
            string g = gunId.ToLowerInvariant();
            if (g.Contains("shotgun"))
            {
                bool autoShot = g.Contains("auto");
                return new WeaponProfile { GunId = gunId, FireRate = autoShot ? 0.22f : 0.55f, BurstMin = 1, BurstMax = 1, BurstPause = autoShot ? 0.4f : 0.85f, Damage = autoShot ? 9 : 14, Range = 22f, Pellets = autoShot ? 6 : 8, MagSize = autoShot ? 16 : 2, ReloadSec = 2.6f };
            }
            if (g.Contains("sniper") || g.Contains("hunting") || g.Contains("lever"))
                return new WeaponProfile { GunId = gunId, FireRate = 0.9f, BurstMin = 1, BurstMax = 1, BurstPause = 0.9f, Damage = 42, Range = 90f, Pellets = 1, MagSize = 12, ReloadSec = 2.5f };
            if (g.Contains("smg") || (g.Contains("pipe") && g.Contains("machine")))
                return new WeaponProfile { GunId = gunId, FireRate = 0.09f, BurstMin = 5, BurstMax = 9, BurstPause = 0.5f, Damage = 9, Range = 35f, Pellets = 1, MagSize = 30, ReloadSec = 1.8f };
            // Parenthesized above, "pipe" alone is a rifle (gunPipeRifle), not
            // a machine gun, and must not fall through to the pistol default.
            if (g.Contains("m60") || g.Contains("tactical") || g.Contains("ak") || g.Contains("pipe"))
                return new WeaponProfile { GunId = gunId, FireRate = 0.11f, BurstMin = 3, BurstMax = 6, BurstPause = 0.55f, Damage = 16, Range = 55f, Pellets = 1, MagSize = 30, ReloadSec = 2.0f };
            if (g.Contains("magnum") || g.Contains("desert"))
                return new WeaponProfile { GunId = gunId, FireRate = 0.32f, BurstMin = 1, BurstMax = 2, BurstPause = 0.6f, Damage = 34, Range = 45f, Pellets = 1, MagSize = 6, ReloadSec = 2.2f };
            // pistol default
            return new WeaponProfile { GunId = gunId, FireRate = 0.28f, BurstMin = 1, BurstMax = 3, BurstPause = 0.6f, Damage = 16, Range = 40f, Pellets = 1, MagSize = 15, ReloadSec = 1.2f };
        }
    }
}
