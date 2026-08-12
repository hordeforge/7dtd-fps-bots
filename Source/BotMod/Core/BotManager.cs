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
        float _tickAccum;
        float _spawnRetryTimer;
        bool _started;
        BotManager() { }
        public IReadOnlyList<Bot> Bots => _bots;
        public int BotCount => _bots.Count;
        public bool IsBotEntity(int entityId) => _botEntityIds.Contains(entityId);
        public bool IsBotEntity(Entity e) => e != null && _botEntityIds.Contains(e.entityId);
        public Bot GetBot(int entityId) => _bots.Find(b => b.EntityId == entityId);
        public void OnGameStartDone()
        {
            _started = true; _tickAccum = 0f; _spawnRetryTimer = 0f;
            _bots.Clear(); _botEntityIds.Clear();
            ModApi.Log("BotManager ready. TargetBots=" + ModApi.Config.TargetBotCount + " diff=" + ModApi.Config.Difficulty + " weapon=" + ModApi.Config.BotWeapon);
        }
        public void OnWorldShuttingDown() { _started = false; _bots.Clear(); _botEntityIds.Clear(); }
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
                if (b.IsDeadOrUnloaded(world)) { _botEntityIds.Remove(b.EntityId); _bots.RemoveAt(i); continue; }
                try { b.Tick(dt, world); } catch (Exception ex) { ModApi.Log("Bot tick failed id=" + b.EntityId + " " + ex.Message); }
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
            if (e == null) { ModApi.Log("Spawn failed at " + pos); return false; }
            BotSpawner.ConfigureBotEntity(e, cfg, wp);
            var bot = new Bot(e.entityId, name, Time.time, wp);
            _bots.Add(bot); _botEntityIds.Add(e.entityId);
            if (cfg.AnnounceSpawns) ModApi.Log($"Bot spawned: {name} [{wp.GunId}] id={e.entityId} at {pos} ({_bots.Count}/{cfg.TargetBotCount})");
            return true;
        }
        public int RemoveAllBots(string reason = "command")
        {
            var world = GameManager.Instance?.World; int n = 0;
            foreach (var b in _bots.ToArray())
            {
                try { if (world != null) { var ent = world.GetEntity(b.EntityId) as EntityAlive; if (ent != null) { ent.SetDead(); world.RemoveEntity(b.EntityId, EnumRemoveEntityReason.Killed); } } _botEntityIds.Remove(b.EntityId); n++; }
                catch (Exception ex) { ModApi.Log("Remove bot failed: " + ex.Message); }
            }
            _bots.Clear(); _botEntityIds.Clear();
            if (n > 0) ModApi.Log($"Removed {n} bots ({reason}).");
            return n;
        }
        public bool RemoveBot(int entityId)
        {
            var world = GameManager.Instance?.World;
            var bot = _bots.Find(b => b.EntityId == entityId);
            if (bot == null) return false;
            try { if (world != null) { var ent = world.GetEntity(entityId) as EntityAlive; if (ent != null) { ent.SetDead(); world.RemoveEntity(entityId, EnumRemoveEntityReason.Killed); } } } catch (Exception ex) { ModApi.Log("Remove bot failed: " + ex.Message); }
            _bots.Remove(bot); _botEntityIds.Remove(entityId);
            return true;
        }
        public void NotifyBotDeath(int entityId) { var bot = _bots.Find(b => b.EntityId == entityId); if (bot != null) bot.MarkDead(); }
    }
}
