using System;
using System.Collections.Generic;
using UnityEngine;

namespace BotMod.Core
{
    public sealed class BotManager
    {
        public static BotManager Instance { get; } = new BotManager();
        readonly List<Bot> _bots = new List<Bot>();
        readonly HashSet<int> _botEntityIds = new HashSet<int>();
        // O(1) id lookup for the per-damage-event / per-shot ally checks; a linear
        // _bots.Find with a closure ran on every DamageEntity, trigger pull and
        // FindTarget candidate.
        readonly Dictionary<int, Bot> _botById = new Dictionary<int, Bot>();
        float _tickAccum;
        float _spawnRetryTimer;
        // Tick failures repeat every frame while a bot is broken; log the first
        // one in full, then suppress repeats for a cooldown so one bad bot
        // cannot flood the server log (~60 lines/s otherwise).
        const float TickFailLogCooldownSec = 10f;
        float _tickFailCooldown;
        int _tickFailsSuppressed;
        bool _started;
        BotManager() { }
        public IReadOnlyList<Bot> Bots => _bots;
        public int BotCount => _bots.Count;
        public bool IsBotEntity(int entityId) => _botEntityIds.Contains(entityId);
        public bool IsBotEntity(Entity e) => e != null && _botEntityIds.Contains(e.entityId);
        public Bot GetBot(int entityId) => _botById.TryGetValue(entityId, out var b) ? b : null;

        // Teams are keyed by base bot name ([Bot] Grunt_42 -> Grunt, same split
        // as BotCharacterDB) so an assignment survives death and respawn.
        public static string BaseName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            if (name.StartsWith("[Bot] ", StringComparison.OrdinalIgnoreCase)) name = name.Substring(6);
            return name.Split('_')[0];
        }
        /// <summary>Resolve a player by entity id, client id, or (partial) name.
        /// Shared by the `bot player` console command and the web API's spawnNear
        /// so both surfaces accept the same identifiers.</summary>
        public static EntityPlayer FindPlayerByNameOrId(World world, string ident)
        {
            if (world == null || string.IsNullOrEmpty(ident)) return null;
            // by entityId
            if (int.TryParse(ident, out int eid)) {
                var e = world.GetEntity(eid) as EntityPlayer;
                if (e != null) return e;
                // also try ClientInfo entityId lookup
                var cm = ConnectionManager.Instance;
                if (cm != null) {
                    var ci = cm.Clients.ForEntityId(eid);
                    if (ci != null) { var ep = world.GetEntity(ci.entityId) as EntityPlayer; if (ep != null) return ep; }
                }
            }
            string low = ident.ToLowerInvariant();
            if (world.Players != null && world.Players.list != null) {
                foreach (var p in world.Players.list) if (p != null) {
                    string name = p.EntityName ?? p.PlayerDisplayName ?? "";
                    if (name.ToLowerInvariant() == low || name.ToLowerInvariant().Contains(low)) return p;
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
            // Bot.TeamKey is the base name frozen at spawn; no per-call Split allocs.
            // Locked lookup: web threads mutate TeamAssignments concurrently.
            return ModApi.Config.GetTeamAssignment(bot.TeamKey);
        }
        // Single ally rule for every damage path (targeting, firing, DamageEntity):
        // same entity, global no-bot-vs-bot, squad mode, or a shared nonzero team.
        public bool AreAllies(int aId, int bId)
        {
            if (aId == bId) return true;
            var cfg = ModApi.Config;
            if (!cfg.BotVsBot) return true; // bots never fight bots
            if (cfg.BotTeam) return true;   // squad mode: everyone allies
            int ta = GetTeamId(aId), tb = GetTeamId(bId);
            return ta != 0 && ta == tb;
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
            if (_tickFailCooldown > 0f) _tickFailCooldown -= dt;
            if (_spawnRetryTimer <= 0f) { _spawnRetryTimer = 1f; MaintainPopulation(); }
            for (int i = _bots.Count - 1; i >= 0; i--)
            {
                var b = _bots[i];
                if (b.IsDeadOrUnloaded(world)) { _botEntityIds.Remove(b.EntityId); _botById.Remove(b.EntityId); _bots.RemoveAt(i); continue; }
                try { b.Tick(dt, world); }
                catch (Exception ex)
                {
                    if (_tickFailCooldown <= 0f)
                    {
                        string suppressed = _tickFailsSuppressed > 0 ? " (+ " + _tickFailsSuppressed + " suppressed)" : "";
                        ModApi.Warn("Bot tick failed id=" + b.EntityId + suppressed + ": " + ex);
                        _tickFailsSuppressed = 0;
                        _tickFailCooldown = TickFailLogCooldownSec;
                    }
                    else _tickFailsSuppressed++;
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
        public bool TrySpawnOne(Vector3? posOverride = null, string nameOverride = null, string weaponOverride = null)
        {
            var world = GameManager.Instance?.World;
            if (world == null) return false;
            var cfg = ModApi.Config;
            if (_bots.Count >= cfg.MaxBots) { ModApi.Log("Max bots reached (" + cfg.MaxBots + ")"); return false; }
            Vector3 pos = posOverride ?? BotSpawner.PickSpawnPosition(world, cfg);
            if (pos == Vector3.zero) pos = BotSpawner.PickSpawnPosition(world, cfg);
            string name = nameOverride ?? BotSpawner.PickName(cfg);
            string gun = weaponOverride;
            var wp = BotSpawner.PickWeapon(cfg, gun);
            Entity e = BotSpawner.SpawnBotEntity(world, pos, cfg.BotEntityClass, name);
            var character = BotMod.Config.BotCharacterDB.ForName(name);
            if (e == null) { ModApi.Warn("Spawn failed at " + pos); return false; }
            BotSpawner.ConfigureBotEntity(e, cfg, wp, name);
            var bot = new Bot(e.entityId, name, Time.time, wp, character);
            _bots.Add(bot); _botEntityIds.Add(e.entityId); _botById[e.entityId] = bot;
            if (cfg.AnnounceSpawns) ModApi.Log($"Bot spawned: {name} [{wp.GunId}] id={e.entityId} at {pos} ({_bots.Count}/{cfg.TargetBotCount})");
            return true;
        }
        public int RemoveAllBots(string reason = "command")
        {
            var world = GameManager.Instance?.World; int n = 0;
            foreach (var b in _bots.ToArray())
            {
                try { if (world != null) { var ent = world.GetEntity(b.EntityId) as EntityAlive; if (ent != null) { ent.SetDead(); world.RemoveEntity(b.EntityId, EnumRemoveEntityReason.Killed); } } _botEntityIds.Remove(b.EntityId); n++; }
                catch (Exception ex) { ModApi.Warn("Remove bot failed: " + ex.Message); }
            }
            _bots.Clear(); _botEntityIds.Clear(); _botById.Clear();
            if (n > 0) ModApi.Log($"Removed {n} bots ({reason}).");
            return n;
        }
        public bool RemoveBot(int entityId)
        {
            var world = GameManager.Instance?.World;
            var bot = GetBot(entityId);
            if (bot == null) return false;
            try { if (world != null) { var ent = world.GetEntity(entityId) as EntityAlive; if (ent != null) { ent.SetDead(); world.RemoveEntity(entityId, EnumRemoveEntityReason.Killed); } } } catch (Exception ex) { ModApi.Warn("Remove bot failed: " + ex.Message); }
            _bots.Remove(bot); _botEntityIds.Remove(entityId); _botById.Remove(entityId);
            return true;
        }
        public void NotifyBotDeath(int entityId) { var bot = GetBot(entityId); if (bot != null) bot.MarkDead(); }
    }
}
