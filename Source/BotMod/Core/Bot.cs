using System;
using BotMod.AI;
using UnityEngine;

namespace BotMod.Core
{
    public sealed class Bot
    {
        public int EntityId { get; }
        public string Name { get; }
        public float SpawnTime { get; }
        bool _dead;

        EntityAlive _cachedEntity;
        float _nextTargetScan;
        float _nextPathRecalc;
        float _nextFire;
        float _stuckSince;
        Vector3 _lastPos;
        Vector3 _wanderTarget;
        float _nextWander;
        float _loseTargetTimer;

        EntityAlive _target;
        BotBrain.State _state = BotBrain.State.Wander;

        public Bot(int entityId, string name, float now)
        {
            EntityId = entityId;
            Name = name;
            SpawnTime = now;
            _lastPos = Vector3.zero;
        }

        public void MarkDead() { _dead = true; }

        public bool IsDeadOrUnloaded(World world)
        {
            if (_dead) return true;
            if (world == null) return false;
            var e = world.GetEntity(EntityId) as EntityAlive;
            if (e == null) return true;
            if (e.IsDead() || !e.IsAlive()) return true;
            return false;
        }

        EntityAlive GetEntity(World world)
        {
            if (_cachedEntity != null && _cachedEntity.entityId == EntityId && _cachedEntity.IsAlive()) return _cachedEntity;
            var e = world.GetEntity(EntityId) as EntityAlive;
            _cachedEntity = e;
            return e;
        }

        public void Tick(float dt, World world)
        {
            var me = GetEntity(world);
            if (me == null) return;

            var cfg = ModApi.Config;
            if (Time.time - SpawnTime < cfg.SpawnProtectionSec) return;

            if (Time.time >= _nextTargetScan)
            {
                _nextTargetScan = Time.time + 0.5f;
                var newTarget = BotBrain.FindTarget(me, world, cfg);
                if (newTarget != null)
                {
                    _target = newTarget;
                    _loseTargetTimer = 0f;
                    _state = BotBrain.State.Chase;
                }
                else if (_target != null)
                {
                    _loseTargetTimer += 0.5f;
                    float dist = Vector3.Distance(me.position, _target.position);
                    if (_target.IsDead() || !IsValidTarget(_target, cfg) || dist > cfg.LoseTargetRange || _loseTargetTimer > cfg.LoseTargetTimeSec)
                    {
                        _target = null;
                        _state = BotBrain.State.Wander;
                    }
                }
            }

            if (_target != null && _target.IsAlive() && !IsValidTargetDead(_target))
            {
                Vector3 targetPos = _target.position;
                float dist = Vector3.Distance(me.position, targetPos);
                bool canSee = BotBrain.CanSee(me, _target, world, cfg);
                bool inRange = dist <= cfg.AttackRange;

                if (inRange && canSee)
                {
                    _state = BotBrain.State.Attack;
                    BotBrain.FaceTowards(me, targetPos);
                    TryShoot(me, _target, cfg);
                    if (Time.time >= _nextPathRecalc)
                    {
                        _nextPathRecalc = Time.time + cfg.PathRecalcIntervalSec * 2f;
                        BotBrain.Strafe(me, _target);
                    }
                }
                else
                {
                    _state = BotBrain.State.Chase;
                    if (Time.time >= _nextPathRecalc)
                    {
                        _nextPathRecalc = Time.time + cfg.PathRecalcIntervalSec;
                        BotBrain.MoveTo(me, targetPos);
                    }
                    float moved = Vector3.Distance(me.position, _lastPos);
                    if (moved < 0.15f)
                    {
                        if (_stuckSince == 0f) _stuckSince = Time.time;
                        else if (Time.time - _stuckSince > cfg.StuckTimeoutSec)
                        {
                            BotBrain.JumpOrStrafe(me);
                            _stuckSince = 0f;
                            _nextPathRecalc = Time.time + 0.3f;
                        }
                    }
                    else
                    {
                        _stuckSince = 0f;
                        _lastPos = me.position;
                    }
                }
            }
            else
            {
                _state = BotBrain.State.Wander;
                if (Time.time >= _nextWander || Vector3.Distance(me.position, _wanderTarget) < 2f)
                {
                    _nextWander = Time.time + cfg.RandomWanderIntervalSec * (0.7f + (float)new System.Random().NextDouble() * 0.6f);
                    _wanderTarget = BotBrain.PickWanderTarget(me, world, cfg.RandomWanderRadius);
                    BotBrain.MoveTo(me, _wanderTarget);
                }
            }
        }

        bool IsValidTarget(EntityAlive e, BotMod.Config.BotConfig cfg)
        {
            if (e == null || e.IsDead()) return false;
            if (e.entityId == EntityId) return false;
            if (BotManager.Instance.IsBotEntity(e.entityId) && !cfg.BotVsBot) return false;
            if (e is EntityPlayer && !cfg.BotVsPlayer) return false;
            if (e is EntityZombie && !cfg.BotVsZombie) return false;
            if (e is EntityTrader) return false;
            if (e is EntitySupplyCrate) return false;
            return e.IsAlive();
        }
        bool IsValidTargetDead(EntityAlive e) => e == null || e.IsDead() || !e.IsAlive();

        void TryShoot(EntityAlive me, EntityAlive target, BotMod.Config.BotConfig cfg)
        {
            if (Time.time < _nextFire) return;
            _nextFire = Time.time + cfg.FireRateSec * (0.85f + (float)new System.Random().NextDouble() * 0.3f);

            bool headshot = (float)new System.Random().NextDouble() < cfg.HeadshotChance;
            int dmg = cfg.DamagePerShot;
            if (headshot) dmg = Mathf.RoundToInt(dmg * cfg.HeadshotMultiplier);

            float jitter = cfg.AimJitterDegrees;
            if (jitter > 0.01f)
            {
                float missAngle = (float)new System.Random().NextDouble() * jitter;
                float dist = Vector3.Distance(me.position, target.position);
                if ((float)new System.Random().NextDouble() < (missAngle / 12f) * (dist / 20f))
                    return;
            }

            try
            {
                DamageSource ds;
                try { ds = new DamageSourceEntity(EnumDamageSource.External, EnumDamageTypes.Piercing, me.entityId); }
                catch { ds = new DamageSource(EnumDamageSource.External, EnumDamageTypes.Piercing); }

                target.DamageEntity(ds, dmg, headshot, 1f);
                if (target.IsDead())
                {
                    try { BotCombat.OnKilled(me, target); } catch { }
                }
            }
            catch { }
        }

        public string Status(World world)
        {
            var me = world?.GetEntity(EntityId) as EntityAlive;
            string pos = me != null ? me.position.ToString() : "?";
            string tgt = _target != null ? $"{_target.entityId}" : "none";
            return $"Bot {Name} id={EntityId} state={_state} pos={pos} target={tgt} hp={(me!=null?me.Health.ToString():"?")}";
        }
    }
}
