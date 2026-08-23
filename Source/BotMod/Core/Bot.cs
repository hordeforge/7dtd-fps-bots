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
        /// <summary>Base bot name used as the team-assignments key ([Bot] Grunt_42 ->
        /// Grunt), computed once so AreAllies/GetTeamId stay allocation-free.</summary>
        public string TeamKey { get; }
        public float SpawnTime { get; }
        public WeaponProfile Weapon { get; private set; }
        public BotCharacter Character { get; private set; }
        bool _dead;
        EntityAlive _cachedEntity;
        float _nextTargetScan;
        float _nextPathRecalc;
        float _stuckSince;
        Vector3 _lastPos;
        // When GetEntity last returned null (grace for transient entity-dict lookup misses)
        float _missingSince;
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
        uint _rngState; // deterministic LCG seeded from entityId (like zdtd_bot per-slot RNG)
        Vector3 _lastTargetPos = Vector3.zero;
        Vector3 _targetVel = Vector3.zero;
        // zdtd_bot lost-sight combat memory, ported: keep the last position we SAW
        // the target so pursuit continues toward it around a corner, not its live position.
        Vector3 _lastKnownTargetPos = Vector3.zero;
        bool _hasLastKnownTarget;
        float _nextCoverRoute;
        float _nextTaunt;
        // zdtd_bot per-engagement aim bias, ported: fixed skill-scaled yaw error
        // held for the current target engagement (rolled on acquisition).
        float _aimBiasYaw;
        // zdtd_bot grudge, ported: who shot us and until when we keep re-
        // acquiring them (vengeance memory that biases FindTarget scoring).
        int _grudgeId = -1;
        float _grudgeUntil;
        // zdtd_bot ammo pacing, ported: rounds left in the magazine and the
        // reload window (WeaponProfile.MagSize/ReloadSec).
        int _ammo;
        float _reloadUntil = -10f;
        // zdtd_bot camp hold, ported back: when a camper decides to camp, hold
        // position for a few seconds and slowly sweep the facing (Q3/Doom3 LTG)
        // instead of just standing still or drifting. `_campHoldUntil` is when the
        // hold ends; the sweep advances _campYaw each tick.
        float _campHoldUntil;
        float _campYaw;
        // zdtd_bot dodge phase, ported back: on-hit dodge first backpedals for a
        // few ticks then flips to a hard strafe on the randomized direction (Q3
        // evasive dodge), rather than a flat strafe-only window.
        int _dodgeTicks;      // ticks left in the dodge
        int _dodgeBackRemain; // ticks of backpedal still left (then flip strafe)
        // Per-tick memoization: one neural forward pass and one CanSee raycast max.
        // Retreat, fire-gate and movement all consumed separate evals (each with its
        // own LOS raycast inside BuildNeuralInputs) for identical inputs within a tick.
        bool _neuralEvalDone;
        bool _neuralOk;
        BotMod.AI.BotNeuralBrain.NeuralOutputs _neuralOuts;
        bool _canSeeDone;
        bool _canSeeVal;
        // Squad flanking scan cadence: FlankAway walks every bot's entity each call,
        // so it runs at 4 Hz instead of once per frame (flip latency <= 0.25 s).
        float _nextFlankScan;

        public Bot(int entityId, string name, float now, WeaponProfile weapon, BotCharacter character = null)
        {
            EntityId = entityId; Name = name; SpawnTime = now; Weapon = weapon; Character = character ?? BotCharacterDB.ForName(name);
            TeamKey = BotManager.BaseName(name); // frozen: names never change after spawn
            _lastPos = Vector3.zero;
            _burstLeft = weapon.BurstMin;
            _rngState = (uint)entityId * 2654435761u + 97u;
            _hasLastKnownTarget = false; // zdtd_bot lost-sight combat memory, ported
            _ammo = weapon.MagSize; // zdtd_bot ammo pacing, ported
        }

        public void MarkDead() { _dead = true; }
        uint RngNext() { _rngState = _rngState * 1103515245u + 12345u; return _rngState; }
        float Rng01() { return (RngNext() >> 8 & 0x00ffffffu) / 16777216f; }
        float RngSym() { return 2f * Rng01() - 1f; }
        public bool IsDeadOrUnloaded(World world)
        {
            if (_dead) return true;
            if (world == null) return false;
            // Trust a cached alive entity even if a fresh lookup glitches (entities can
            // transiently miss the world entity dict). Prevents bots being dropped mid-fight.
            if (_cachedEntity != null && _cachedEntity.entityId == EntityId && _cachedEntity.IsAlive() && !_cachedEntity.IsDead()) return false;
            var e = world.GetEntity(EntityId) as EntityAlive;
            if (e == null)
            {
                if (_missingSince == 0f) _missingSince = Time.time;
                if (Time.time - _missingSince < 6f) return false;
                return true;
            }
            _missingSince = 0f;
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
                // Enforce the vs-class gates on the aggro path too: without this a hit
                // from a disabled class (BotVsPlayer/Zombie=false) adopts itself as
                // _target and the bot shoots back until the next scan drops it.
                if (me != null && IsValidTarget(attacker, cfg) && Vector3.Distance(me.position, attacker.position) < cfg.VisionRange * 1.1f)
                {
                    _target = attacker;
                    _loseTargetTimer = 0f;
                    _state = BotBrain.State.Chase;
                    _reactionUntil = Time.time + cfg.ReactionTimeSec * 0.5f; // quicker when shot
                    _nextTargetScan = Time.time + 0.35f;
                }
            }
            // Strafe-dodge (zdtd_bot phased dodge, ported back): a dodge first
            // backpedals for a few ticks (evade the incoming shot direction), then
            // flips to a hard strafe on a randomized direction. Richer than a flat
            // strafe-only window and reads as a real FPS hop.
            if (Rng01() < cfg.DodgeOnHitChance)
            {
                _dodgeTicks = 12;          // ~0.6 s dodge
                _dodgeBackRemain = 4;      // first ~0.2 s is a backpedal
                _strafeDir = Rng01() < 0.5f ? -1 : 1;
                _strafeUntil = Time.time + 0.7f + Rng01() * 0.6f;
                _nextPathRecalc = Time.time; // force move tick
            }
            // Heavy-hit stagger (zdtd_bot parity): a hit above ~2x the pistol
            // floor dazes the dodge longer, so snipers stagger bots.
            if (strength > 25) _strafeUntil = Mathf.Max(_strafeUntil, Time.time + 1.6f);
        }

        public void Tick(float dt, World world)
        {
            _neuralEvalDone = false;
            _canSeeDone = false;
            var me = GetEntity(world);
            if (me == null) return;
            var cfg = ModApi.Config;
            if (Time.time - SpawnTime < cfg.SpawnProtectionSec) return;

            UpdateTargetVelocity(dt);
            AcquireTarget(me, world, cfg);

            var ch = Character ?? BotCharacterDB.ForName(Name);
            if (_target != null && _target.IsAlive()) RetreatToCover(me, world, cfg, ch);

            if (_target != null && _target.IsAlive() && !IsDeadTgt(_target))
            {
                EngageTarget(me, world, cfg);
                return;
            }
            IdleWanderOrCamp(me, world, cfg, ch);
        }

        /// <summary>Estimate target velocity for leading aim.</summary>
        void UpdateTargetVelocity(float dt)
        {
            if (_target != null && _target.IsAlive())
            {
                Vector3 cur = _target.position;
                if (_lastTargetPos != Vector3.zero)
                    _targetVel = (cur - _lastTargetPos) / Mathf.Max(dt, 0.02f);
                _lastTargetPos = cur;
            }
            else _lastTargetPos = Vector3.zero;
        }

        /// <summary>Target acquisition (faster on higher difficulty), with grudge bias
        /// (zdtd_bot parity) and the lose-target check when FindTarget comes up empty.</summary>
        void AcquireTarget(EntityAlive me, World world, BotConfig cfg)
        {
            float scanPeriod = Mathf.Lerp(0.55f, 0.22f, cfg.Difficulty / 4f);
            if (Time.time < _nextTargetScan) return;
            _nextTargetScan = Time.time + scanPeriod;
            // Grudge bias (zdtd_bot parity): while the revenge memory is
            // fresh, FindTarget out-scores the attacker (0.6x).
            bool vengeful = Time.time < _grudgeUntil;
            var found = BotBrain.FindTarget(me, world, cfg, vengeful ? _grudgeId : -1, 0.6f);
            if (found != null)
            {
                AdoptTarget(found, cfg);
            }
            else if (_target != null)
            {
                _loseTargetTimer += scanPeriod;
                float dist = Vector3.Distance(me.position, _target.position);
                if (_target.IsDead() || !IsValidTarget(_target, cfg) || dist > cfg.LoseTargetRange || _loseTargetTimer > cfg.LoseTargetTimeSec || !TargetVisible(me, world, cfg) && dist > 18f)
                {
                    _target = null; _state = BotBrain.State.Wander;
                    _hasLastKnownTarget = false; // zdtd_bot lost-sight combat memory, ported
                }
            }
        }

        /// <summary>Switch to a newly found target: reset timers, roll the per-engagement
        /// aim bias (zdtd_bot skill_aimerr), occasionally announce the acquire.</summary>
        void AdoptTarget(EntityAlive found, BotConfig cfg)
        {
            if (_target != null && _target.entityId == found.entityId) return;
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

        /// <summary>Q3-style decision: retreat toward cover on low health with high
        /// SelfPreservation / low Aggression. Neural advisory (docs/research/05):
        /// when a net is loaded it drives retreat for every engagement (bots,
        /// zombies, players); heuristic is the fallback.</summary>
        void RetreatToCover(EntityAlive me, World world, BotConfig cfg, BotCharacter ch)
        {
            bool doRetreat;
            if (UseNeuralGate() && TryNeuralOnce(me, world, cfg))
            {
                doRetreat = _neuralOuts.WantRetreat;
            }
            else
            {
                float hpFrac = me.Health / System.Math.Max(1f, cfg.BotHealth);
                doRetreat = hpFrac < 0.35f && ch.SelfPreservation > 0.55f && ch.Aggression < 0.75f;
            }
            // FPS finish-the-kill: if the enemy is also critically wounded, commit instead of
            // retreating. Prevents a mutual-retreat stalemate where two low-HP bots both back
            // off forever and never finish the fight.
            if (doRetreat)
            {
                try
                {
                    float enemyFrac = _target.Health / System.Math.Max(1f, cfg.BotHealth);
                    if (enemyFrac <= 0.4f) doRetreat = false;
                }
                catch { }
            }
            if (!doRetreat) return;
            // Cover search costs 8 LOS raycasts + ground scans, so it runs on the
            // shared path-recalc cadence like every other MoveTo branch instead of
            // every frame while retreating.
            if (Time.time < _nextPathRecalc) return;
            _nextPathRecalc = Time.time + cfg.PathRecalcIntervalSec;
            Vector3 cover = BotBrain.FindCover(me, _target, world);
            if (cover == Vector3.zero) return;
            _state = BotBrain.State.Wander; // Retreat (reuse Wander while seeking cover)
            BotBrain.MoveTo(me, cover);
        }
        /// <summary>Live-target combat: refresh last-seen memory, then either fight
        /// at range or pursue the last-seen position.</summary>
        void EngageTarget(EntityAlive me, World world, BotConfig cfg)
        {
            Vector3 tPos = _target.position;
            Vector3 myPos = me.position;
            float dist = Vector3.Distance(myPos, tPos);
            bool canSee = TargetVisible(me, world, cfg);
            if (canSee) // update the last-known position we saw the target at (zdtd_bot lost-sight combat memory, ported)
            {
                _lastKnownTargetPos = tPos;
                _hasLastKnownTarget = true;
            }
            // Weapon-aware effective attack range: short/close guns stay at AttackRange,
            // long guns (sniper/AK) get to use more of their real range advantage.
            float effRange = Mathf.Min(Weapon.Range, cfg.AttackRange + Mathf.Max(0f, Weapon.Range - 55f) * 0.7f);
            bool inRange = dist <= effRange;

            if (inRange && canSee) AttackInRange(me, world, cfg, dist, effRange);
            else ChaseTarget(me, world, cfg, canSee, tPos, myPos);
        }

        /// <summary>In-range combat (target is visible by the caller's contract): aim
        /// (with per-engagement bias), fire the gated burst, then move via the neural
        /// policy or the Q3 fallback movement.</summary>
        void AttackInRange(EntityAlive me, World world, BotConfig cfg, float dist, float effRange)
        {
            Vector3 tPos = _target.position;
            Vector3 myPos = me.position;
            _state = BotBrain.State.Attack;
            Vector3 aim = BotBrain.LeadAimPoint(myPos, tPos, _targetVel, cfg, Weapon);
            // Neural aimBias advisory drives every engagement (bot-vs-bot, bot-vs-zombie,
            // bot-vs-player) so the evolved brain is exercised live. Classic is fallback.
            // Reuses the tick's cached eval (see TryNeuralOnce).
            if (UseNeuralGate() && TryNeuralOnce(me, world, cfg))
            {
                float acc = Character != null ? Character.AimAccuracy : 0.75f;
                float window = Mathf.Max(0.03f, (1f - acc) * 0.45f);
                _aimBiasYaw = _neuralOuts.AimBiasYaw * window; // outs already tanh in [-1,1]
            }
            if (_aimBiasYaw != 0f)
            {
                Vector3 dir = aim - myPos;
                dir = Quaternion.AngleAxis(_aimBiasYaw * Mathf.Rad2Deg, Vector3.up) * dir;
                aim = myPos + dir;
            }
            BotBrain.FaceTowards(me, aim);
            // Neural fire gate: drives every engagement, not just vs players, so the
            // evolved brain's fire decision actually matters against bots and zombies.
            // Reuses the tick's cached eval (see TryNeuralOnce).
            bool wantToFire = true;
            if (UseNeuralGate() && TryNeuralOnce(me, world, cfg)) wantToFire = _neuralOuts.ShouldFire;
            TryShootBurst(me, _target, aim, world, cfg, wantToFire);
            // R10 neural movement: when the evolved brain is loaded it drives the
            // 2D velocity directly (retreat -> forward, strafe -> lateral, camp ->
            // hold), matching combat_sim. The hardcoded Q3 strafe/dodge logic below
            // is the fallback when the brain is off or broken.
            bool neuralMoved = false;
            if (UseNeuralGate() && TryNeuralOnce(me, world, cfg))
            {
                float retreat = _neuralOuts.RetreatLogit;
                float strafe = _neuralOuts.StrafeLogit;
                float fwd = 1.2f * (1f - 2f * retreat);
                float lat = (strafe - 0.5f) * 2.4f;
                if (_neuralOuts.WantCamp && me.Health > 55f && dist > 18f) fwd *= 0.15f;
                Vector3 toT = tPos - me.position; toT.y = 0;
                if (toT.sqrMagnitude > 0.001f)
                {
                    toT.Normalize();
                    Vector3 perp = Vector3.Cross(Vector3.up, toT);
                    Vector3 dir = toT * fwd + perp * lat;
                    if (dir.sqrMagnitude > 0.001f)
                    {
                        BotBrain.MoveDir(me, dir);
                        _strafeDir = _neuralOuts.StrafeDir;
                        neuralMoved = true;
                    }
                }
            }
            if (!neuralMoved) AttackMoveFallback(me, world, cfg, dist, effRange);
        }

        /// <summary>Q3 fallback movement while in attack range (net off/broken or no
        /// move decided): squad flanking, cover-while-reloading, phased dodge, and
        /// weapon-aware standoff (strafe outside ~35% of effective range, backpedal inside).</summary>
        void AttackMoveFallback(EntityAlive me, World world, BotConfig cfg, float dist, float effRange)
        {
            // Squad flanking: if another bot is strafing the same target in the same
            // direction, flip mine so the team splits around the target (FPS handshake)
            // instead of clumping on one side. FlankAway walks every other bot's
            // entity, so it scans on a 0.25 s cadence rather than every frame.
            if (Time.time >= _nextFlankScan)
            {
                _nextFlankScan = Time.time + 0.25f;
                if (FlankAway(me, world, _target, _strafeDir)) _strafeDir = -_strafeDir;
            }
            // Cover-while-reloading (CS-bot lineage, docs/oss-fps-bot-survey.md):
            // an empty mag with a live visible target seeks cover instead of
            // standing in the open. Gated by the path-recalc cadence.
            if (Time.time < _reloadUntil && Time.time >= _nextPathRecalc)
            {
                Vector3 cover = BotBrain.FindCover(me, _target, world);
                if (cover != Vector3.zero)
                {
                    _nextPathRecalc = Time.time + 0.6f;
                    BotBrain.MoveTo(me, cover);
                }
                else
                {
                    BotBrain.Strafe(me, _target, _strafeDir);
                }
                return;
            }
            // Continuous FPS strafe when in attack range.
            float tooClose = Mathf.Max(6f, effRange * 0.35f);
            // Phased dodge (zdtd_bot dodge, ported back): while dodging, first
            // backpedal away, then flip to a hard strafe on the randomized dir.
            if (_dodgeTicks > 0)
            {
                _dodgeTicks--;
                if (Time.time >= _nextPathRecalc)
                {
                    if (_dodgeBackRemain > 0) { _dodgeBackRemain--; BotBrain.Backpedal(me, _target, _strafeDir); }
                    else { _strafeDir = -_strafeDir; BotBrain.Strafe(me, _target, _strafeDir); }
                    _nextPathRecalc = Time.time + 0.12f;
                }
            }
            else if (_strafeUntil > Time.time || Rng01() < cfg.StrafeChance * 0.35f)
            {
                if (dist > tooClose)
                {
                    if (Time.time >= _nextPathRecalc) { _nextPathRecalc = Time.time + 0.18f; BotBrain.Strafe(me, _target, _strafeDir); }
                }
                else // inside standoff - backpedal to reopen range
                {
                    if (Time.time >= _nextPathRecalc) { _nextPathRecalc = Time.time + 0.25f; BotBrain.Backpedal(me, _target, _strafeDir); }
                }
            }
            else if (dist < tooClose)
            {
                if (Time.time >= _nextPathRecalc) { _nextPathRecalc = Time.time + 0.25f; BotBrain.Backpedal(me, _target, _strafeDir); }
            }
            else if (Rng01() < 0.12f) _strafeDir = -_strafeDir;
        }

        /// <summary>Pursue where we last SAW the target, not its current unseen position
        /// (zdtd_bot lost-sight combat memory, ported): route through nearby cover when
        /// healthy, and juke perpendicular when stuck against an obstacle.</summary>
        void ChaseTarget(EntityAlive me, World world, BotConfig cfg, bool canSee, Vector3 tPos, Vector3 myPos)
        {
            _state = BotBrain.State.Chase;
            Vector3 chaseDest = _hasLastKnownTarget ? _lastKnownTargetPos : tPos;
            if (Time.time >= _nextPathRecalc)
            {
                _nextPathRecalc = Time.time + cfg.PathRecalcIntervalSec;
                // FPS cover advance: when healthy and the target is out of sight, route
                // through a nearby cover point between us and the target (peek from cover)
                // rather than walking straight into the open. Gated by a cooldown so it
                // doesn't jitter.
                bool routeCover = !canSee && me.Health > cfg.BotHealth * 0.55f && Time.time >= _nextCoverRoute;
                if (routeCover)
                {
                    Vector3 cover = BotBrain.FindCover(me, _target, world);
                    if (cover != Vector3.zero && Vector3.Distance(cover, chaseDest) > Vector3.Distance(me.position, chaseDest) * 0.72f)
                    {
                        BotBrain.MoveTo(me, cover);
                        _nextCoverRoute = Time.time + 3f + Rng01() * 3f;
                    }
                    else BotBrain.MoveTo(me, chaseDest);
                }
                else BotBrain.MoveTo(me, chaseDest);
            }
            float moved = Vector3.Distance(myPos, _lastPos);
            if (moved < 0.18f)
            {
                if (_stuckSince == 0f) _stuckSince = Time.time;
                else if (Time.time - _stuckSince > cfg.StuckTimeoutSec)
                {
                    // Stuck perpendicular-juke (zdtd_bot memory-juke, ported back):
                    // offset perpendicular to the obstacle so the bot goes AROUND its
                    // chase target instead of jumping/strafing randomly. When no target
                    // or the offset is tiny, fall back to the old JumpOrStrafe nudge.
                    Vector3 from = myPos;
                    Vector3 toward = chaseDest - from; toward.y = 0;
                    bool juked = false;
                    if (toward.sqrMagnitude > 0.04f)
                    {
                        toward.Normalize();
                        Vector3 perp = Vector3.Cross(Vector3.up, toward) * ((EntityId & 1) == 0 ? 3f : -3f);
                        Vector3 jukePos = from + perp;
                        try { BotBrain.MoveTo(me, jukePos); juked = true; } catch { }
                    }
                    if (!juked) { try { BotBrain.JumpOrStrafe(me); } catch { } }
                    _stuckSince = 0f; _nextPathRecalc = Time.time + 0.2f;
                }
            }
            else { _stuckSince = 0f; _lastPos = myPos; }
        }

        /// <summary>No live target: clear a dead one and rescan immediately so the bot
        /// switches enemies without dead-target linger (FPS flow), then take the Q3 LTG
        /// idle decision between camping (hold + facing sweep) and roaming/hunting.</summary>
        void IdleWanderOrCamp(EntityAlive me, World world, BotConfig cfg, BotCharacter ch)
        {
            if (_target != null && IsDeadTgt(_target))
            {
                _target = null;
                _hasLastKnownTarget = false;
                _nextTargetScan = 0f; // force re-acquisition this tick
            }
            // Q3 LTG decision: idle camp-vs-roam stays heuristic on purpose.
            // The net's camp output is only consumed inside engagements
            // (attack movement); zombies must always pull idle bots out of
            // cover, so no neural gate sits on this branch.
            float idleHp = me.Health / System.Math.Max(1f, cfg.BotHealth);
            if (ch.WantsToCamp(idleHp, Rng01()) && BotBrain.WantsIdleCamp(me, cfg, ch))
            {
                _state = BotBrain.State.Wander;
                // Camp hold + facing sweep (zdtd_bot camp, ported back): instead of
                // drifting to a wander point, pick a spot once, hold there for a few
                // seconds and slowly sweep the facing (Q3/Doom3 LTG camper).
                if (_campHoldUntil < Time.time && Time.time >= _nextWander)
                {
                    _nextWander = Time.time + 9f + Rng01() * 5f;
                    _campHoldUntil = Time.time + 4f + Rng01() * 3f; // hold ~4-7 s
                    _campYaw = 0f;
                    _wanderTarget = BotBrain.FindCover(me, me, world);
                    if (_wanderTarget == UnityEngine.Vector3.zero) _wanderTarget = BotBrain.PickWanderTarget(me, world, 10f, Rng01(), Rng01());
                    BotBrain.MoveTo(me, _wanderTarget);
                }
                else if (Vector3.Distance(me.position, _wanderTarget) < 3f || _campHoldUntil > Time.time)
                {
                    // Holding: sweep the facing slowly instead of standing static.
                    if (Time.time >= _nextPathRecalc)
                    {
                        _nextPathRecalc = Time.time + 0.2f;
                        _campYaw += 0.05f;
                        try { me.SetLookPosition(me.position + Quaternion.Euler(0, _campYaw * Mathf.Rad2Deg, 0) * Vector3.forward); } catch { }
                    }
                }
            }
            else
            {
                _state = BotBrain.State.Wander;
                if (Time.time >= _nextWander || Vector3.Distance(me.position, _wanderTarget) < 2.2f)
                {
                    _nextWander = Time.time + cfg.RandomWanderIntervalSec * (0.7f + Rng01() * 0.6f);
                    // Active hunt: prefer the nearest other bot/player so bots converge and
                    // fight; fall back to random wander when none is close (FPS combat seeking).
                    Vector3 seek = SeekNearestEnemy(me, world, cfg, cfg.VisionRange * 3f);
                    if (seek != Vector3.zero) _wanderTarget = seek;
                    else _wanderTarget = BotBrain.PickWanderTarget(me, world, cfg.RandomWanderRadius, Rng01(), Rng01());
                    BotBrain.MoveTo(me, _wanderTarget);
                }
            }
        }

        bool IsValidTarget(EntityAlive e, BotConfig cfg)
        {
            if (e == null || e.IsDead()) return false;
            if (e.entityId == EntityId) return false;
            bool eIsBot = BotManager.Instance.IsBotEntity(e.entityId);
            if (eIsBot && !cfg.BotVsBot) return false;
            // A bot can still run a trader body when the configured entity class
            // falls back to npcTraderJoel, so don't auto-reject EntityTrader for bots.
            if (!eIsBot && e is EntityTrader) return false;
            if (e is EntityPlayer && !cfg.BotVsPlayer) return false;
            if (e is EntityZombie && !cfg.BotVsZombie) return false;
            if (e is EntitySupplyCrate) return false;
            return e.IsAlive();
        }
        bool IsDeadTgt(EntityAlive e) => e == null || e.IsDead() || !e.IsAlive();

        /// <summary>When idle (no target), seek the nearest other bot or real player within a
        /// bounding radius so bots converge and fight across the map instead of passively
        /// random-wandering at spread spawn points. Returns the target position, or
        /// Vector3.zero if none is near.</summary>
        Vector3 SeekNearestEnemy(EntityAlive me, World world, BotConfig cfg, float maxDist)
        {
            try
            {
                Vector3 best = Vector3.zero; float bestD = maxDist;
                var mePos = me.position;
                // nearest other live bot
                try
                {
                    foreach (var b in BotManager.Instance.Bots)
                    {
                        if (b == null || b.EntityId == EntityId) continue;
                        var e2 = world.GetEntity(b.EntityId) as EntityAlive;
                        if (e2 == null || e2.IsDead() || !e2.IsAlive()) continue;
                        // Single ally rule (BotManager.AreAllies): never converge on
                        // teammates (team/squad modes) or any bot when vs-bot is off.
                        // FindTarget excludes allies, so seeking them just clumps bots.
                        if (BotManager.Instance.AreAllies(EntityId, b.EntityId)) continue;
                        float d = Vector3.Distance(mePos, e2.position);
                        if (d < bestD) { bestD = d; best = e2.position; }
                    }
                }
                catch { }
                // nearest real player
                try
                {
                    if (world.Players != null && world.Players.list != null && cfg.BotVsPlayer)
                        foreach (var p in world.Players.list)
                        {
                            if (p == null || p.IsDead()) continue;
                            float d = Vector3.Distance(mePos, p.position);
                            if (d < bestD) { bestD = d; best = p.position; }
                        }
                }
                catch { }
                return best;
            }
            catch { return Vector3.zero; }
        }

        /// <summary>True if another bot is engaging the same target from the same strafe side
        /// (cross-product sign), so this bot flips to split around the enemy — light squad
        /// flanking via position only (no shared state).</summary>
        bool FlankAway(EntityAlive me, World world, EntityAlive target, int myStrafeDir)
        {
            try
            {
                if (me == null || target == null || world == null) return false;
                var toT = target.position - me.position; toT.y = 0;
                if (toT.sqrMagnitude < 0.01f) return false;
                var cross = Vector3.Cross(Vector3.up, toT.normalized);
                int mySide = (int)Mathf.Sign(Vector3.Dot(cross, Vector3.right) * myStrafeDir);
                foreach (var b in BotManager.Instance.Bots)
                {
                    if (b == null || b.EntityId == EntityId) continue;
                    var e2 = world.GetEntity(b.EntityId) as EntityAlive;
                    if (e2 == null || e2.IsDead() || !e2.IsAlive()) continue;
                    // is this bot lining up on the same target?
                    if (Vector3.Distance(e2.position, target.position) > 3f) continue;
                    var bToT = target.position - e2.position; bToT.y = 0;
                    if (bToT.sqrMagnitude < 0.01f) continue;
                    var bCross = Vector3.Cross(Vector3.up, bToT.normalized);
                    int bSide = (int)Mathf.Sign(Vector3.Dot(bCross, Vector3.right));
                    if (bSide == mySide) return true; // same side - flank away
                }
                return false;
            }
            catch { return false; }
        }

        void TryShootBurst(EntityAlive me, EntityAlive target, Vector3 aimPos, World world, BotConfig cfg, bool wantToFire)
        {
            // Ally guard: squad mode, vsBot-off and same-team bots never get shot.
            // FindTarget already excludes them via IsFriendly; this covers a stale
            // target picked up before a toggle or an aggro-swap past the scan.
            if (target != null && BotManager.Instance.AreAllies(me.entityId, target.entityId)) return;
            if (Time.time < _reactionUntil) return;
            if (Time.time < _burstPauseUntil) return;
            // Ammo pacing (zdtd_bot parity): an empty magazine starts a reload
            // during which the bot holds fire (movement continues in Tick).
            if (Time.time < _reloadUntil) return;
            if (_ammo <= 0)
            {
                _ammo = Weapon.MagSize;
                _reloadUntil = Time.time + Weapon.ReloadSec;
                return;
            }
            // Neural fire gate (docs/research/05): when loaded, the net can hold fire
            // even when the heuristic would shoot. Still ANDed with every hard gate.
            if (!wantToFire) return;
            if (_burstLeft <= 0)
            {
                // Start a new burst. No extra pause here: finishing the previous
                // burst already armed BurstPause below, so pausing again would
                // hold fire for ~2x BurstPauseSec between every burst.
                _burstLeft = Weapon.BurstMin + (int)(Rng01() * (Weapon.BurstMax - Weapon.BurstMin + 1));
                // vary strafe dir between bursts
                if (Rng01() < 0.6f) _strafeDir = -_strafeDir;
                return;
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
                        // miss if LOS blocked to aim point - but we already checked canSee
                    }
                    // Damage per pellet: spread damage for shotguns
                    int dmg = pellets > 1 ? Mathf.Max(3, Weapon.Damage) : Weapon.Damage;
                    bool head = pellets == 1 && Rng01() < cfg.HeadshotChance;
                    if (head) dmg = Mathf.RoundToInt(dmg * cfg.HeadshotMultiplier);
                    // Visible fire: zombieSoldier* avatars don't play the gun
                    // holster anim, so we only guarantee that DamageEntity fires
                    // and the log tick is reliable for scoring (see BotCombat).
                    int hpBefore = target.Health;
                    DamageSource ds;
                    try { ds = new DamageSourceEntity(EnumDamageSource.External, EnumDamageTypes.Piercing, me.entityId); }
                    catch { ds = new DamageSource(EnumDamageSource.External, EnumDamageTypes.Piercing); }
                    int dmgResult = 0;
                    // Trader bodies (npcTraderJoel bots) are damage-immune in the engine: the
                    // vanilla DamageEntity call returns 0 and leaves HP unchanged. For a
                    // bot-on-bot hit we bypass by applying raw health damage directly so
                    // player-model bots can actually be killed (FPS scoring intact).
                    bool targetIsBot = BotManager.Instance.IsBotEntity(target.entityId);
                    if (target is EntityTrader && targetIsBot)
                    {
                        target.Health = Mathf.Max(0, target.Health - dmg);
                        try { target.Stats?.Health?.SetChangedFlag(target.Health, target.Health + dmg); } catch { }
                        dmgResult = dmg;
                        if (target.Health <= 0) { try { target.SetDead(); } catch { } }
                    }
                    else
                    {
                        dmgResult = target.DamageEntity(ds, dmg, head, 1f);
                    }
                    int hpAfter = target.Health;
                    if (hpBefore != hpAfter && Rng01() < 0.04f) ModApi.Log($"{Name} -> {target.entityId} dmg={dmg} res={dmgResult} hp {hpBefore}->{hpAfter} weap={Weapon.GunId}");
                    if (hpBefore == hpAfter && dmgResult == 0 && Rng01() < 0.02f) ModApi.Log($"{Name} shot {target.entityId} blocked dmg={dmg} res=0 weap={Weapon.GunId} burst={_burstLeft}");
                    if (target.IsDead()) { try { BotCombat.OnKilled(me, target); } catch { } ModApi.Log($"{Name} KILLED {target.entityId} with {Weapon.GunId}"); break; }
                    if (pellets > 1) break; // one hit per pellet volley (vanilla groups pellets via ray)
                }
            }
            catch (Exception ex)
            {
                // Without this log a throwing shot path fails silently every
                // burst: bots look alive and armed but never fire, and the
                // manager's per-tick wrapper never sees the exception. Flood
                // gate keeps heavy combat from spamming the log.
                ModApi.WarnRateLimited("Bot shot failed " + Name + " -> "
                    + (target != null ? target.entityId.ToString() : "?") + ": " + ex);
            }
            _burstLeft--;
            _ammo--; // one round per trigger pull (zdtd_bot ammo pacing parity)
            // Fire-rate gate: spacing inside a burst is the next-shot pause set
            // here (FireRate); BurstPause only separates bursts (±15% roll,
            // zdtd parity jitter).
            _burstPauseUntil = Time.time + Weapon.FireRate * 0.95f;
            if (_burstLeft <= 0) _burstPauseUntil = Time.time + Weapon.BurstPause * (0.85f + Rng01() * 0.3f);
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

        /// <summary>CanSee against the current target, evaluated at most once per Tick.
        /// Positions are static within a tick, so every consumer (lose-target check,
        /// attack branch, neural inputs) shares one LOS raycast.</summary>
        bool TargetVisible(EntityAlive me, World world, BotConfig cfg)
        {
            if (!_canSeeDone)
            {
                _canSeeDone = true;
                _canSeeVal = _target != null && BotBrain.CanSee(me, _target, world, cfg);
            }
            return _canSeeVal;
        }

        /// <summary>One neural forward pass per Tick. Retreat, aim-bias, fire-gate and
        /// movement all read identical inputs within a tick, so later callers reuse the
        /// cached outputs instead of re-evaluating (each old eval also re-raycast LOS
        /// inside BuildNeuralInputs). Returns false when the gate is off or eval failed,
        /// mirroring the previous per-site fallback to heuristics.</summary>
        bool TryNeuralOnce(EntityAlive me, World world, BotConfig cfg)
        {
            if (_neuralEvalDone) return _neuralOk;
            _neuralEvalDone = true;
            try
            {
                _neuralOk = BotMod.AI.BotNeuralBrain.TryEval(BuildNeuralInputs(me, world, cfg), out _neuralOuts);
                return _neuralOk;
            }
            catch { _neuralOk = false; return false; }
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
            try { canSee = TargetVisible(me, world, cfg) ? 1f : 0f; } catch { }
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

    }
}
