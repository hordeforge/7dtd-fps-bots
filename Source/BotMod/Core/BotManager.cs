using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using BotMod.Config;
using BotMod.Foundation;

namespace BotMod.Core
{
    public sealed class BotManager
    {
        public static BotManager Instance { get; } = new BotManager();
        // Cycle-break note (the old AI/BotRegistry install shim, removed with
        // 0.7.1): AI namespace query code (BotBrain, BotCombat) calls this Core
        // singleton directly. Core already depends on AI, so an AI -> Core edge
        // would close a namespace cycle between the two layers; it compiles in
        // one assembly but keep Core the only layer that reaches the other way.
        readonly List<Bot> _bots = new List<Bot>();
        // The one id index: O(1) membership and lookup for the per-damage-event /
        // per-shot ally checks; a linear _bots.Find with a closure ran on every
        // DamageEntity, trigger pull and FindTarget candidate. A separate
        // HashSet<int> of the same keys stood beside this dictionary and had to
        // be added to and cleared alongside it at every one of the six mutation
        // sites, so any one of them drifting left IsBotEntity answering for a
        // bot the rest of the registry no longer had (a corpse still counted as
        // a bot, or a live one stopped counting); membership is this
        // dictionary's key set, which has nothing to keep in step.
        readonly Dictionary<int, Bot> _botById = new Dictionary<int, Bot>();
        float _tickAccum;
        float _spawnRetryTimer;
        bool _started;
        // How often the population heartbeat reports alive vs wanted. A slow
        // cadence keeps the line readable; the heartbeat is the only recurring
        // evidence that population maintenance is running at all.
        const float PopulationReportIntervalSec = 30f;
        BotManager() { }
        public IReadOnlyList<Bot> Bots => _bots;
        public int BotCount => _bots.Count;
        public bool IsBotEntity(int entityId) => _botById.ContainsKey(entityId);
        public Bot GetBot(int entityId) => _botById.TryGetValue(entityId, out var b) ? b : null;

        /// <summary>Whether a player is a legal target of a bot operation: in
        /// the world, alive, and not a corpse. One definition, because the two
        /// surfaces that name players have to agree on the answer. The status
        /// roster already filtered on this; the lookup below did not, so the
        /// same id resolved through `bot player` / the web API's spawnNear
        /// where the dashboard's player list omits it, and spawnNear's
        /// response then echoed a dead player's real name back as
        /// "found":true. One predicate, both callers.</summary>
        public static bool IsSelectablePlayer(EntityPlayer p)
        {
            // Main-thread only (reads entity state), which is where every
            // caller runs: console commands execute on the main thread and the
            // web API dispatches to it before resolving a target.
            return p != null && !p.IsDead() && p.IsSpawned();
        }

        /// <summary>Resolve a player by entity id, client id, or (partial) name,
        /// accepting only players <see cref="IsSelectablePlayer"/> admits.
        /// Shared by the `bot player` console command and the web API's spawnNear
        /// so both surfaces accept the same identifiers.</summary>
        public static EntityPlayer FindPlayerByNameOrId(World world, string ident)
        {
            if (world == null || string.IsNullOrEmpty(ident)) return null;
            // by entityId
            // Invariant parse: entity ids are protocol tokens, not locale text
            // (same convention as coordinate parsing in ConsoleCmdBot.DoSpawn).
            if (int.TryParse(ident, NumberStyles.Integer, CultureInfo.InvariantCulture, out int eid)) {
                var e = world.GetEntity(eid) as EntityPlayer;
                if (IsSelectablePlayer(e)) return e;
                // also try ClientInfo entityId lookup
                var cm = ConnectionManager.Instance;
                if (cm != null) {
                    var ci = cm.Clients.ForEntityId(eid);
                    if (ci != null) { var ep = world.GetEntity(ci.entityId) as EntityPlayer; if (IsSelectablePlayer(ep)) return ep; }
                }
            }
            // Name match: BotText.NameMatches canonicalizes both sides to NFC
            // and folds case ordinally, so an NFD spelling typed over telnet
            // finds the NFC name the server holds, without host-locale traps
            // (a tr-TR ToLower would turn "Kira" into "kıra" and miss).
            if (world.Players != null && world.Players.list != null) {
                foreach (var p in world.Players.list) if (IsSelectablePlayer(p)) {
                    string name = p.EntityName ?? p.PlayerDisplayName ?? "";
                    if (BotText.NameMatches(name, ident)) return p;
                }
                // Numeric lookup above resolves the id through ClientInfo, which
                // misses when the connection is gone but the player is still listed.
                foreach (var p in world.Players.list) if (IsSelectablePlayer(p)) {
                    if (p.entityId.ToString(CultureInfo.InvariantCulture) == ident) return p;
                }
            }
            return null;
        }
        // Teams are keyed by base bot name ([Bot] Grunt_42 -> Grunt, same split
        // as BotCharacterDB) so an assignment survives death and respawn.
        public int GetTeamId(int entityId)
        {
            if (ModApi.Config.BotTeamCount <= 0) return 0;
            var bot = GetBot(entityId);
            if (bot == null) return 0;
            // Bot.TeamKey is the base name frozen at spawn; no per-call Split allocs,
            // and the canonical fast path skips re-normalizing it per damage event.
            // Locked lookup: web threads mutate TeamAssignments concurrently.
            return ModApi.Config.GetTeamAssignmentCanonical(bot.TeamKey);
        }
        // Single ally rule for every damage path (targeting, firing, DamageEntity):
        // same entity, or two bots that are globally barred from fighting
        // (vsBot off), in squad mode, or on a shared nonzero team. A MIXED pair
        // (bot vs player/zombie body) is never allied - CombatGates.AllyBlocks
        // scopes the vsBot/squad early returns to bot pairs, so the trigger-pull
        // guard in Bot.TryShootBurst cannot silence bot fire at world bodies.
        public bool AreAllies(int aId, int bId)
        {
            if (aId == bId) return true;
            var cfg = ModApi.Config;
            bool aBot = IsBotEntity(aId);
            bool bBot = IsBotEntity(bId);
            // Team lookups stay behind the both-bots check: world bodies have no
            // registry entry (GetTeamId would return 0) and the hot damage path
            // skips the dictionary work for them.
            if (!aBot || !bBot) return false;
            return CombatGates.AllyBlocks(aBot, bBot, cfg.BotVsBot, cfg.BotTeam, GetTeamId(aId), GetTeamId(bId));
        }
        public void OnGameStartDone()
        {
            _started = true; _tickAccum = 0f; _spawnRetryTimer = 0f;
            _bots.Clear(); _botById.Clear();
            BotSpawner.InvalidateDmSpawnCache();
            var cfg = ModApi.Config;
            BotSpawner.Reseed((uint)cfg.Seed);
            ModApi.Log("BotManager ready. TargetBots=" + cfg.TargetBotCount + " diff=" + cfg.Difficulty + " weapon=" + cfg.BotWeapon + " seed=0x" + ((uint)cfg.Seed).ToString("X8", CultureInfo.InvariantCulture));
        }
        public void OnWorldShuttingDown()
        {
            _started = false; _bots.Clear(); _botById.Clear();
            // The spawnpoint memo is keyed on the world name, so a new world
            // carrying the same name would keep getting the old world's
            // coordinates. Drop it with the rest of the per-world state.
            BotSpawner.InvalidateDmSpawnCache();
        }
        public void Tick(float dt)
        {
            if (!_started) return;
            var world = GameManager.Instance?.World;
            if (world == null)
            {
                // Started but no world: the tick loop runs and simulates
                // nothing, which from the outside is indistinguishable from a
                // disabled mod or a spawner that cannot find ground. Rate
                // limited, so a world that stays unloaded costs one line per
                // window instead of one per tick.
                ModApi.WarnRateLimited(() => "bot tick idle: GameStartDone fired but no world is loaded, so no bot is simulating");
                return;
            }
            _tickAccum += dt;
            _spawnRetryTimer -= dt;
            if (_spawnRetryTimer <= 0f) { _spawnRetryTimer = 1f; MaintainPopulation(); }
            for (int i = _bots.Count - 1; i >= 0; i--)
            {
                var b = _bots[i];
                if (b.IsDeadOrUnloaded(world)) { _botById.Remove(b.EntityId); _bots.RemoveAt(i); continue; }
                try { b.Tick(dt, world); }
                catch (Exception ex)
                {
                    // Tick failures repeat every frame while a bot is broken;
                    // the shared flood gate logs the first one in full, then
                    // counts repeats so one bad bot cannot flood the log.
                    // Lazy message: ex.ToString() walks the stack, so it must
                    // not run per frame while the gate suppresses.
                    ModApi.WarnRateLimited(() => "Bot tick failed id=" + b.EntityId + ": " + ex);
                }
            }
            if (_tickAccum > PopulationReportIntervalSec)
            {
                _tickAccum = 0f;
                int wanted = ModApi.Config.TargetBotCount;
                // Report the deficit, not just the healthy case: gating on
                // "any alive" left the most alarming state (a target set and
                // nothing spawning) with no recurring line at all, so an
                // operator saw only the per-second spawn-failure warning, which
                // names a cause but never the standing alive/wanted gap. With a
                // target of 0 there is nothing to report and the line is skipped.
                if (wanted > 0) ModApi.Log($"Bots alive: {_bots.Count}/{wanted}");
            }
        }
        void MaintainPopulation()
        {
            var cfg = ModApi.Config;
            int target = Math.Min(cfg.TargetBotCount, cfg.MaxBots);
            if (target <= 0) return;
            int alive = 0; var world = GameManager.Instance?.World;
            foreach (var b in _bots) if (!b.IsDeadOrUnloaded(world)) alive++;
            if (alive >= target) return;
            TrySpawnOne();
        }
        public bool TrySpawnOne(Vector3? posOverride = null, string weaponOverride = null)
        {
            var world = GameManager.Instance?.World;
            if (world == null) return false;
            var cfg = ModApi.Config;
            if (_bots.Count >= cfg.MaxBots) { ModApi.Log("Max bots reached (" + cfg.MaxBots + ")"); return false; }
            Vector3 pos = posOverride ?? BotSpawner.PickSpawnPosition(world, cfg);
            if (pos == Vector3.zero) pos = BotSpawner.PickSpawnPosition(world, cfg);
            string name = BotSpawner.PickName(cfg);
            var wp = BotSpawner.PickWeapon(cfg, weaponOverride);
            Entity e = BotSpawner.SpawnBotEntity(world, pos, cfg.BotEntityClass, name);
            if (e == null)
            {
                // MaintainPopulation retries this every second, so an unthrottled
                // warning becomes a line/s flood for as long as the cause lasts
                // (unknown entity class, no valid spawn point). The first
                // occurrence carries the full detail, repeats are counted.
                Vector3 at = pos;
                ModApi.WarnRateLimited(() => "Spawn failed at " + at);
                return false;
            }
            BotSpawner.ConfigureBotEntity(e, cfg, wp.GunId, name);
            var bot = new Bot(e.entityId, name, BotClock.Now, wp);
            _bots.Add(bot); _botById[e.entityId] = bot;
            if (cfg.AnnounceSpawns) ModApi.Log($"Bot spawned: {name} [{wp.GunId}] id={e.entityId} at {pos} ({_bots.Count}/{cfg.TargetBotCount})");
            return true;
        }
        /// <summary>Spawn <paramref name="count"/> bots near an already-resolved
        /// player, retrying each failed position once (shared body of
        /// `bot player` and the web API's spawnNear so both surfaces pick spots
        /// and loadouts identically). Returns how many bots actually spawned.</summary>
        public int SpawnNearPlayer(EntityPlayer target, int count, string weaponOverride)
        {
            var world = GameManager.Instance?.World;
            if (world == null || target == null || count <= 0) return 0;
            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                Vector3 pos = BotSpawner.PickSpawnNearPlayer(world, target, ModApi.Config);
                if (pos == Vector3.zero) pos = BotSpawner.PickSpawnNearPlayer(world, target, ModApi.Config); // retry
                if (TrySpawnOne(pos, weaponOverride: weaponOverride)) spawned++;
            }
            return spawned;
        }
        public int RemoveAllBots(string reason = "command")
        {
            var world = GameManager.Instance?.World; int n = 0;
            // Bots whose world removal threw stay tracked: dropping them here
            // would orphan a live entity nobody manages or can retry removing
            // (the registry is the only handle to it). They are re-offered on
            // the next `bot remove all`.
            List<Bot> stuck = null;
            foreach (var b in _bots.ToArray())
            {
                bool removed = true;
                try
                {
                    if (world != null)
                    {
                        var ent = world.GetEntity(b.EntityId) as EntityAlive;
                        if (ent != null) { ent.SetDead(); world.RemoveEntity(b.EntityId, EnumRemoveEntityReason.Killed); }
                    }
                }
                catch (Exception ex) { ModApi.Warn("Remove bot failed id=" + b.EntityId + ": " + ex.Message); removed = false; }
                if (removed) n++;
                else { if (stuck == null) stuck = new List<Bot>(); stuck.Add(b); }
            }
            _bots.Clear(); _botById.Clear();
            if (stuck != null)
            {
                foreach (var b in stuck) { _bots.Add(b); _botById[b.EntityId] = b; }
                ModApi.Warn("RemoveAll kept " + stuck.Count + " bot(s) whose world removal threw; they stay tracked, retry 'bot remove all'.");
            }
            if (n > 0) ModApi.Log($"Removed {n} bots ({reason}).");
            return n;
        }
        /// <summary>Remove one tracked bot. <paramref name="reason"/> names the
        /// surface ("command" console, "web") in the audit line: a single-bot
        /// removal is destructive and must be reconstructable from the server
        /// log alone, same as RemoveAllBots.</summary>
        public bool RemoveBot(int entityId, string reason = "command")
        {
            var world = GameManager.Instance?.World;
            var bot = GetBot(entityId);
            if (bot == null) return false;
            // False means "still tracked": callers must not report success
            // while the entity may still be alive in-world (it would run on
            // unmanaged as vanilla AI with no registry entry left to find it).
            bool removed = true;
            if (world != null)
            {
                try
                {
                    var ent = world.GetEntity(entityId) as EntityAlive;
                    if (ent != null) { ent.SetDead(); world.RemoveEntity(entityId, EnumRemoveEntityReason.Killed); }
                }
                catch (Exception ex) { ModApi.Warn("Remove bot failed id=" + entityId + ": " + ex.Message); removed = false; }
            }
            if (!removed) return false;
            _bots.Remove(bot); _botById.Remove(entityId);
            ModApi.Log("Removed bot id=" + entityId + " (" + reason + ").");
            return true;
        }
        public void NotifyBotDeath(int entityId) { var bot = GetBot(entityId); if (bot != null) bot.MarkDead(); }
    }
}
