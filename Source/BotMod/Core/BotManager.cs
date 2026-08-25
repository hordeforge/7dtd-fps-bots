using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace BotMod.Core
{
    public sealed class BotManager : BotMod.AI.IBotRegistry
    {
        public static BotManager Instance { get; } = new BotManager();
        // Field initializers run before this static ctor, so Instance is
        // fully built when AI's query bridge starts resolving to it.
        static BotManager() { BotMod.AI.BotRegistry.Install(Instance); }
        readonly List<Bot> _bots = new List<Bot>();
        readonly HashSet<int> _botEntityIds = new HashSet<int>();
        // O(1) id lookup for the per-damage-event / per-shot ally checks; a linear
        // _bots.Find with a closure ran on every DamageEntity, trigger pull and
        // FindTarget candidate.
        readonly Dictionary<int, Bot> _botById = new Dictionary<int, Bot>();
        float _tickAccum;
        float _spawnRetryTimer;
        bool _started;
        BotManager() { }
        public IReadOnlyList<Bot> Bots => _bots;
        public int BotCount => _bots.Count;
        public bool IsBotEntity(int entityId) => _botEntityIds.Contains(entityId);
        public Bot GetBot(int entityId) => _botById.TryGetValue(entityId, out var b) ? b : null;

        // Teams are keyed by base bot name ([Bot] Grunt_42 -> Grunt, same split
        // as BotCharacterDB) so an assignment survives death and respawn.
        // Canonicalization lives in BotMod.Config.BotText (shared with
        // BotCharacterDB; Core already references Config).
        public static string BaseName(string name) => Config.BotText.BaseName(name);
        /// <summary>Resolve a player by entity id, client id, or (partial) name.
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
                if (e != null) return e;
                // also try ClientInfo entityId lookup
                var cm = ConnectionManager.Instance;
                if (cm != null) {
                    var ci = cm.Clients.ForEntityId(eid);
                    if (ci != null) { var ep = world.GetEntity(ci.entityId) as EntityPlayer; if (ep != null) return ep; }
                }
            }
            // Name match: BotText.NameMatches canonicalizes both sides to NFC
            // and folds case ordinally, so an NFD spelling typed over telnet
            // finds the NFC name the server holds, without host-locale traps
            // (a tr-TR ToLower would turn "Kira" into "kıra" and miss).
            if (world.Players != null && world.Players.list != null) {
                foreach (var p in world.Players.list) if (p != null) {
                    string name = p.EntityName ?? p.PlayerDisplayName ?? "";
                    if (Config.BotText.NameMatches(name, ident)) return p;
                }
                // exact entityId string already tried; try prefix match
                foreach (var p in world.Players.list) if (p != null) {
                    if (p.entityId.ToString() == ident) return p;
                }
            }
            return null;
        }
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
            return Config.CombatGates.AllyBlocks(aBot, bBot, cfg.BotVsBot, cfg.BotTeam, GetTeamId(aId), GetTeamId(bId));
        }
        public void OnGameStartDone()
        {
            _started = true; _tickAccum = 0f; _spawnRetryTimer = 0f;
            _bots.Clear(); _botEntityIds.Clear(); _botById.Clear();
            ModApi.Log("BotManager ready. TargetBots=" + ModApi.Config.TargetBotCount + " diff=" + ModApi.Config.Difficulty + " weapon=" + ModApi.Config.BotWeapon);
        }
        public void OnWorldShuttingDown() { _started = false; _bots.Clear(); _botEntityIds.Clear(); _botById.Clear(); }
        public void Tick(float dt)
        {
            if (!_started) return;
            var world = GameManager.Instance?.World;
            if (world == null) return;
            _tickAccum += dt;
            _spawnRetryTimer -= dt;
            if (_spawnRetryTimer <= 0f) { _spawnRetryTimer = 1f; MaintainPopulation(); }
            for (int i = _bots.Count - 1; i >= 0; i--)
            {
                var b = _bots[i];
                if (b.IsDeadOrUnloaded(world)) { _botEntityIds.Remove(b.EntityId); _botById.Remove(b.EntityId); _bots.RemoveAt(i); continue; }
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
            if (_tickAccum > 30f) { _tickAccum = 0f; if (_bots.Count > 0) ModApi.Log($"Bots alive: {_bots.Count}/{ModApi.Config.TargetBotCount}"); }
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
            var character = BotMod.Config.BotCharacterDB.ForName(name);
            if (e == null) { ModApi.Warn("Spawn failed at " + pos); return false; }
            BotSpawner.ConfigureBotEntity(e, cfg, wp, name);
            var bot = new Bot(e.entityId, name, Time.time, wp, character);
            _bots.Add(bot); _botEntityIds.Add(e.entityId); _botById[e.entityId] = bot;
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
            _bots.Clear(); _botEntityIds.Clear(); _botById.Clear();
            if (stuck != null)
            {
                foreach (var b in stuck) { _bots.Add(b); _botEntityIds.Add(b.EntityId); _botById[b.EntityId] = b; }
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
            _bots.Remove(bot); _botEntityIds.Remove(entityId); _botById.Remove(entityId);
            ModApi.Log("Removed bot id=" + entityId + " (" + reason + ").");
            return true;
        }
        public void NotifyBotDeath(int entityId) { var bot = GetBot(entityId); if (bot != null) bot.MarkDead(); }
    }
}
