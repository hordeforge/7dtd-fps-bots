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
        public float SpreadDeg; // per-shot spread
        public int Damage;
        public float Range; // effective
        public int Pellets; // shotgun
        public int MagSize; // rounds per magazine (zdtd_bot ammo pacing parity)
        public float ReloadSec; // reload pause on empty (zdtd_bot parity)
        static Lcg _pickCtr = Lcg.Seeded(0x5A17B243u);
        public static WeaponProfile ForGun(string gunId, BotConfig cfg)
        {
            if (string.IsNullOrEmpty(gunId) || gunId == "mixed")
            {
                if (cfg.LoadoutPool != null && cfg.LoadoutPool.Length > 0)
                {
                    // Deterministic per-call LCG counter (zdtd parity: no wall-clock noise)
                    // so mixed spawns in the same tick still pick distinct entries.
                    gunId = cfg.LoadoutPool[_pickCtr.Index(cfg.LoadoutPool.Length)];
                }
                else gunId = "gunMGT1AK47";
            }
            string g = gunId.ToLowerInvariant();
            if (g.Contains("shotgun"))
            {
                bool autoShot = g.Contains("auto");
                return new WeaponProfile { GunId = gunId, FireRate = autoShot ? 0.22f : 0.55f, BurstMin = 1, BurstMax = 1, BurstPause = autoShot ? 0.4f : 0.85f, SpreadDeg = autoShot ? 6f : 9f, Damage = autoShot ? 9 : 14, Range = 22f, Pellets = autoShot ? 6 : 8, MagSize = autoShot ? 16 : 2, ReloadSec = 2.6f };
            }
            if (g.Contains("sniper") || g.Contains("hunting") || g.Contains("lever"))
                return new WeaponProfile { GunId = gunId, FireRate = 0.9f, BurstMin = 1, BurstMax = 1, BurstPause = 0.9f, SpreadDeg = 0.35f, Damage = 42, Range = 90f, Pellets = 1, MagSize = 12, ReloadSec = 2.5f };
            if (g.Contains("smg") || g.Contains("pipe") && g.Contains("machine"))
                return new WeaponProfile { GunId = gunId, FireRate = 0.09f, BurstMin = 5, BurstMax = 9, BurstPause = 0.5f, SpreadDeg = 2.2f, Damage = 9, Range = 35f, Pellets = 1, MagSize = 30, ReloadSec = 1.8f };
            if (g.Contains("m60") || g.Contains("tactical") || g.Contains("ak"))
                return new WeaponProfile { GunId = gunId, FireRate = 0.11f, BurstMin = 3, BurstMax = 6, BurstPause = 0.55f, SpreadDeg = 1.4f, Damage = 16, Range = 55f, Pellets = 1, MagSize = 30, ReloadSec = 2.0f };
            if (g.Contains("magnum") || g.Contains("desert"))
                return new WeaponProfile { GunId = gunId, FireRate = 0.32f, BurstMin = 1, BurstMax = 2, BurstPause = 0.6f, SpreadDeg = 1.0f, Damage = 34, Range = 45f, Pellets = 1, MagSize = 6, ReloadSec = 2.2f };
            // pistol default
            return new WeaponProfile { GunId = gunId, FireRate = 0.28f, BurstMin = 1, BurstMax = 3, BurstPause = 0.6f, SpreadDeg = 1.6f, Damage = 16, Range = 40f, Pellets = 1, MagSize = 15, ReloadSec = 1.2f };
        }
    }
}
