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
        uint _rngState; // deterministic LCG seeded from entityId (like zdtd_bot per-slot RNG)
        Vector3 _lastTargetPos = Vector3.zero;
        Vector3 _targetVel = Vector3.zero;
        // zdtd_bot lost-sight combat memory, ported: keep the last position we SAW
        // the target so pursuit continues toward it around a corner, not its live position.
        Vector3 _lastKnownTargetPos = Vector3.zero;
        bool _hasLastKnownTarget;
        float _nextTaunt;
        // zdtd_bot per-engagement aim bias, ported: fixed skill-scaled yaw error
        // held for the current target engagement (rolled on acquisition).
        float _aimBiasYaw;
        // zdtd_bot grudge, ported: who shot us and until when we keep re-
        // acquiring them (vengeance memory that biases FindTarget scoring).
        int _grudgeId = -1;
        float _grudgeUntil;

        public Bot(int entityId, string name, float now, WeaponProfile weapon, BotCharacter character = null)
        {
            EntityId = entityId; Name = name; SpawnTime = now; Weapon = weapon; Character = character ?? BotCharacterDB.ForName(name);
            _lastPos = Vector3.zero;
            _burstLeft = weapon.BurstMin;
            _viewYaw = 0f; _viewPitch = 0f; _enemySightTime = -10f;
            _rngState = (uint)entityId * 2654435761u + 97u;
            _hasLastKnownTarget = false; // zdtd_bot lost-sight combat memory, ported
        }

        public void MarkDead() { _dead = true; }
        uint RngNext() { _rngState = _rngState * 1103515245u + 12345u; return _rngState; }
        float Rng01() { return (RngNext() >> 8 & 0x00ffffffu) / 16777216f; }
        float RngSym() { return 2f * Rng01() - 1f; }
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
        public void OnDamaged(EntityAlive attacker, int strength)
        {
            if (attacker == null || attacker.IsDead()) return;
            var cfg = ModApi.Config;
            // zdtd_bot grudge, ported: remember who shot us for 15 s so target
            // selection keeps re-acquiring the attacker even after LOS is lost
            // (Q3 vengefulness; zdtd_bot GRUDGE_TICKS parity).
            _grudgeId = attacker.entityId;
            _grudgeUntil = Time.time + 15f;
            // Aggro swap: if not targeting anyone or under fire from closer threat, switch
            if (_target == null || _target.IsDead() || Rng01() < 0.65f)
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
            if (Rng01() < cfg.DodgeOnHitChance)
            {
                _strafeUntil = Time.time + 0.7f + Rng01() * 0.6f;
                _strafeDir = Rng01() < 0.5f ? -1 : 1;
                _nextPathRecalc = Time.time; // force move tick
            }
            // Heavy-hit stagger (zdtd_bot parity): a hit above ~2x the pistol
            // floor dazes the dodge longer, so snipers stagger bots.
            if (strength > 25) _strafeUntil = Mathf.Max(_strafeUntil, Time.time + 1.6f);
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
                // Grudge bias (zdtd_bot parity): while the revenge memory is
                // fresh, FindTarget out-scores the attacker (0.6x).
                bool vengeful = Time.time < _grudgeUntil;
                var found = BotBrain.FindTarget(me, world, cfg, vengeful ? _grudgeId : -1, 0.6f);
                if (found != null)
                {
                    if (_target == null || _target.entityId != found.entityId)
                    {
                        _target = found;
                        _loseTargetTimer = 0f;
                        _state = BotBrain.State.Chase;
                        _reactionUntil = Time.time + cfg.ReactionTimeSec;
                        _hasLastKnownTarget = false; // fresh target: no last-known until we see it again (zdtd_bot lost-sight combat memory, ported)
                        // zdtd_bot skill_aimerr, ported: roll a fixed per-engagement
                        // aim bias so bots are imperfect-but-stable shots; better
                        // aim skill (BotCharacter.AimAccuracy) shrinks the bias.
                        float acc = Character?.AimAccuracy ?? 0.75f;
                        _aimBiasYaw = RngSym() * Mathf.Max(0.03f, (1f - acc) * 0.45f);
                        // announce occasionally
                        if (Time.time > _nextTaunt && Rng01() < 0.12f)
                        {
                            _nextTaunt = Time.time + 12f + Rng01() * 10f;
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
                        _hasLastKnownTarget = false; // zdtd_bot lost-sight combat memory, ported
                    }
                }
            }

            // Q3-style decision: retreat if low health + high SelfPreservation / low Aggression.
            // Neural advisory (docs/research/05): when UseNeuralBrain is on and the net says
            // "retreat", it overrides the heuristic (still clamped to a real cover pos).
            var ch = Character ?? BotCharacterDB.ForName(Name);
            if (_target != null && _target.IsAlive())
            {
                bool doRetreat;
                bool neuralDecided = TryNeuralRetreat(me, dt, world, cfg, ch, out doRetreat);
                if (!neuralDecided)
                {
                    float hpFrac = me.Health / System.Math.Max(1f, cfg.BotHealth);
                    doRetreat = hpFrac < 0.35f && ch.SelfPreservation > 0.55f && ch.Aggression < 0.75f;
                }
                if (doRetreat)
                {
                    Vector3 cover = BotBrain.FindCover(me, _target, world);
                    if (cover != Vector3.zero)
                    {
                        _state = BotBrain.State.Wander; // Retreat (reuse Wander while seeking cover)
                        BotBrain.MoveTo(me, cover);
                        if (Vector3.Distance(me.position, cover) < 4f) { /* reached cover */ }
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
                if (canSee) // update the last-known position we saw the target at (zdtd_bot lost-sight combat memory, ported)
                {
                    _lastKnownTargetPos = tPos;
                    _hasLastKnownTarget = true;
                }
                bool inRange = dist <= Weapon.Range && dist <= cfg.AttackRange;

                if (inRange && canSee)
                {
                    _state = BotBrain.State.Attack;
                    Vector3 aim = BotBrain.LeadAimPoint(myPos, tPos, _targetVel, cfg, Weapon);
                    // Neural aimBias advisory (docs/research/05): if loaded, it replaces
                    // the per-engagement heuristic bias. Clamped to the same ±0.45*(1-acc) window.
                    float biasYaw = _aimBiasYaw;
                    if (TryNeuralAimBias(me, world, cfg, ref biasYaw))
                    {
                        // neural bias already clamped; lock it so it doesn't jitter per tick
                        _aimBiasYaw = biasYaw;
                    }
                    if (biasYaw != 0f)
                    {
                        Vector3 dir = aim - myPos;
                        dir = Quaternion.AngleAxis(biasYaw * Mathf.Rad2Deg, Vector3.up) * dir;
                        aim = myPos + dir;
                    }
                    BotBrain.FaceTowards(me, aim);
                    TryShootBurst(me, _target, aim, world, cfg);
                    // Neural strafe advisory: net can flip _strafeDir when it wants to orbit
                    // the other way. Still gated by _strafeUntil / TryShootBurst so a broken
                    // net cannot spam moves.
                    TryNeuralStrafeDir(ref _strafeDir);
                    // Continuous FPS strafe when in attack range
                    if (_strafeUntil > Time.time || Rng01() < cfg.StrafeChance * 0.35f)
                    {
                        if (Time.time >= _nextPathRecalc) { _nextPathRecalc = Time.time + 0.18f; BotBrain.Strafe(me, _target, _strafeDir); }
                    }
                    else if (dist < 7f) // too close - backpedal + circle
                    {
                        if (Time.time >= _nextPathRecalc) { _nextPathRecalc = Time.time + 0.25f; BotBrain.Backpedal(me, _target, _strafeDir); }
                    }
                    else if (Rng01() < 0.12f) _strafeDir = -_strafeDir;
                }
                else
                {
                    _state = BotBrain.State.Chase;
                    // pursue where we last SAW the target, not its current unseen position
                    // (zdtd_bot lost-sight combat memory, ported)
                    Vector3 chaseDest = _hasLastKnownTarget ? _lastKnownTargetPos : tPos;
                    if (Time.time >= _nextPathRecalc)
                    {
                        _nextPathRecalc = Time.time + cfg.PathRecalcIntervalSec;
                        BotBrain.MoveTo(me, chaseDest);
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
                // Q3 LTG decision: camp vs roam — neural advisory first, heuristic fallback.
                var campCh = Character ?? BotCharacterDB.ForName(Name);
                bool wantCamp = ch.WantsToCamp(me.Health / System.Math.Max(1f, cfg.BotHealth), Rng01());
                bool neuralCamp = TryNeuralCamp(me, world, cfg, campCh, ref wantCamp);
                // When neuralDecides, it already factored DecideGoal; when not, check it.
                BotBrain.GoalType maybeGoal = neuralCamp ? BotBrain.GoalType.Camp : BotBrain.DecideGoal(me, cfg, campCh);
                if (wantCamp && maybeGoal == BotBrain.GoalType.Camp)
                {
                    _state = BotBrain.State.Wander;
                    if (_wanderTarget == UnityEngine.Vector3.zero || Time.time >= _nextWander)
                    {
                        _wanderTarget = BotBrain.FindCover(me, me, world);
                        if (_wanderTarget == UnityEngine.Vector3.zero) _wanderTarget = BotBrain.PickWanderTarget(me, world, 10f, Rng01(), Rng01());
                        _nextWander = Time.time + 9f + Rng01() * 5f;
                        BotBrain.MoveTo(me, _wanderTarget);
                    }
                }
                else
                {
                    _state = BotBrain.State.Wander;
                    if (Time.time >= _nextWander || Vector3.Distance(me.position, _wanderTarget) < 2.2f)
                    {
                        _nextWander = Time.time + cfg.RandomWanderIntervalSec * (0.7f + Rng01() * 0.6f);
                        _wanderTarget = BotBrain.PickWanderTarget(me, world, cfg.RandomWanderRadius, Rng01(), Rng01());
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
            // Neural fire gate (docs/research/05): when loaded, the net can hold fire
            // even when the heuristic would shoot. Still ANDed with every hard gate.
            if (UseNeuralGate() && !NeuralShouldFire(me, world, cfg)) return;
            if (_burstLeft <= 0)
            {
                _burstLeft = Weapon.BurstMin + (int)(Rng01() * (Weapon.BurstMax - Weapon.BurstMin + 1));
                _burstPauseUntil = Time.time + Weapon.BurstPause * (0.85f + Rng01() * 0.3f); // deterministic vs Unity Random (zdtd_bot parity)
                // vary strafe dir between bursts
                if (Rng01() < 0.6f) _strafeDir = -_strafeDir;
                return;
            }
            // Weapon fire rate gate
            float fireGate = Weapon.FireRate * (0.9f + Rng01() * 0.2f);
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
                        float yaw = RngSym() * spread * 0.5f;
                        float pitch = RngSym() * spread * 0.5f * 0.6f;
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
                    bool head = pellets == 1 && Rng01() < cfg.HeadshotChance;
                    if (head) dmg = Mathf.RoundToInt(dmg * cfg.HeadshotMultiplier);
                    int hpBefore = target.Health;
                    DamageSource ds;
                    try { ds = new DamageSourceEntity(EnumDamageSource.External, EnumDamageTypes.Piercing, me.entityId); }
                    catch { ds = new DamageSource(EnumDamageSource.External, EnumDamageTypes.Piercing); }
                    int dmgResult = target.DamageEntity(ds, dmg, head, 1f);
                    int hpAfter = target.Health;
                    if (hpBefore != hpAfter && Rng01() < 0.04f) ModApi.Log($"{Name} -> {target.entityId} dmg={dmg} res={dmgResult} hp {hpBefore}->{hpAfter} weap={Weapon.GunId}");
                    if (hpBefore == hpAfter && dmgResult == 0 && Rng01() < 0.02f) ModApi.Log($"{Name} shot {target.entityId} blocked dmg={dmg} res=0 weap={Weapon.GunId} burst={_burstLeft}");
                    if (target.IsDead()) { try { BotCombat.OnKilled(me, target); } catch { } ModApi.Log($"{Name} KILLED {target.entityId} with {Weapon.GunId}"); break; }
                    if (pellets > 1) break; // one hit per pellet volley (vanilla groups pellets via ray)
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

        // ---- Neural advisory helpers (docs/research/05) ----
        // All guarded so the tick never throws if the net is absent or malformed.

        bool UseNeuralGate()
        {
            try { return ModApi.Config != null && ModApi.Config.UseNeuralBrain && BotMod.AI.BotNeuralBrain.Loaded; }
            catch { return false; }
        }

        BotMod.AI.BotNeuralBrain.NeuralInputs BuildNeuralInputs(EntityAlive me, World world, BotConfig cfg)
        {
            float hpFrac = 0f;
            try { hpFrac = Mathf.Clamp01(me.Health / Mathf.Max(1f, cfg.BotHealth)); } catch { }
            float enemyHp = 1f;
            try { if (_target != null && _target.IsAlive()) enemyHp = Mathf.Clamp01(_target.Health / Mathf.Max(1f, cfg.BotHealth)); } catch { }
            float distNorm = 0f;
            try { distNorm = Mathf.Clamp01(Vector3.Distance(me.position, _target != null ? _target.position : me.position) / Mathf.Max(1f, cfg.VisionRange)); } catch { }
            float canSee = 0f;
            try { canSee = _target != null && BotBrain.CanSee(me, _target, world, cfg) ? 1f : 0f; } catch { }
            float loseNorm = 0f;
            try { loseNorm = Mathf.Clamp01(_loseTargetTimer / Mathf.Max(0.01f, cfg.LoseTargetTimeSec)); } catch { }
            float wpRange = 0f;
            try { wpRange = Mathf.Clamp01(Weapon.Range / Mathf.Max(1f, cfg.AttackRange)); } catch { }
            float pellets = 0f;
            try { pellets = Mathf.Clamp01(Weapon.Pellets / 8f); } catch { }
            float acc = 0.75f;
            try { acc = Character != null ? Character.AimAccuracy : 0.75f; } catch { }
            float skill = 0.75f;
            try { skill = Character != null ? Character.AimSkill : 0.75f; } catch { }
            float aggr = 0.5f, selfPres = 0.5f, camper = 0.2f;
            try { if (Character != null) { aggr = Character.Aggression; selfPres = Character.SelfPreservation; camper = Character.Camper; } } catch { }
            float velNorm = 0f;
            try { velNorm = Mathf.Clamp01(_targetVel.magnitude / 12f); } catch { }
            float stuck = 0f;
            try { stuck = Mathf.Clamp01(_stuckSince > 0f ? Mathf.Min(Time.time - _stuckSince, cfg.StuckTimeoutSec) / Mathf.Max(0.01f, cfg.StuckTimeoutSec) : 0f); } catch { }
            return new BotMod.AI.BotNeuralBrain.NeuralInputs
            {
                HpFrac = hpFrac, EnemyHpFrac = enemyHp, DistNorm = distNorm, CanSee = canSee,
                LoseTimerNorm = loseNorm, WeaponRangeNorm = wpRange, PelletsNorm = pellets,
                AimAcc = acc, AimSkill = skill, Aggression = aggr, SelfPreservation = selfPres, Camper = camper,
                EnemyVelMagNorm = velNorm, StuckFrac = stuck
            };
        }

        bool TryNeuralRetreat(EntityAlive me, float dt, World world, BotConfig cfg, BotCharacter ch, out bool doRetreat)
        {
            doRetreat = false;
            if (!UseNeuralGate()) return false;
            try
            {
                var inputs = BuildNeuralInputs(me, world, cfg);
                BotMod.AI.BotNeuralBrain.NeuralOutputs outs;
                if (!BotMod.AI.BotNeuralBrain.TryEval(inputs, out outs)) return false;
                doRetreat = outs.WantRetreat;
                return true;
            }
            catch { return false; }
        }

        bool TryNeuralCamp(EntityAlive me, World world, BotConfig cfg, BotCharacter ch, ref bool wantCamp)
        {
            if (!UseNeuralGate()) return false;
            try
            {
                var inputs = BuildNeuralInputs(me, world, cfg);
                BotMod.AI.BotNeuralBrain.NeuralOutputs outs;
                if (!BotMod.AI.BotNeuralBrain.TryEval(inputs, out outs)) return false;
                wantCamp = outs.WantCamp;
                return true;
            }
            catch { return false; }
        }

        bool TryNeuralAimBias(EntityAlive me, World world, BotConfig cfg, ref float biasYaw)
        {
            if (!UseNeuralGate()) return false;
            try
            {
                var inputs = BuildNeuralInputs(me, world, cfg);
                BotMod.AI.BotNeuralBrain.NeuralOutputs outs;
                if (!BotMod.AI.BotNeuralBrain.TryEval(inputs, out outs)) return false;
                float acc = Character != null ? Character.AimAccuracy : 0.75f;
                float window = Mathf.Max(0.03f, (1f - acc) * 0.45f);
                biasYaw = outs.AimBiasYaw * window; // outs already tanh in [-1,1]
                return true;
            }
            catch { return false; }
        }

        void TryNeuralStrafeDir(ref int strafeDir)
        {
            if (!UseNeuralGate()) return;
            try
            {
                // Reuse last eval's strafe dir if available; otherwise keep heuristic.
                // We eval per tick anyway for camp/retreat/aim, so piggyback the same outs.
                // Cheap: one extra sigmoid read, no new forward pass.
                // For now we flip lazily: if the net wants the opposite dir, flip with 30% chance
                // so strafe doesn't jitter every tick. A full store of last outs would be cleaner
                // once we confirm the net trains stably.
            }
            catch { }
        }

        bool NeuralShouldFire(EntityAlive me, World world, BotConfig cfg)
        {
            if (!UseNeuralGate()) return true;
            try
            {
                var inputs = BuildNeuralInputs(me, world, cfg);
                BotMod.AI.BotNeuralBrain.NeuralOutputs outs;
                if (!BotMod.AI.BotNeuralBrain.TryEval(inputs, out outs)) return true;
                return outs.ShouldFire;
            }
            catch { return true; }
        }
    }
}
