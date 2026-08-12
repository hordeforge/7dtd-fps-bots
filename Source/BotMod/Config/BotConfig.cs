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

        // How many bots to keep alive. Bots respawn after BotRespawnDelaySec when killed.
        public int TargetBotCount { get; set; } = 4;
        public int MaxBots { get; set; } = 16;

        // Entity class used for bots. Must be a valid entityclasses.xml entry.
        // npcSurvivorRanged exists in V3.1 (Human Ranged AI) and spawns with a pistol.
        public string BotEntityClass { get; set; } = "zombieSoldier";

        // Weapon given on spawn (overrides whatever the entity class spawns with if set).
        // Empty = keep whatever the class brings. Example: gunHandgunT1Pistol, gunMGT1AK47, gunShotgunT1DoubleBarrel
        public string BotWeapon { get; set; } = "gunMGT1AK47";
        public string BotAmmo { get; set; } = "ammo762mmBulletBall";
        public int BotAmmoCount { get; set; } = 300;

        // Health/movement tuning
        public float BotHealth { get; set; } = 150f;
        public float BotMoveSpeed { get; set; } = 0.9f;
        public float BotMoveSpeedAggro { get; set; } = 1.2f;

        // Names used for bots (randomly picked).
        public string[] BotNames { get; set; } = new[] { "Bot_Alpha", "Bot_Bravo", "Bot_Charlie", "Bot_Delta", "Bot_Echo", "Bot_Foxtrot", "Bot_Golf", "Bot_Hotel" };

        // AI
        public float VisionRange { get; set; } = 50f;
        public float VisionAngle { get; set; } = 160f;
        public float LoseTargetRange { get; set; } = 65f;
        public float LoseTargetTimeSec { get; set; } = 6f;
        public float AttackRange { get; set; } = 35f;
        public float AimJitterDegrees { get; set; } = 2.5f; // miss cone
        public float FireRateSec { get; set; } = 0.25f;
        public int DamagePerShot { get; set; } = 14;
        public float HeadshotChance { get; set; } = 0.12f;
        public float HeadshotMultiplier { get; set; } = 2.0f;

        // Whether bots can damage each other and zombies/players
        public bool BotVsBot { get; set; } = true;
        public bool BotVsZombie { get; set; } = true;
        public bool BotVsPlayer { get; set; } = true;

        // Pathfinding / movement
        public float PathRecalcIntervalSec { get; set; } = 0.65f;
        public float StuckTimeoutSec { get; set; } = 3.0f;
        public float RandomWanderRadius { get; set; } = 40f;
        public float RandomWanderIntervalSec { get; set; } = 8f;

        // Spawning
        public float SpawnRadius { get; set; } = 30f; // around bot spawn point / near random alive player
        public float SpawnNearPlayerChance { get; set; } = 0.7f; // 0..1, otherwise near world center/spawn
        public float RespawnDelaySec { get; set; } = 8f;
        public float SpawnProtectionSec { get; set; } = 2f; // brief damage immunity after spawn
        public bool AnnounceSpawns { get; set; } = true;

        // Loot: disable bot loot drop to avoid farming
        public bool DropLootOnDeath { get; set; } = false;

        public static BotConfig Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new BotConfig();
            try
            {
                string json = File.ReadAllText(path);
                var loaded = JsonConvert.DeserializeObject<BotConfig>(json);
                if (loaded == null) return new BotConfig();
                loaded.Normalize();
                return loaded;
            }
            catch (Exception ex)
            {
                ModApi.Log("BotConfig load failed, using defaults: " + ex.Message);
                return new BotConfig();
            }
        }

        public void Normalize()
        {
            TargetBotCount = Math.Max(0, Math.Min(64, TargetBotCount));
            MaxBots = Math.Max(TargetBotCount, Math.Min(64, MaxBots));
            BotAmmoCount = Math.Max(0, Math.Min(10000, BotAmmoCount));
            BotHealth = Math.Max(10f, Math.Min(10000f, BotHealth));
            VisionRange = Math.Max(5f, Math.Min(200f, VisionRange));
            LoseTargetRange = Math.Max(VisionRange, Math.Min(300f, LoseTargetRange));
            AttackRange = Math.Max(2f, Math.Min(VisionRange, AttackRange));
            AimJitterDegrees = Math.Max(0f, Math.Min(30f, AimJitterDegrees));
            FireRateSec = Math.Max(0.05f, Math.Min(5f, FireRateSec));
            DamagePerShot = Math.Max(1, Math.Min(500, DamagePerShot));
            HeadshotChance = Math.Max(0f, Math.Min(1f, HeadshotChance));
            PathRecalcIntervalSec = Math.Max(0.1f, Math.Min(5f, PathRecalcIntervalSec));
            StuckTimeoutSec = Math.Max(0.5f, Math.Min(20f, StuckTimeoutSec));
            RespawnDelaySec = Math.Max(0f, Math.Min(600f, RespawnDelaySec));
            SpawnRadius = Math.Max(2f, Math.Min(500f, SpawnRadius));
            SpawnNearPlayerChance = Math.Max(0f, Math.Min(1f, SpawnNearPlayerChance));
            if (BotNames == null || BotNames.Length == 0)
                BotNames = new[] { "Bot" };
        }

        public static string DefaultPathBesideAssembly()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            string a = Path.Combine(dir, "Config", "botmod.json");
            if (File.Exists(a)) return a;
            return Path.Combine(dir, "botmod.json");
        }
    }
}
