using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BotMod.Config
{
    public sealed class BotConfig
    {
        public bool Enabled { get; set; } = true;
        public bool DedicatedOnly { get; set; } = true;
        // Auth bypass for offline LAN/loadgen clients with synthetic Steam ids
        // (76561199000000000..10000). Off by default: on leaves any server running
        // this mod open to forged ids in that range.
        public bool AllowSyntheticAuthBypass { get; set; } = false;
        public int TargetBotCount { get; set; } = 6;
        public int MaxBots { get; set; } = 16;
        // Bot body. "mixed" picks zombieSoldier variants: mod-spawned trader bodies
        // (npcTraderJoel) render nothing on this dedi and survivor classes come back
        // negative, so soldiers are the working visible FPS bodies (our loop drives
        // their combat, not the zombie AI).
        public string BotEntityClass { get; set; } = "mixed";
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
        // Squad mode: all bots are one team and never target or damage each other,
        // regardless of BotVsBot. Players and zombies are still fair game.
        public bool BotTeam { get; set; } = false;
        // Team deathmatch: bots with the same nonzero team are allies (keyed by
        // base bot name so assignments survive respawn). 0 = free-for-all (no teams).
        public int BotTeamCount { get; set; } = 2;
        public Dictionary<string, int> TeamAssignments { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // TeamAssignments is written by admin surfaces that run OFF the main
        // thread (web API handlers execute on thread pool threads) while the
        // game tick reads it on every damage event. Dictionary is not safe for
        // concurrent read+write, so every mutation and lookup goes through the
        // locked helpers below; never touch the property directly at runtime.
        internal readonly object TeamGate = new object();

        /// <summary>Set (team > 0) or clear (team <= 0) an assignment keyed by
        /// base bot name. Accepts a bare base name or a full spawned name
        /// ("[Bot] Kíra_42"): the key derives through BotText.BaseName, the
        /// same split live-bot lookups use (Bot.TeamKey), so every surface
        /// (web JSON, console, a pasted scoreboard name) lands on one stored
        /// NFC form instead of a near-miss key that silently never matches.</summary>
        public void SetTeamAssignment(string baseName, int team)
        {
            if (string.IsNullOrEmpty(baseName)) return;
            string key = BotText.BaseName(baseName);
            if (key.Length == 0) return;
            lock (TeamGate)
            {
                if (team <= 0) TeamAssignments.Remove(key);
                else TeamAssignments[key] = team;
            }
        }

        public void ClearTeamAssignments()
        {
            lock (TeamGate) TeamAssignments = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Locked lookup for hot paths (per-damage-event ally checks).
        /// IdentityKey on the lookup side mirrors SetTeamAssignment, so an NFD
        /// or invisible-noise caller spelling cannot miss an entry stored under
        /// its canonical form.</summary>
        public int GetTeamAssignment(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) return 0;
            lock (TeamGate)
            {
                int t;
                return TeamAssignments.TryGetValue(BotText.IdentityKey(baseName), out t) ? Math.Max(0, t) : 0;
            }
        }

        /// <summary>Hot-path variant of <see cref="GetTeamAssignment"/> for keys
        /// already in canonical form (output of BotText.BaseName, e.g.
        /// Bot.TeamKey frozen at spawn): skips the per-call NFC check and
        /// invisible-character rescan the general lookup applies to arbitrary
        /// admin text. AreAllies runs on every DamageEntity event and every
        /// FindTarget candidate, so that rescan (plus its potential Normalize
        /// allocation) ran twice per ally comparison; a substring of a
        /// canonical key stays canonical, so the OrdinalIgnoreCase dictionary
        /// lookup alone returns the identical result here.</summary>
        public int GetTeamAssignmentCanonical(string canonicalKey)
        {
            if (string.IsNullOrEmpty(canonicalKey)) return 0;
            lock (TeamGate)
            {
                int t;
                return TeamAssignments.TryGetValue(canonicalKey, out t) ? Math.Max(0, t) : 0;
            }
        }

        /// <summary>Copy for off-main persistence/enumeration so no thread ever
        /// iterates the live dictionary while another mutates it.</summary>
        public Dictionary<string, int> SnapshotTeamAssignments()
        {
            lock (TeamGate) return new Dictionary<string, int>(TeamAssignments, TeamAssignments.Comparer);
        }
        public float PathRecalcIntervalSec { get; set; } = 0.45f;
        public float StuckTimeoutSec { get; set; } = 2.0f;
        public float RandomWanderRadius { get; set; } = 60f;
        public float RandomWanderIntervalSec { get; set; } = 5f;
        public float SpawnRadius { get; set; } = 25f;
        public float SpawnNearPlayerChance { get; set; } = 0.35f; // DM wants far spawns, not stacking on players
        public bool UseSpawnpoints { get; set; } = true; // read world's spawnpoints.xml
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

        /// <summary>Apply a "vs" toggle from an admin surface (web `vs` action,
        /// `bot vs` console command). Accepts singular/plural class names plus
        /// the "human" alias. Returns false for an unknown target; otherwise
        /// sets the flag and names the JSON field to persist.</summary>
        public bool SetVsTarget(string target, bool on, out string field)
        {
            switch (target)
            {
                case "bot": case "bots": BotVsBot = on; field = "BotVsBot"; return true;
                case "zombie": case "zombies": BotVsZombie = on; field = "BotVsZombie"; return true;
                case "player": case "players": case "human": BotVsPlayer = on; field = "BotVsPlayer"; return true;
                default: field = null; return false;
            }
        }

        /// <summary>Warning sink for config problems. Wired to ModApi.Warn by
        /// ModApi.InitMod so this file stays free of engine/game type
        /// dependencies (headless unit tests can exercise Load); the default
        /// writes to stdout.</summary>
        internal static Action<string> Warn = msg => Console.WriteLine("[BotMod] WARNING: " + msg);

        public static BotConfig Load(string path)
        {
            if (string.IsNullOrEmpty(path)) return new BotConfig();
            // Primary first; if it is missing or unparseable (torn write from an
            // older non-atomic persist, manual edit gone wrong) recover the last
            // known-good .bak instead of silently resetting every persisted
            // setting to defaults. Only when neither file exists (fresh install)
            // or both are unreadable do the C# property initializers below take
            // over as last-resort fallback; config/botmod.json shipped by the
            // build is the operator-facing default otherwise.
            foreach (string candidate in new[] { path, AtomicTextFile.BackupPath(path) })
            {
                string json;
                if (!AtomicTextFile.TryRead(candidate, out json)) continue;
                try
                {
                    var loaded = JsonConvert.DeserializeObject<BotConfig>(json);
                    if (loaded == null) continue;
                    // Json.NET silently ignores keys that bind no property, so a
                    // typo ("TagetBotCount") keeps the built-in default with no
                    // signal at all. Surface every unknown key instead.
                    foreach (string key in UnknownKeys(json))
                        Warn("Unknown config key '" + key + "' in " + candidate + " (typo? key ignored, built-in default applies)");
                    loaded.Normalize();
                    if (candidate != path) Warn("BotConfig restored from backup " + candidate + " (" + path + " was unreadable)");
                    return loaded;
                }
                catch (Exception ex) { Warn("BotConfig parse failed (" + candidate + "): " + ex.Message); }
            }
            return new BotConfig();
        }

        /// <summary>Top-level JSON keys in <paramref name="json"/> that bind no
        /// BotConfig property. Comparison mirrors how JsonConvert binds members
        /// (exact first, then case-insensitive), so valid keys never false-positive.</summary>
        internal static List<string> UnknownKeys(string json)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PropertyInfo p in typeof(BotConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                known.Add(p.Name);
            var unknown = new List<string>();
            foreach (JProperty p in JObject.Parse(json).Properties())
                if (!known.Contains(p.Name)) unknown.Add(p.Name);
            return unknown;
        }
        public void Normalize()
        {
            TargetBotCount = Math.Max(0, Math.Min(64, TargetBotCount));
            MaxBots = Math.Max(TargetBotCount, Math.Min(64, MaxBots));
            BotAmmoCount = Math.Max(0, Math.Min(10000, BotAmmoCount));
            Difficulty = Math.Max(0, Math.Min(4, Difficulty));
            // Finite guards before every clamp below: Newtonsoft parses bare
            // NaN/Infinity literals into float properties, and Math.Max/Math.Min
            // return NaN when either operand is NaN, so a plain clamp chain lets
            // NaN through into hp fractions (divisor side of Health/BotHealth),
            // the neural obs vector and RoundToInt(dmg*HeadshotMultiplier)
            // (same boundary convention as BotCharacter.Normalize).
            BotHealth = Math.Max(10f, Math.Min(10000f, Finite(BotHealth, 100f)));
            VisionRange = Math.Max(8f, Math.Min(300f, Finite(VisionRange, 70f)));
            LoseTargetRange = Math.Max(VisionRange, Math.Min(400f, Finite(LoseTargetRange, 85f)));
            AttackRange = Math.Max(3f, Math.Min(VisionRange, Finite(AttackRange, 45f)));
            AimJitterDegrees = Math.Max(0f, Math.Min(30f, Finite(AimJitterDegrees, 2f)));
            HeadshotChance = Math.Max(0f, Math.Min(1f, Finite(HeadshotChance, 0.08f)));
            // Multiplier feeds an int damage cast: out-of-range magnitudes would
            // overflow Mathf.RoundToInt above ~1.3e8 (unspecified int result,
            // negative values heal targets). Default 2.0.
            HeadshotMultiplier = Math.Max(1f, Math.Min(10f, Finite(HeadshotMultiplier, 2f)));
            BurstMin = Math.Max(1, Math.Min(20, BurstMin));
            BurstMax = Math.Max(BurstMin, Math.Min(30, BurstMax));
            BurstPauseSec = Math.Max(0.1f, Math.Min(3f, Finite(BurstPauseSec, 0.65f)));
            ReactionTimeSec = Math.Max(0f, Math.Min(1.5f, Finite(ReactionTimeSec, 0.28f)));
            PathRecalcIntervalSec = Math.Max(0.08f, Math.Min(5f, Finite(PathRecalcIntervalSec, 0.45f)));
            StuckTimeoutSec = Math.Max(0.5f, Math.Min(20f, Finite(StuckTimeoutSec, 2f)));
            SpawnRadius = Math.Max(2f, Math.Min(500f, Finite(SpawnRadius, 25f)));
            SpawnNearPlayerChance = Math.Max(0f, Math.Min(1f, Finite(SpawnNearPlayerChance, 0.35f)));
            StrafeChance = Math.Max(0f, Math.Min(1f, Finite(StrafeChance, 0.9f)));
            DodgeOnHitChance = Math.Max(0f, Math.Min(1f, Finite(DodgeOnHitChance, 0.75f)));
            // Consumed unclamped (FOV gate, wander pacing, spawn protection):
            // still must be finite after load.
            VisionAngle = Finite(VisionAngle, 190f);
            LoseTargetTimeSec = Finite(LoseTargetTimeSec, 4.5f);
            RandomWanderRadius = Finite(RandomWanderRadius, 60f);
            RandomWanderIntervalSec = Finite(RandomWanderIntervalSec, 5f);
            SpawnProtectionSec = Finite(SpawnProtectionSec, 1.2f);
            BotTeamCount = Math.Max(0, Math.Min(8, BotTeamCount));
            lock (TeamGate)
            {
                if (TeamAssignments == null) TeamAssignments = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                // Canonical keys at ingestion (IdentityKey = NFC + no control/
                // invisible characters): hand-edited configs may hold NFD
                // spellings (macOS editors split accented names into base +
                // combining mark) or paste noise like a zero-width space, while
                // runtime lookups derive IdentityKeys from bot names;
                // OrdinalIgnoreCase alone cannot bridge either gap.
                var canonical = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, int> kv in TeamAssignments)
                {
                    string key = BotText.IdentityKey(kv.Key);
                    if (key.Length == 0) continue;
                    int team = kv.Value < 0 || kv.Value > BotTeamCount ? 0 : kv.Value;
                    canonical[key] = team;
                }
                TeamAssignments = canonical;
            }
            // Drop null/empty entries first (hand-edited JSON tolerates them,
            // e.g. "LoadoutPool": ["gunHandgunT1Pistol", null]): left in,
            // ForGun's mixed pick dereferences null (ToLowerInvariant) and
            // every mixed spawn - including the auto-respawn loop - throws
            // every second; PickName mints tagless "_NN" names. Then apply
            // the documented default when nothing survives the filter.
            BotNames = WithoutEmptyEntries(BotNames);
            if (BotNames.Length == 0) BotNames = new[] { "Bot" };
            LoadoutPool = WithoutEmptyEntries(LoadoutPool);
            if (LoadoutPool.Length == 0) LoadoutPool = new[] { "gunMGT1AK47" };
            // Apply difficulty preset over tunables that weren't hand-tweaked far from defaults
            ApplyDifficulty();
            // The preset can raise VisionRange after the relational clamps
            // above ran (difficulty >= 3 bumps VisionRange), which would
            // strand LoseTargetRange below vision; re-assert the relations.
            LoseTargetRange = Math.Max(VisionRange, Math.Min(400f, LoseTargetRange));
            AttackRange = Math.Max(3f, Math.Min(VisionRange, AttackRange));
        }
        /// <summary>v replaced by fallback when NaN or Infinite (hand-edited
        /// JSON may carry bare NaN/Infinity literals that survive Max/Min
        /// clamps); range clamps then apply to the finite value.</summary>
        static float Finite(float v, float fallback)
        {
            return float.IsNaN(v) || float.IsInfinity(v) ? fallback : v;
        }

        /// <summary>Copy of <paramref name="items"/> without null or empty
        /// entries. Never returns null; an empty result means "everything was
        /// dropped" and the caller applies its documented fallback.</summary>
        static string[] WithoutEmptyEntries(string[] items)
        {
            if (items == null || items.Length == 0) return new string[0];
            var kept = new List<string>(items.Length);
            foreach (string s in items) if (!string.IsNullOrEmpty(s)) kept.Add(s);
            return kept.ToArray();
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
