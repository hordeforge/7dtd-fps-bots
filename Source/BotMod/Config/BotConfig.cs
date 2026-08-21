using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace BotMod.Config
{
    public sealed class BotConfig
    {
        public bool Enabled { get; set; } = true;
        public bool DedicatedOnly { get; set; } = true;
        public int TargetBotCount { get; set; } = 6;
        public int MaxBots { get; set; } = 16;
        // Bot body. Use the vanilla npcTraderJoel human (renders a player model and
        // proves a positive EntityClass id on dedi); our FPS loop drives its combat.
        public string BotEntityClass { get; set; } = "npcTraderJoel";
        public string BotWeapon { get; set; } = "mixed"; // mixed=random per bot from LoadoutPool, or a single gun id
        public string BotAmmo { get; set; } = "ammo762mmBulletBall";
        public int BotAmmoCount { get; set; } = 300;
        public float BotHealth { get; set; } = 100f; // Q3-like 100 (armor handled via config if desired)
        public string[] BotNames { get; set; } = new[] { "Grunt", "Ranger", "Phobos", "Dozer", "Klesk", "Sorlag", "TankJr", "Hunter", "Wrack", "Visor", "Bones", "Slash" };
        public string[] LoadoutPool { get; set; } = new[] { "gunHandgunT1Pistol", "gunShotgunT1DoubleBarrel", "gunMGT1AK47", "gunRifleT3SniperRifle", "gunShotgunT3AutoShotgun", "gunHandgunT3SMG5" };
        // 0 bot, 1 easy, 2 normal, 3 hard, 4 nightmare - like Q3 bot cvars
        public int Difficulty { get; set; } = 2;
        public float VisionRange { get; set; } = 70f;
        public float VisionAngle { get; set; } = 190f; // FPS bots look around, not narrow cone
        public float LoseTargetRange { get; set; } = 85f;
        public float LoseTargetTimeSec { get; set; } = 4.5f;
        public float AttackRange { get; set; } = 45f;
        // Per-weapon spread is in WeaponProfile; this is base fallback
        public float AimJitterDegrees { get; set; } = 2.0f;
        public float FireRateSec { get; set; } = 0.18f;
        public int DamagePerShot { get; set; } = 16;
        public float HeadshotChance { get; set; } = 0.08f;
        public float HeadshotMultiplier { get; set; } = 2.0f;
        // Burst fire - FPS feel. 0 = auto
        public int BurstMin { get; set; } = 2;
        public int BurstMax { get; set; } = 4;
        public float BurstPauseSec { get; set; } = 0.65f;
        public float ReactionTimeSec { get; set; } = 0.28f; // see -> shoot delay
        public bool BotVsBot { get; set; } = true;
        public bool BotVsZombie { get; set; } = true;
        public bool BotVsPlayer { get; set; } = true;
        public float PathRecalcIntervalSec { get; set; } = 0.45f;
        public float StuckTimeoutSec { get; set; } = 2.0f;
        public float RandomWanderRadius { get; set; } = 60f;
        public float RandomWanderIntervalSec { get; set; } = 5f;
        public float SpawnRadius { get; set; } = 25f;
        public float SpawnNearPlayerChance { get; set; } = 0.35f; // DM wants far spawns, not stacking on players
        public bool UseSpawnpoints { get; set; } = true; // read world's spawnpoints.xml
        public float RespawnDelaySec { get; set; } = 3f; // Q3-like 3s
        public float SpawnProtectionSec { get; set; } = 1.2f;
        public bool AnnounceSpawns { get; set; } = true;
        public bool BotAnnounceKillsInChat { get; set; } = true; // broadcast bot frags to player chat
        public bool DropLootOnDeath { get; set; } = false;
        // Strafe / dodge
        public float StrafeChance { get; set; } = 0.9f;
        public float DodgeOnHitChance { get; set; } = 0.75f;
        // Neural brain (docs/research/00..06) — advisory only, heuristic fallback
        public bool UseNeuralBrain { get; set; } = false;
        public string BotNeuralWeightPath { get; set; } = "evolved/best.json";

        public WeaponProfile ResolveWeapon(string gunId)
        {
            return WeaponProfile.ForGun(gunId ?? BotWeapon, this);
        }

        public static BotConfig Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new BotConfig();
            try
            {
                var loaded = JsonConvert.DeserializeObject<BotConfig>(File.ReadAllText(path));
                if (loaded == null) return new BotConfig();
                loaded.Normalize(); return loaded;
            }
            catch (Exception ex) { ModApi.Log("BotConfig load failed: " + ex.Message); return new BotConfig(); }
        }
        public void Normalize()
        {
            TargetBotCount = Math.Max(0, Math.Min(64, TargetBotCount));
            MaxBots = Math.Max(TargetBotCount, Math.Min(64, MaxBots));
            BotAmmoCount = Math.Max(0, Math.Min(10000, BotAmmoCount));
            BotHealth = Math.Max(10f, Math.Min(10000f, BotHealth));
            Difficulty = Math.Max(0, Math.Min(4, Difficulty));
            VisionRange = Math.Max(8f, Math.Min(300f, VisionRange));
            LoseTargetRange = Math.Max(VisionRange, Math.Min(400f, LoseTargetRange));
            AttackRange = Math.Max(3f, Math.Min(VisionRange, AttackRange));
            AimJitterDegrees = Math.Max(0f, Math.Min(30f, AimJitterDegrees));
            FireRateSec = Math.Max(0.04f, Math.Min(2f, FireRateSec));
            DamagePerShot = Math.Max(1, Math.Min(500, DamagePerShot));
            HeadshotChance = Math.Max(0f, Math.Min(1f, HeadshotChance));
            BurstMin = Math.Max(1, Math.Min(20, BurstMin));
            BurstMax = Math.Max(BurstMin, Math.Min(30, BurstMax));
            BurstPauseSec = Math.Max(0.1f, Math.Min(3f, BurstPauseSec));
            ReactionTimeSec = Math.Max(0f, Math.Min(1.5f, ReactionTimeSec));
            PathRecalcIntervalSec = Math.Max(0.08f, Math.Min(5f, PathRecalcIntervalSec));
            StuckTimeoutSec = Math.Max(0.5f, Math.Min(20f, StuckTimeoutSec));
            RespawnDelaySec = Math.Max(0f, Math.Min(600f, RespawnDelaySec));
            SpawnRadius = Math.Max(2f, Math.Min(500f, SpawnRadius));
            SpawnNearPlayerChance = Math.Max(0f, Math.Min(1f, SpawnNearPlayerChance));
            StrafeChance = Math.Max(0f, Math.Min(1f, StrafeChance));
            DodgeOnHitChance = Math.Max(0f, Math.Min(1f, DodgeOnHitChance));
            if (BotNames == null || BotNames.Length == 0) BotNames = new[] { "Bot" };
            if (LoadoutPool == null || LoadoutPool.Length == 0) LoadoutPool = new[] { "gunMGT1AK47" };
            // Apply difficulty preset over tunables that weren't hand-tweaked far from defaults
            ApplyDifficulty();
        }
        void ApplyDifficulty()
        {
            // Higher diff = tighter aim, faster reaction, tighter bursts, wider engagement
            float aimScale = 1f - Difficulty * 0.18f; // 1.0,0.82,0.64,0.46,0.28
            float react = 0.42f - Difficulty * 0.09f; // 0.42,0.33,0.24,0.15,0.06
            // Only nudge if user left defaults near stock
            if (Math.Abs(ReactionTimeSec - 0.28f) < 0.08f) ReactionTimeSec = Math.Max(0.05f, react);
            if (Math.Abs(AimJitterDegrees - 2.0f) < 1.5f) AimJitterDegrees = Math.Max(0.3f, 2.8f * aimScale);
            if (Difficulty >= 3 && VisionRange < 80f) VisionRange = 80f + Difficulty * 10f;
            if (Difficulty >= 3 && AttackRange < 50f) AttackRange = 50f;
            if (Difficulty <= 1) { HeadshotChance = Math.Min(HeadshotChance, 0.04f); }
            else if (Difficulty >= 3) HeadshotChance = Math.Max(HeadshotChance, 0.1f + Difficulty * 0.02f);
        }
        public static string DefaultPathBesideAssembly()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            string a = Path.Combine(dir, "Config", "botmod.json");
            if (File.Exists(a)) return a;
            return Path.Combine(dir, "botmod.json");
        }
    }

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
        public float ProjectileSpeed; // hitscan=0
        public int MagSize; // rounds per magazine (zdtd_bot ammo pacing parity)
        public float ReloadSec; // reload pause on empty (zdtd_bot parity)
        static uint _pickCtr = 0x5A17B243u;
        public static WeaponProfile ForGun(string gunId, BotConfig cfg)
        {
            if (string.IsNullOrEmpty(gunId) || gunId == "mixed")
            {
                if (cfg.LoadoutPool != null && cfg.LoadoutPool.Length > 0)
                {
                    // Deterministic per-call LCG counter (zdtd parity: no wall-clock noise)
                    // so mixed spawns in the same tick still pick distinct entries.
                    _pickCtr = _pickCtr * 1103515245u + 12345u;
                    int idx = (int)((_pickCtr >> 8 & 0x00ffffffu) % (uint)cfg.LoadoutPool.Length);
                    gunId = cfg.LoadoutPool[idx];
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
