using System;
using BotMod.AI;
using BotMod.Config;
using UnityEngine;

namespace BotMod.Core
{
    public sealed class Bot
    {
        public int EntityId { get; }
        public string Name { get; }
        public float SpawnTime { get; }
        public WeaponProfile Weapon { get; private set; }
        public BotCharacter Character { get; private set; }
        bool _dead;
        EntityAlive _cachedEntity;
        float _nextTargetScan;
        float _nextPathRecalc;
        float _stuckSince;
        Vector3 _lastPos;
        Vector3 _wanderTarget;
        float _nextWander;
        float _loseTargetTimer;
        EntityAlive _target;
        BotBrain.State _state = BotBrain.State.Wander;

        // FPS combat state
        float _reactionUntil; // can't shoot until this time after acquiring target
        int _burstLeft;
        float _burstPauseUntil;
        float _strafeUntil;
        int _strafeDir = 1;
        float _fireThrottleWaitUntil;
        float _fireThrottleShootUntil;
        float _idealYaw, _idealPitch;
        float _viewYaw, _viewPitch;
        float _viewYawVel, _viewPitchVel;
        float _enemySightTime;
        float _weaponChangeTime;
        Vector3 _lastTargetPos = Vector3.zero;
        Vector3 _targetVel = Vector3.zero;
        float _nextTaunt;

        public Bot(int entityId, string name, float now, WeaponProfile weapon, BotCharacter character = null)
        {
            EntityId = entityId; Name = name; SpawnTime = now; Weapon = weapon; Character = character ?? BotCharacterDB.ForName(name);
            _lastPos = Vector3.zero;
            _burstLeft = weapon.BurstMin;
            _viewYaw = 0f; _viewPitch = 0f; _enemySightTime = -10f;
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
            _cachedEntity = e; return e;
        }

        /// Called from BotPatches when this bot takes damage - FPS dodge + aggro swap
        public void OnDamaged(EntityAlive attacker)
        {
            if (attacker == null || attacker.IsDead()) return;
            var cfg = ModApi.Config;
            // Aggro swap: if not targeting anyone or under fire from closer threat, switch
            if (_target == null || _target.IsDead() || UnityEngine.Random.value < 0.65f)
            {
                var me = _cachedEntity;
                if (me != null && Vector3.Distance(me.position, attacker.position) < cfg.VisionRange * 1.1f)
                {
                    _target = attacker;
                    _loseTargetTimer = 0f;
                    _state = BotBrain.State.Chase;
                    _reactionUntil = Time.time + cfg.ReactionTimeSec * 0.5f; // quicker when shot
                    _nextTargetScan = Time.time + 0.35f;
                }
            }
            // Strafe-dodge
            if (UnityEngine.Random.value < cfg.DodgeOnHitChance)
            {
                _strafeUntil = Time.time + 0.7f + UnityEngine.Random.value * 0.6f;
                _strafeDir = UnityEngine.Random.value < 0.5f ? -1 : 1;
                _nextPathRecalc = Time.time; // force move tick
            }
        }

        public void Tick(float dt, World world)
        {
            var me = GetEntity(world);
            if (me == null) return;
            var cfg = ModApi.Config;
            if (Time.time - SpawnTime < cfg.SpawnProtectionSec) return;

            // Estimate target velocity for leading aim
            if (_target != null && _target.IsAlive())
            {
                Vector3 cur = _target.position;
                if (_lastTargetPos != Vector3.zero)
                    _targetVel = (cur - _lastTargetPos) / Mathf.Max(dt, 0.02f);
                _lastTargetPos = cur;
            }
            else _lastTargetPos = Vector3.zero;

            // Target acquisition (faster on higher difficulty)
            float scanPeriod = Mathf.Lerp(0.55f, 0.22f, cfg.Difficulty / 4f);
            if (Time.time >= _nextTargetScan)
            {
                _nextTargetScan = Time.time + scanPeriod;
                var found = BotBrain.FindTarget(me, world, cfg);
                if (found != null)
                {
                    if (_target == null || _target.entityId != found.entityId)
                    {
                        _target = found;
                        _loseTargetTimer = 0f;
                        _state = BotBrain.State.Chase;
                        _reactionUntil = Time.time + cfg.ReactionTimeSec;
                        // announce occasionally
                        if (Time.time > _nextTaunt && UnityEngine.Random.value < 0.12f)
                        {
                            _nextTaunt = Time.time + 12f + UnityEngine.Random.value * 10f;
                            ModApi.Log($"{Name} acquired target #{found.entityId}");
                        }
                    }
                }
                else if (_target != null)
                {
                    _loseTargetTimer += scanPeriod;
                    float dist = Vector3.Distance(me.position, _target.position);
                    if (_target.IsDead() || !IsValidTarget(_target, cfg) || dist > cfg.LoseTargetRange || _loseTargetTimer > cfg.LoseTargetTimeSec || !BotBrain.CanSee(me, _target, world, cfg) && dist > 18f)
                    {
                        _target = null; _state = BotBrain.State.Wander;
                    }
                }
            }

            // Q3-style decision: retreat if low health + high SelfPreservation / low Aggression
            var ch = Character ?? BotCharacterDB.ForName(Name);
            if (_target != null && _target.IsAlive())
            {
                float hpFrac = me.Health / System.Math.Max(1f, cfg.BotHealth);
                if (hpFrac < 0.35f && ch.SelfPreservation > 0.55f && ch.Aggression < 0.75f)
                {
                    Vector3 cover = BotBrain.FindCover(me, _target, world);
                    if (cover != Vector3.zero)
                    {
                        _state = BotBrain.State.Wander; // Retreat (reuse Wander while seeking cover)
                        BotBrain.MoveTo(me, cover);
                        // heal-ish: don't shoot while retreating
                        if (Vector3.Distance(me.position, cover) < 4f) { /* reached cover */ }
                        // still tick but skip attack this frame
                    }
                }
            }
            if (_target != null && _target.IsAlive() && !IsDeadTgt(_target))
            {
                Vector3 tPos = _target.position;
                Vector3 myPos = me.position;
                float dist = Vector3.Distance(myPos, tPos);
                bool canSee = BotBrain.CanSee(me, _target, world, cfg);
                if (canSee) _enemySightTime = Time.time;
                bool inRange = dist <= Weapon.Range && dist <= cfg.AttackRange;

                if (inRange && canSee)
                {
                    _state = BotBrain.State.Attack;
                    Vector3 aim = BotBrain.LeadAimPoint(myPos, tPos, _targetVel, cfg, Weapon);
                    BotBrain.FaceTowards(me, aim);
                    TryShootBurst(me, _target, aim, world, cfg);
                    // Continuous FPS strafe when in attack range
                    if (_strafeUntil > Time.time || UnityEngine.Random.value < cfg.StrafeChance * 0.35f)
                    {
                        if (Time.time >= _nextPathRecalc) { _nextPathRecalc = Time.time + 0.18f; BotBrain.Strafe(me, _target, _strafeDir); }
                    }
                    else if (dist < 7f) // too close - backpedal + circle
                    {
                        if (Time.time >= _nextPathRecalc) { _nextPathRecalc = Time.time + 0.25f; BotBrain.Backpedal(me, _target, _strafeDir); }
                    }
                    else if (UnityEngine.Random.value < 0.12f) _strafeDir = -_strafeDir;
                }
                else
                {
                    _state = BotBrain.State.Chase;
                    if (Time.time >= _nextPathRecalc)
                    {
                        _nextPathRecalc = Time.time + cfg.PathRecalcIntervalSec;
                        BotBrain.MoveTo(me, tPos);
                    }
                    float moved = Vector3.Distance(myPos, _lastPos);
                    if (moved < 0.18f)
                    {
                        if (_stuckSince == 0f) _stuckSince = Time.time;
                        else if (Time.time - _stuckSince > cfg.StuckTimeoutSec) { BotBrain.JumpOrStrafe(me); _stuckSince = 0f; _nextPathRecalc = Time.time + 0.2f; }
                    }
                    else { _stuckSince = 0f; _lastPos = myPos; }
                }
            }
            else
            {
                // Q3 LTG decision: camp vs roam (BotWantsToRetreat/Camp)
                var campCh = Character ?? BotCharacterDB.ForName(Name);
                if (ch.WantsToCamp(me.Health / System.Math.Max(1f, cfg.BotHealth)) && BotBrain.DecideGoal(me, cfg, campCh) == BotBrain.GoalType.Camp)
                {
                    _state = BotBrain.State.Wander;
                    if (_wanderTarget == UnityEngine.Vector3.zero || Time.time >= _nextWander)
                    {
                        _wanderTarget = BotBrain.FindCover(me, me, world);
                        if (_wanderTarget == UnityEngine.Vector3.zero) _wanderTarget = BotBrain.PickWanderTarget(me, world, 10f);
                        _nextWander = Time.time + 9f + UnityEngine.Random.value * 5f;
                        BotBrain.MoveTo(me, _wanderTarget);
                    }
                }
                else
                {
                    _state = BotBrain.State.Wander;
                    if (Time.time >= _nextWander || Vector3.Distance(me.position, _wanderTarget) < 2.2f)
                    {
                        _nextWander = Time.time + cfg.RandomWanderIntervalSec * (0.7f + UnityEngine.Random.value * 0.6f);
                        _wanderTarget = BotBrain.PickWanderTarget(me, world, cfg.RandomWanderRadius);
                        BotBrain.MoveTo(me, _wanderTarget);
                    }
                }
            }
        }

        bool IsValidTarget(EntityAlive e, BotConfig cfg)
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
        bool IsDeadTgt(EntityAlive e) => e == null || e.IsDead() || !e.IsAlive();

        void TryShootBurst(EntityAlive me, EntityAlive target, Vector3 aimPos, World world, BotConfig cfg)
        {
            if (Time.time < _reactionUntil) return;
            if (Time.time < _burstPauseUntil) return;
            if (_burstLeft <= 0)
            {
                _burstLeft = UnityEngine.Random.Range(Weapon.BurstMin, Weapon.BurstMax + 1);
                _burstPauseUntil = Time.time + Weapon.BurstPause * (0.85f + UnityEngine.Random.value * 0.3f);
                // vary strafe dir between bursts
                if (UnityEngine.Random.value < 0.6f) _strafeDir = -_strafeDir;
                return;
            }
            // Weapon fire rate gate
            float fireGate = Weapon.FireRate * (0.9f + UnityEngine.Random.value * 0.2f);
            // Use a per-bot accumulator instead of global _nextFire for burst fidelity
            // Reuse burstPauseUntil as fire gate when bursting
            if (Time.time < _burstPauseUntil - Weapon.BurstPause + fireGate && _burstLeft != Weapon.BurstMin)
            {
                // inside burst - respect fire rate via short pause
                // encode as: we set _burstPauseUntil to next shot time within burst
            }
            // Pellet/shot loop with per-pellet spread
            try
            {
                int pellets = Mathf.Max(1, Weapon.Pellets);
                for (int p = 0; p < pellets; p++)
                {
                    Vector3 shotAim = aimPos;
                    float spread = Weapon.SpreadDeg + cfg.AimJitterDegrees * (1f - cfg.Difficulty * 0.16f);
                    spread = Mathf.Max(0.2f, spread);
                    // Difficulty reduces spread heavily
                    float diffScale = 1f - cfg.Difficulty * 0.19f;
                    spread *= diffScale;
                    if (spread > 0.4f)
                    {
                        float yaw = (UnityEngine.Random.value - 0.5f) * spread;
                        float pitch = (UnityEngine.Random.value - 0.5f) * spread * 0.6f;
                        Vector3 dir = (shotAim - (me.position + Vector3.up * 1.45f)).normalized;
                        // apply yaw/pitch
                        Quaternion rot = Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.AngleAxis(pitch, Vector3.Cross(dir, Vector3.up).normalized);
                        dir = rot * dir;
                        // ray check per pellet
                        Vector3 from = me.position + Vector3.up * 1.45f;
                        // miss if LOS blocked to aim point - but we already checked canSee
                    }
                    // Damage per pellet: spread damage for shotguns
                    int dmg = pellets > 1 ? Mathf.Max(3, Weapon.Damage) : Weapon.Damage;
                    bool head = pellets == 1 && UnityEngine.Random.value < cfg.HeadshotChance;
                    if (head) dmg = Mathf.RoundToInt(dmg * cfg.HeadshotMultiplier);
                    int hpBefore = target.Health;
                    DamageSource ds;
                    try { ds = new DamageSourceEntity(EnumDamageSource.External, EnumDamageTypes.Piercing, me.entityId); }
                    catch { ds = new DamageSource(EnumDamageSource.External, EnumDamageTypes.Piercing); }
                    int dmgResult = target.DamageEntity(ds, dmg, head, 1f);
                    int hpAfter = target.Health;
                    if (hpBefore != hpAfter && UnityEngine.Random.value < 0.04f) ModApi.Log($"{Name} -> {target.entityId} dmg={dmg} res={dmgResult} hp {hpBefore}->{hpAfter} weap={Weapon.GunId}");
                    if (hpBefore == hpAfter && dmgResult == 0 && UnityEngine.Random.value < 0.02f) ModApi.Log($"{Name} shot {target.entityId} blocked dmg={dmg} res=0 weap={Weapon.GunId} burst={_burstLeft}");
                    if (target.IsDead()) { try { BotCombat.OnKilled(me, target); } catch { } ModApi.Log($"{Name} KILLED {target.entityId} with {Weapon.GunId}"); break; }
                    if (pellets > 1) break; // only one target hit per pellet grouping - avoid multi-hit on same frame (vanilla handles pellets via ray)
                    // For multi-pellet we coalesce to one hit with scaled damage to avoid insta-kill
                    if (pellets > 1) { target.DamageEntity(ds, dmg * (pellets - 1), false, 0.5f); break; }
                }
            }
            catch { }
            _burstLeft--;
            _burstPauseUntil = Time.time + Weapon.FireRate * 0.95f;
            if (_burstLeft <= 0) _burstPauseUntil = Time.time + Weapon.BurstPause;
        }

        public string Status(World world)
        {
            var me = world?.GetEntity(EntityId) as EntityAlive;
            string pos = me != null ? me.position.ToString() : "?";
            string tgt = _target != null ? $"{_target.entityId}" : "none";
            return $"Bot {Name} [{Weapon.GunId}] id={EntityId} state={_state} pos={pos} tgt={tgt} hp={(me!=null?me.Health.ToString():"?")} burst={_burstLeft}";
        }
    }
}
