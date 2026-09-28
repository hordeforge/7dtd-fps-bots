using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BotMod.Foundation;

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
        /// <summary>Default spawn seed, and the value every consumer of the
        /// spawn stream must agree on (BotSpawner and WeaponProfile seed their
        /// streams from it).</summary>
        public const int DefaultSeed = 0xC0FFEE;
        /// <summary>Seed for the spawn-time picks: bot name, gun, spawn spot
        /// and the mixed loadout counter. Everything a live bot decides on its
        /// own comes from its entity id, so this seed is what makes the spawn
        /// side of a run reproducible: the same seed and the same sequence of
        /// spawn requests pick the same names, guns and spots. It is applied
        /// once per world (BotManager.OnGameStartDone) and printed in the
        /// startup config line, so a run that misbehaved can be reproduced by
        /// starting another world with the same value. Replaying needs the same
        /// world and the same spawn order too, since the stream is consumed in
        /// request order. Any int is a valid seed, including 0.</summary>
        public int Seed { get; set; } = DefaultSeed;
        // Bot body. "mixed" resolves to the one class this mod can spawn and
        // render: mod-spawned trader bodies (npcTraderJoel) render nothing on
        // this dedi and survivor classes come back negative, so zombieSoldier is
        // the working visible FPS body (our loop drives its combat, not the
        // zombie AI). Any other value is honored as written
        // (BotSpawner.SpawnBotEntity), which falls back to zombieSoldier, then
        // zombieBoe, npcTraderJoel, npcSurvivorRanged, when it does not resolve.
        public string BotEntityClass { get; set; } = "mixed";
        public string BotWeapon { get; set; } = "mixed"; // mixed=random per bot from LoadoutPool, or a single gun id
        public string BotAmmo { get; set; } = "ammo762mmBulletBall";
        public int BotAmmoCount { get; set; } = 300;
        public float BotHealth { get; set; } = 100f; // Q3-like 100 (armor handled via config if desired)
        public string[] BotNames { get; set; } = new[] { "Grunt", "Ranger", "Phobos", "Dozer", "Klesk", "Sorlag", "TankJr", "Hunter", "Wrack", "Visor", "Bones", "Slash" };
        public string[] LoadoutPool { get; set; } = new[] { "gunHandgunT1Pistol", "gunShotgunT1DoubleBarrel", "gunMGT1AK47", "gunRifleT3SniperRifle", "gunShotgunT3AutoShotgun", "gunHandgunT3SMG5" };
        // 0 bot, 1 easy, 2 normal, 3 hard, 4 nightmare - like Q3 bot cvars
        public int Difficulty { get; set; } = 2;
        // The five Stock* constants below are the values the difficulty
        // preset and its bounds drive from. Named so the pristine snapshot in
        // RawTunables, the property initializers and the Normalize clamps all
        // read the same definition.
        public const float StockVisionRange = 70f;
        public const float StockAttackRange = 45f;
        public const float StockReactionTimeSec = 0.28f;
        public const float StockHeadshotChance = 0.08f;
        public float VisionRange { get; set; } = StockVisionRange;
        public float VisionAngle { get; set; } = 190f; // FPS bots look around, not narrow cone
        public float LoseTargetRange { get; set; } = 85f;
        public float LoseTargetTimeSec { get; set; } = 4.5f;
        public float AttackRange { get; set; } = StockAttackRange;
        public float HeadshotChance { get; set; } = StockHeadshotChance;
        public float HeadshotMultiplier { get; set; } = 2.0f;
        public float ReactionTimeSec { get; set; } = StockReactionTimeSec; // see -> shoot delay
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

        /// <summary>The values the loaded file actually carried, kept so
        /// Normalize can always recompute from them instead of from whatever
        /// the previous Normalize wrote. The difficulty preset and its bounds
        /// overwrite these four fields in place, so recomputing from the live
        /// value made every field's result depend on how many times Normalize
        /// had run and on which difficulties it had seen: the preset's own
        /// output was fed back in as its input, and a field only ever moved
        /// further from stock. `bot skill 0` then `bot skill 2` left the easy
        /// 0.42s reaction in place, `bot skill 4` then `bot skill 2` never
        /// restored headshot chance off its 0.04 cap, and difficulty 4's 120m
        /// vision survived the drop back to 2. Recomputing from the pristine
        /// value makes Normalize idempotent and every difficulty change
        /// reversible, and it still leaves an operator's own value alone
        /// because that value is the pristine one.</summary>
        struct RawTunables
        {
            public float ReactionTimeSec;
            public float VisionRange;
            public float AttackRange;
            public float HeadshotChance;
            /// <summary>True when the file asked for something other than the
            /// stock value, i.e. a real override rather than the preset's own
            /// starting point. The shipped botmod.json spells it out at
            /// stock, so it keeps following Difficulty; a tuned value does not
            /// get overwritten because it happens to sit near stock.</summary>
            public bool ReactionTimeSecOverridden;
        }
        RawTunables _raw = new RawTunables
        {
            ReactionTimeSec = StockReactionTimeSec,
            VisionRange = StockVisionRange,
            AttackRange = StockAttackRange,
            HeadshotChance = StockHeadshotChance
        };

        /// <summary>Record the loaded file's values for the preset-driven
        /// fields. Called after deserialization and before the first
        /// Normalize; a config that never came from a file (the built-in
        /// defaults) is already pristine.</summary>
        void CaptureRawTunables()
        {
            _raw.ReactionTimeSec = ReactionTimeSec;
            _raw.ReactionTimeSecOverridden = ReactionTimeSec != StockReactionTimeSec;
            _raw.VisionRange = VisionRange;
            _raw.AttackRange = AttackRange;
            _raw.HeadshotChance = HeadshotChance;
        }

        /// <summary>Put the preset-driven fields back to the loaded file's
        /// values so the clamps and the difficulty preset below both see the
        /// operator's input rather than the previous pass's output.</summary>
        void RestoreRawTunables()
        {
            ReactionTimeSec = _raw.ReactionTimeSec;
            VisionRange = _raw.VisionRange;
            AttackRange = _raw.AttackRange;
            HeadshotChance = _raw.HeadshotChance;
        }

        /// <summary>Outcome of one SetTeamAssignment call. Callers must report
        /// anything but <see cref="Ok"/>: a silently dropped assignment reads
        /// to the operator as "the team is set and the bots still fight each
        /// other".</summary>
        public enum TeamAssignResult { Ok, NoName, NameTooLong, AtCapacity }

        /// <summary>Upper bound on stored team assignments. The map is keyed by
        /// operator-supplied text (web `setTeam`, console `bot team assign`) and
        /// only `bot team clear` or a team drop removes a key, so an unbounded
        /// map grows for the life of the process and is written whole into
        /// botmod.json on every assignment. It is also read under TeamGate on
        /// every damage event and every FindTarget candidate, so the growth
        /// lands on the combat hot path, not just on disk. Generous next to a
        /// real deployment (BotNames holds a handful of base names) and well
        /// above any hand-built set, so the cap is only reached by a client
        /// looping distinct names.</summary>
        internal const int MaxTeamAssignments = 256;
        /// <summary>Key limit in characters, counted by BotText.CharCount so an
        /// emoji name is not rejected for measuring double in UTF-16 (same
        /// convention as the idempotency ledger's key limit).</summary>
        internal const int MaxTeamNameChars = 64;

        /// <summary>Set (team > 0) or clear (team <= 0) an assignment keyed by
        /// base bot name. Accepts a bare base name or a full spawned name
        /// ("[Bot] Kíra_42"): the key derives through BotText.BaseName, the
        /// same split live-bot lookups use (Bot.TeamKey), so every surface
        /// (web JSON, console, a pasted scoreboard name) lands on one stored
        /// NFC form instead of a near-miss key that silently never matches.
        /// A key over the name limit, or a new key past MaxTeamAssignments, is
        /// refused rather than stored, and the caller learns why from the
        /// returned result.</summary>
        public TeamAssignResult SetTeamAssignment(string baseName, int team)
        {
            if (string.IsNullOrEmpty(baseName)) return TeamAssignResult.NoName;
            string key = BotText.BaseName(baseName);
            if (key.Length == 0) return TeamAssignResult.NoName;
            if (BotText.CharCount(key) > MaxTeamNameChars) return TeamAssignResult.NameTooLong;
            lock (TeamGate)
            {
                if (team <= 0) { TeamAssignments.Remove(key); return TeamAssignResult.Ok; }
                if (!TeamAssignments.ContainsKey(key) && TeamAssignments.Count >= MaxTeamAssignments)
                    return TeamAssignResult.AtCapacity;
                TeamAssignments[key] = team;
                return TeamAssignResult.Ok;
            }
        }

        public void ClearTeamAssignments()
        {
            lock (TeamGate) TeamAssignments = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Locked lookup for hot paths (per-damage-event ally checks).
        /// The key is derived exactly as SetTeamAssignment derives it, so the
        /// two sides cannot disagree: a full spawned name ("[Bot] Grunt_42")
        /// and an NFD or invisible-noise spelling both resolve to the entry
        /// stored under the canonical base name.</summary>
        public int GetTeamAssignment(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) return 0;
            string key = BotText.BaseName(baseName);
            if (key.Length == 0) return 0;
            lock (TeamGate)
            {
                int t;
                return TeamAssignments.TryGetValue(key, out t) ? Math.Max(0, t) : 0;
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
        // Neural brain (docs/research/00..06): advisory only, heuristic fallback
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

        /// <summary>Clamp + apply an admin difficulty level (web `skill` action,
        /// `bot skill` console command): one definition of the 0..4 range so
        /// both surfaces cannot drift, then Normalize so derived tunables
        /// follow the preset. Returns the JSON field name to pass to
        /// ModApi.PersistConfigField.</summary>
        public string SetDifficulty(int level)
        {
            Difficulty = Math.Max(0, Math.Min(4, level));
            Normalize();
            return "Difficulty";
        }

        /// <summary>Clamp + apply the team-bucket count from an admin surface
        /// (web `teamCount` action, `bot teams` console command): one
        /// definition of the 0..8 range, then Normalize rewrites assignments
        /// outside the new range to 0. Returns the JSON field name to pass to
        /// ModApi.PersistConfigField (persist SnapshotTeamAssignments()
        /// alongside it).</summary>
        public string SetTeamCount(int count)
        {
            BotTeamCount = Math.Max(0, Math.Min(8, count));
            Normalize();
            return "BotTeamCount";
        }

        /// <summary>Warning sink for config-layer problems (BotConfig.Load and
        /// BotCharacterDB ingestion). Wired to ModApi.Warn by ModApi.InitMod
        /// so this layer stays free of engine/game type dependencies
        /// (headless unit tests can exercise Load); the default writes to
        /// stdout. Volatile for the same reason the ledger's sink is: wired on
        /// the main thread by InitMod, read from web handler threads
        /// (PersistConfigField's audit path).</summary>
        internal static Action<string> Warn
        {
            get { return System.Threading.Volatile.Read(ref _warn); }
            set { System.Threading.Volatile.Write(ref _warn, value); }
        }

        static Action<string> _warn = msg => Console.WriteLine("[BotMod] WARNING: " + msg);

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
                string json, source;
                if (!AtomicTextFile.TryRead(candidate, out json, out source)) continue;
                try
                {
                    var loaded = JsonConvert.DeserializeObject<BotConfig>(json);
                    if (loaded == null)
                    {
                        // Deserialization can yield a null object without
                        // throwing (a hand-edited file holding bare JSON
                        // "null"): surface it like every other unreadable
                        // candidate instead of silently falling through to
                        // .bak / defaults with no signal at all.
                        Warn("BotConfig parse failed (" + source + "): JSON body deserialized to null");
                        continue;
                    }
                    // Json.NET silently ignores keys that bind no property, so a
                    // typo ("TagetBotCount") keeps the built-in default with no
                    // signal at all. Surface every unknown key instead.
                    foreach (string key in UnknownKeys(json))
                        Warn("Unknown config key '" + key + "' in " + source + " (typo? key ignored, built-in default applies)");
                    loaded.CaptureRawTunables();
                    loaded.Normalize();
                    // The content may have come from TryRead's internal .bak
                    // fallback (primary missing or deleted, not merely torn)
                    // rather than this loop's own candidate; compare against the
                    // file actually read so every restore-from-backup is
                    // visible, not just the corrupt-primary one.
                    if (source != candidate)
                        Warn("BotConfig restored from backup " + source + " (" + candidate + " was unreadable)");
                    WarnUnsafe(loaded, source);
                    return loaded;
                }
                catch (Exception ex) { Warn("BotConfig parse failed (" + source + "): " + ex.Message); }
            }
            // Neither the primary nor its .bak was readable. A shipped install
            // always has the file (config/botmod.json lands in Config/), so
            // silence here is how a deleted file, a bad BOTMOD_CONFIG or a wrong
            // Mods layout turns into "the mod runs, but on the C# property
            // defaults" (difficulty 2, 100 hp) with nothing in the log. Say so
            // and name every path that was tried.
            Warn("no config file found (" + path + ", " + AtomicTextFile.BackupPath(path)
                + "); running on built-in defaults" + (ConfigPathOverride() != null
                    ? " - check the " + ConfigPathEnvVar + " override" : ""));
            return new BotConfig();
        }

        /// <summary>Report a loaded setting that weakens the server's identity
        /// checks, on every load and reload instead of only inside the startup
        /// config line an operator has to parse field by field. Today that is
        /// one switch, and it is the one field here whose wrong value is not
        /// visible in play: the forged-id connection it permits behaves like an
        /// ordinary client. <paramref name="source"/> is the file the value
        /// came from, so an operator tracing the log back to a file is not left
        /// guessing which of botmod.json and its .bak asked for it.</summary>
        static void WarnUnsafe(BotConfig loaded, string source)
        {
            if (loaded.AllowSyntheticAuthBypass)
                Warn("AllowSyntheticAuthBypass is on in " + source
                    + ": any client can connect with a synthetic Steam id in the reserved range and is treated as authenticated");
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
            // Preset-driven fields are recomputed from what the file carried,
            // never from what the last Normalize left behind, so this method
            // is idempotent and a difficulty change is exactly reversible.
            RestoreRawTunables();
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
            HeadshotChance = Math.Max(0f, Math.Min(1f, Finite(HeadshotChance, 0.08f)));
            // Multiplier feeds an int damage cast: out-of-range magnitudes would
            // overflow Mathf.RoundToInt above ~1.3e8 (unspecified int result,
            // negative values heal targets). Default 2.0.
            HeadshotMultiplier = Math.Max(1f, Math.Min(10f, Finite(HeadshotMultiplier, 2f)));
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
                    // Same key limits the runtime setter enforces: botmod.json is
                    // hand-edited operator text and every assignment is written
                    // back into it whole, so an over-long or unbounded key set
                    // would otherwise enter the map once per load and stay.
                    if (BotText.CharCount(key) > MaxTeamNameChars) continue;
                    int team = kv.Value < 0 || kv.Value > BotTeamCount ? 0 : kv.Value;
                    canonical[key] = team;
                }
                TeamAssignments = TruncateToCap(canonical);
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
            if (LoadoutPool.Length == 0) LoadoutPool = new[] { WeaponProfile.DefaultGun };
            // Apply difficulty preset over tunables that weren't hand-tweaked far from defaults
            ApplyDifficulty();
            // The preset can raise VisionRange after the relational clamps
            // above ran (difficulty >= 3 bumps VisionRange), which would
            // strand LoseTargetRange below vision; re-assert the relations.
            LoseTargetRange = Math.Max(VisionRange, Math.Min(400f, LoseTargetRange));
            AttackRange = Math.Max(3f, Math.Min(VisionRange, AttackRange));
        }
        /// <summary>Map bounded to MaxTeamAssignments. Over the cap, the
        /// ordinal-first keys are kept so two hosts loading the same file keep
        /// the same assignments (dictionary order is not stable), and the drop
        /// is reported: a silently shortened map reads as "those teams were
        /// never set".</summary>
        static Dictionary<string, int> TruncateToCap(Dictionary<string, int> assignments)
        {
            if (assignments.Count <= MaxTeamAssignments) return assignments;
            var keys = new List<string>(assignments.Keys);
            keys.Sort(StringComparer.Ordinal);
            var kept = new Dictionary<string, int>(Math.Min(assignments.Count, MaxTeamAssignments), StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < MaxTeamAssignments; i++) kept[keys[i]] = assignments[keys[i]];
            Warn("TeamAssignments holds " + assignments.Count + " entries; kept the first "
                + MaxTeamAssignments + " (botmod.json is hand-edited text, and each key is stored and looked up per damage event)");
            return kept;
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
            // Higher diff = faster reaction, tighter aim, wider engagement
            float react = 0.42f - Difficulty * 0.09f; // 0.42,0.33,0.24,0.15,0.06
            // Preset: the difficulty authors this outright, from the file's
            // own value, so an operator who tuned it keeps it and everyone
            // else tracks Difficulty in both directions. Aim tightness is the
            // characters' own (BotCharacter.AimAccuracy, difficulty-lerped in
            // BotCharacterDB.Load) and burst shape is per-weapon
            // (WeaponProfile), so neither is a config field to scale here.
            if (!_raw.ReactionTimeSecOverridden) ReactionTimeSec = Math.Max(0.05f, react);
            // Bounds: these pull a value toward the difficulty's floor or
            // ceiling instead of authoring one, so they compose with whatever
            // the file asked for rather than replacing it.
            if (Difficulty >= 3 && VisionRange < 80f) VisionRange = 80f + Difficulty * 10f;
            if (Difficulty >= 3 && AttackRange < 50f) AttackRange = 50f;
            if (Difficulty <= 1) { HeadshotChance = Math.Min(HeadshotChance, 0.04f); }
            else if (Difficulty >= 3) HeadshotChance = Math.Max(HeadshotChance, 0.1f + Difficulty * 0.02f);
        }
        /// <summary>One-line JSON of the effective configuration: every value
        /// as it will run, after Normalize has clamped and cross-checked it.
        /// This is the answer to "what is the server actually running", for the
        /// startup log line and the `bot config` dump; the raw file still hides
        /// the clamps, the difficulty preset and every field the operator never
        /// set. BotConfig holds no credentials, so the whole object is safe to
        /// log and print (see docs/THREAT_MODEL.md for the field inventory).</summary>
        public string EffectiveSummary()
        {
            return JsonConvert.SerializeObject(this, Formatting.None);
        }

        /// <summary>Environment variable naming the botmod.json to read and
        /// persist, overriding the assembly-relative default. Same
        /// environment-over-defaults contract as SEVENDTD_DS_DIR in the repo's
        /// shell scripts: unset (or blank) means "next to the assembly". It
        /// exists so a deployment that mounts its config outside the mod
        /// directory - a bind-mounted host file, a config map, a read-only
        /// image with a writable copy elsewhere - does not have to also mount
        /// the config into Config/, and so the read path and the persist path
        /// can never end up on two different files.</summary>
        public const string ConfigPathEnvVar = "BOTMOD_CONFIG";

        /// <summary>The BOTMOD_CONFIG value, or null when unset or blank.
        /// Blank (exported empty by a wrapper script) is treated as unset, not
        /// as a request to read from the current directory.</summary>
        public static string ConfigPathOverride()
        {
            string env = null;
            try { env = Environment.GetEnvironmentVariable(ConfigPathEnvVar); }
            catch (Exception) { env = null; } // no env in some sandboxes
            if (string.IsNullOrEmpty(env) || env.Trim().Length == 0) return null;
            return env.Trim();
        }

        /// <summary>Path of the config file to read, and the file
        /// PersistConfigField writes: the override when set, else
        /// Config/botmod.json beside the assembly. One resolver, so the load
        /// path and the write path cannot disagree about which file is
        /// authoritative.</summary>
        public static string ConfigPath()
        {
            string env = ConfigPathOverride();
            if (env != null) return env;
            return DefaultPathBesideAssembly();
        }

        static string DefaultPathBesideAssembly()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            string a = Path.Combine(dir, "Config", "botmod.json");
            if (File.Exists(a)) return a;
            return Path.Combine(dir, "botmod.json");
        }
    }
}
