using System;
using BotMod.Config;
using BotMod.Core;
using BotMod.Foundation;
using UnityEngine;

namespace BotMod.AI
{
    public static class BotBrain
    {
        public enum State { Wander, Chase, Attack }

        // Grudge bias (zdtd_bot parity): while the revenge memory is fresh,
        // FindTarget out-scores the attacker by this factor (0.6x).
        const float GrudgeBias = 0.6f;

        // Smallest multiplier any score below can carry, so a candidate can be
        // rejected on distance alone before its LOS raycast runs. The scoring
        // is `distance * classMult(s) + hpFrac * 6` (health term never
        // negative), times GrudgeBias for the preferred id, so `distance *
        // minMult` is a lower bound on the final score for every candidate.
        // 0.82 (player) * 0.9 (registered bot) is the floor: both conditions
        // apply to a body that is somehow both, and the exact multiplier for
        // any one candidate is at least this. The full-world EntityAlives
        // fallback pass applies no class multiplier at all, so it passes 1f.
        const float MinScoreMult = 0.82f * 0.9f;

        /// <summary>Relative slack on the pruning test. The bound is computed
        /// as `dist * mult * GrudgeBias` while the score it bounds is computed
        /// as `(dist * mult + hpFrac * 6) * GrudgeBias`: different
        /// association, so the bound can land one rounding step ABOVE the score
        /// it is meant to understate, and a candidate sitting a rounding step
        /// below the incumbent would then be pruned when it should have won.
        /// The slack is six orders of magnitude above float epsilon at these
        /// magnitudes, so the bound stays a true lower bound; a candidate that
        /// close to the incumbent simply pays its one raycast.</summary>
        const float ScoreBoundSlack = 1e-6f;

        /// <summary>True when a candidate at <paramref name="dist"/> cannot
        /// out-score the incumbent, so its line-of-sight raycast (a
        /// Physics.Raycast plus up to 64 voxel GetBlock calls) can be skipped.
        /// <paramref name="minMult"/> is the scoring pass's smallest class
        /// multiplier. Pure arithmetic, no engine types, so the "the bound is
        /// a true lower bound" contract is pinned headless by
        /// BotBrainArithTests rather than by a comment.</summary>
        internal static bool CannotBeat(int entityId, int preferredId, float dist, float bestScore, float minMult)
        {
            float bound = dist * (entityId == preferredId ? minMult * GrudgeBias : minMult);
            return bound > bestScore * (1f + ScoreBoundSlack);
        }

        public static EntityAlive FindTarget(EntityAlive me, World world, BotConfig cfg, int preferredId)
        {
            if (world == null || me == null) return null;
            EntityAlive best = null;
            float bestScore = float.MaxValue;
            Vector3 myPos = me.position;
            // Hoisted out of the candidate loops: the hpFrac divisor is
            // config-constant for the whole scan, and the loops run it once per
            // entity in the vision box (or once per EntityAlive in the world).
            float botHealth = System.Math.Max(1f, cfg.BotHealth);

            try
            {
                // Facing is candidate-independent: read me.transform once per
                // acquisition instead of once per entity in the vision box.
                Vector3 fwd = me.transform != null ? me.transform.forward : Vector3.forward;
                fwd.y = 0; if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward; else fwd.Normalize();
                var entities = world.GetEntitiesInBounds(null, new Bounds(myPos, new Vector3(cfg.VisionRange * 2f, 40f, cfg.VisionRange * 2f)));
                if (entities != null)
                {
                    foreach (var e in entities)
                    {
                        if (e == null || e == me) continue;
                        if (!(e is EntityAlive alive)) continue;
                        if (alive.IsDead() || !alive.IsAlive()) continue;
                        float dist = Vector3.Distance(myPos, alive.position);
                        if (dist > cfg.VisionRange) continue;
                        // Range, then the distance-only score bound, then the
                        // ally check, cheapest first. IsFriendly costs a
                        // registry lookup and, for a bot body, two TeamGate
                        // lock acquisitions; the bound rejects a candidate
                        // that cannot out-score the incumbent, so it also
                        // skips the FOV math and the LOS raycast. Every gate
                        // here only skips, so the order changes no pick.
                        if (CannotBeat(alive.entityId, preferredId, dist, bestScore, MinScoreMult)) continue;
                        if (IsFriendly(me, alive, cfg)) continue;
                        Vector3 dir = (alive.position - myPos); dir.y = 0;
                        if (dir == Vector3.zero) continue;
                        dir.Normalize();
                        float angle = Vector3.Angle(fwd, dir);
                        // Wide FOV for FPS feel; close targets always spotted
                        float fov = cfg.VisionAngle * (dist < 12f ? 1.1f : 1f);
                        if (dist > 7f && angle > fov * 0.5f) continue;
                        if (!HasLineOfSight(myPos + Vector3.up * 1.45f, alive.position + Vector3.up * 1.05f, world)) continue;
                        float score = dist;
                        if (alive is EntityPlayer) score *= 0.82f;
                        if (BotManager.Instance.IsBotEntity(alive.entityId)) score *= 0.9f;
                        // FPS priority: strongly prefer finishing wounded targets (low HP -> low
                        // score -> chosen). A ~10% HP foe beats a full-HP one by ~5.4 on the
                        // distance scale, matching finish-the-kill. Fraction of cfg.BotHealth,
                        // same divisor as every other hpFrac in the mod.
                        score += (alive.Health / botHealth) * 6f;
                        // Retaliation bias (zdtd_bot grudge parity): the bot keeps
                        // re-acquiring whoever shot it while the grudge is fresh,
                        // instead of forgetting the instant they leave LOS.
                        if (preferredId >= 0 && alive.entityId == preferredId) score *= GrudgeBias;
                        if (score < bestScore) { bestScore = score; best = alive; }
                    }
                }
            }
            // The vision-box pass aborting is not fatal (the sweep below still
            // runs), but a bot that only ever fails this pass never acquires a
            // target and the log says nothing about why.
            catch (Exception ex) { ModApi.WarnRateLimited(() => "vision-box target scan failed, falling back to the world sweep: " + ex.Message); }

            if (best == null)
            {
                try
                {
                    bool playersScanned = false;
                    if (world.Players != null && world.Players.list != null)
                    {
                        playersScanned = true;
                        foreach (var p in world.Players.list)
                        {
                            if (p == null || p == me || p.IsDead()) continue;
                            float dist = Vector3.Distance(myPos, p.position);
                            if (dist > cfg.VisionRange) continue;
                            if (CannotBeat(p.entityId, preferredId, dist, bestScore, MinScoreMult)) continue;
                            if (IsFriendly(me, p, cfg)) continue;
                            if (!HasLineOfSight(myPos + Vector3.up * 1.45f, p.position + Vector3.up * 1.05f, world)) continue;
                            float score = dist * 0.82f;
                            if (preferredId >= 0 && p.entityId == preferredId) score *= GrudgeBias;
                            if (score < bestScore) { bestScore = score; best = p; }
                        }
                    }
                    var alives = world.EntityAlives;
                    if (alives != null)
                        foreach (var a in alives)
                        {
                            if (a == null || a == me || a.IsDead() || !a.IsAlive()) continue;
                            // Connected players were already scored above with
                            // dist * 0.82, which is strictly lower than what
                            // this loop computes for the same body (dist +
                            // hp bonus), so a second pass cannot change the
                            // pick; it only repeats their LOS raycast.
                            if (playersScanned && a is EntityPlayer) continue;
                            // Scan every EntityAlive, not just zombies: bot bodies follow
                            // BotEntityClass (and its negative-id fallbacks), so they are
                            // not guaranteed to be zombie-typed.
                            float dist = Vector3.Distance(myPos, a.position);
                            if (dist > cfg.VisionRange) continue;
                            // Same gates, cheapest first (see the bounds pass).
                            // The bound is the sharpest of the three: this pass
                            // applies no class multiplier, so an ordinary
                            // candidate cannot score below its distance at
                            // all. On a world with hundreds of EntityAlives
                            // this is what keeps the sweep from raycasting
                            // every body in vision range.
                            if (CannotBeat(a.entityId, preferredId, dist, bestScore, 1f)) continue;
                            if (IsFriendly(me, a, cfg)) continue;
                            if (!HasLineOfSight(myPos + Vector3.up * 1.45f, a.position + Vector3.up * 1.05f, world)) continue;
                            float score = dist;
                            score += (a.Health / botHealth) * 6f; // finish wounded targets
                            if (preferredId >= 0 && a.entityId == preferredId) score *= GrudgeBias;
                            if (score < bestScore) { bestScore = score; best = a; }
                        }
                }
                // Last resort gone: the bot ends this tick with no target at
                // all and drifts into Wander, which reads exactly like a quiet
                // world. Per bot per tick, so the gate is what bounds it.
                catch (Exception ex) { ModApi.WarnRateLimited(() => "world target sweep failed, bot finds no enemy: " + ex.Message); }
            }
            return best;
        }

        /// <summary>Q3 LTG analog for idle bots: hold ground when health falls
        /// into the retreat band (0.35 + SelfPreservation*0.18), or when a
        /// committed camper rolls a hold. The kill/item/roam goal picks
        /// collapse here to "not camping".</summary>
        public static bool WantsIdleCamp(EntityAlive me, BotConfig cfg, BotCharacter ch)
        {
            float hp = me.Health / System.Math.Max(1f, cfg.BotHealth);
            if (hp < 0.35f + ch.SelfPreservation * 0.18f) return true;
            return ch.Camper > 0.6f && hp > 0.7f && CampHashGate(me.entityId, ch.Camper);
        }
        /// <summary>Deterministic camper hold roll: passes for about Camper*12 percent
        /// of entity ids (Q3 LTG parity). The hash runs in uint space on purpose:
        /// C# promotes int*uint to long and % keeps the dividend's sign, so the
        /// previous inline form ((me.entityId * 2654435761u) % 100) went negative
        /// for every negative entity id and compared true against the unsigned
        /// threshold unconditionally - fallback spawn classes camped every idle
        /// tick instead of rolling. Same tap as Foundation.Lcg.</summary>
        internal static bool CampHashGate(int entityId, float camper)
        {
            if (float.IsNaN(camper) || float.IsInfinity(camper) || camper <= 0f) return false;
            uint threshold = (uint)System.Math.Min(100f, camper * 12f); // saturates above camper=8.33
            return ((uint)entityId * 2654435761u) % 100u < threshold;
        }
        static bool IsFriendly(EntityAlive me, EntityAlive other, BotConfig cfg)
        {
            bool otherIsBot = BotManager.Instance.IsBotEntity(other.entityId);
            // Squad mode, vsBot-off, and same-team all make bots allies; otherwise
            // bots are fair game. Bot bodies are zombieSoldier (EntityZombie) - the
            // friendly checks below must not exempt them from the vsBot gate.
            if (otherIsBot) return BotManager.Instance.AreAllies(me.entityId, other.entityId);
            if (other is EntityPlayer && !cfg.BotVsPlayer) return true;
            if (other is EntityZombie && !cfg.BotVsZombie) return true;
            if (other is EntityTrader) return true;
            if (other is EntityAnimal) return true;
            return false;
        }

        public static bool CanSee(EntityAlive me, EntityAlive target, World world, BotConfig cfg)
        {
            if (me == null || target == null || world == null) return false;
            float dist = Vector3.Distance(me.position, target.position);
            if (dist > cfg.VisionRange + 2f) return false;
            return HasLineOfSight(me.position + Vector3.up * 1.45f, target.position + Vector3.up * 1.05f, world);
        }

        static bool HasLineOfSight(Vector3 from, Vector3 to, World world)
        {
            try
            {
                Vector3 dir = to - from; float dist = dir.magnitude;
                if (dist < 0.1f) return true; dir /= dist;
                Ray ray = new Ray(from, dir);
                if (Physics.Raycast(ray, out RaycastHit hit, dist, -1))
                {
                    if (Vector3.Distance(hit.point, to) < 0.7f) return true;
                    var hitEnt = hit.collider != null ? hit.collider.GetComponentInParent<Entity>() : null;
                    if (hitEnt != null) return true; // hit an entity - clear
                    return false;
                }
                return VoxelLineClear(from, to, world);
            }
            // "Clear" is the fallback because a blind bot never fights, which is
            // a worse outcome than one that shoots at a wall. It is also the
            // outcome that hides itself: a throwing raycast had every bot in
            // vision range shooting through geometry, with no LOS evidence
            // anywhere in the log. Per bot per tick, so the flood gate is what
            // keeps this to one line.
            catch (Exception ex) { ModApi.WarnRateLimited(() => "line-of-sight raycast failed, assuming clear: " + ex.Message); return true; }
        }
        static bool VoxelLineClear(Vector3 from, Vector3 to, World world)
        {
            try
            {
                Vector3 dir = to - from; float dist = dir.magnitude;
                if (dist < 1f) return true; dir /= dist;
                int steps = Mathf.Clamp(Mathf.RoundToInt(dist * 1.2f), 4, 64);
                for (int i = 1; i < steps; i++)
                {
                    Vector3 p = Vector3.Lerp(from, to, (float)i / steps);
                    var bv = world.GetBlock(new Vector3i(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z)));
                    if (bv.type != 0) { var block = Block.list[bv.type]; if (block != null && block.IsCollideMovement) return false; }
                }
                return true;
            }
            // Same contract as HasLineOfSight above: the voxel fallback runs
            // only when the physics ray found nothing, so a throw here is the
            // only thing standing between the bot and a wall-shotted target.
            catch (Exception ex) { ModApi.WarnRateLimited(() => "voxel line-of-sight failed, assuming clear: " + ex.Message); return true; }
        }

        public static Vector3 LeadAimPoint(Vector3 from, Vector3 targetPos, Vector3 targetVel, BotConfig cfg, WeaponProfile wp)
        {
            try
            {
                // Hitscan leading: move aim ahead by (dist / bulletSpeed) * velocity. Bullet is hitscan so speed is virtual.
                // Use a 55 m/s virtual bullet + distance factor; strafe prediction scales with difficulty.
                float dist = Vector3.Distance(from, targetPos);
                float leadScale = 0.25f + cfg.Difficulty * 0.18f + (wp.Range > 40f ? 0.15f : 0f);
                Vector3 vel = targetVel;
                // Clamp insane velocities
                if (vel.magnitude > 12f) vel = vel.normalized * 12f;
                float t = dist / 55f; // ~0.2s at 10m, 0.8s at 45m
                Vector3 lead = targetPos + vel * t * leadScale;
                // Vertical: aim at chest, not feet
                lead.y = targetPos.y + 1.05f;
                return lead;
            }
            // Unled aim is a real accuracy loss, so it is worth a line; the
            // caller still gets a usable point and the bot keeps fighting.
            catch (Exception ex) { ModApi.WarnRateLimited(() => "LeadAimPoint failed, aiming at the unshifted target: " + ex.Message); return targetPos; }
        }

        /// <summary>Report a movement primitive that threw before it moved the
        /// body at all. The per-statement catches inside the primitives stay
        /// silent on purpose: MoveEntityHeaded and SetLookPosition throw
        /// routinely for a body that died or was unlinked earlier this tick, and
        /// logging that would bury real failures under a per-frame flood. The
        /// outer catch is the opposite case: the whole step never ran, so the
        /// bot stands still and the state dump shows no reason. Rate-limited and
        /// main-thread, like every other hot-path failure report.</summary>
        static void WarnMoveFailed(string op, EntityAlive me, Exception ex)
        {
            string id = "?";
            try { id = me != null ? me.entityId.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?"; }
            catch { }
            ModApi.WarnRateLimited(() => "bot " + id + " " + op + " failed, position unchanged: " + ex.Message);
        }

        public static void MoveTo(EntityAlive me, Vector3 pos)
        {
            try
            {
                Vector3 dir = pos - me.position; dir.y = 0;
                float dist = dir.magnitude;
                if (dist < 0.2f) return;
                dir.Normalize();
                Vector3 before = me.position;
                try { me.MoveEntityHeaded(dir, false); } catch { }
                try { me.SetLookPosition(pos + Vector3.up * 1f); } catch { }
                if (dist > 6f) try { me.FindPath(pos, 1f, false, null); } catch { }
                // Trader bodies (npcTraderJoel bots) ignore MoveEntityHeaded: the engine only
                // moves them through their AI moveHelper, which we don't drive. Detect that the
                // position didn't change and step it directly so player-model bots patrol/chase.
                if (Vector3.Distance(me.position, before) <= 0.01f)
                    ManualStep(me, dir, dist);
            }
            catch (Exception ex) { WarnMoveFailed("MoveTo", me, ex); }
        }
        /// <summary>Direct position step used when the entity's motor ignores MoveEntityHeaded
        /// (trader bodies). Steps me.position toward `dir` at a fixed speed; used by move/strafe.</summary>
        static void ManualStep(EntityAlive me, Vector3 dir, float dist)
        {
            try
            {
                float stepSpeed = 1.6f;
                Vector3 step = dir * (stepSpeed * BotClock.Delta);
                if (step.magnitude > dist) step = dir * dist;
                Vector3 np = me.position + step;
                // The assignment itself is the only way a trader body moves, and a
                // throw here means the body is stuck for this tick.
                try { me.position = np; }
                catch (Exception ex) { WarnMoveFailed("ManualStep", me, ex); }
            }
            catch (Exception ex) { WarnMoveFailed("ManualStep", me, ex); }
        }
        /// <summary>Try the motor, falling back to a manual position step for static bodies
        /// (traders). Shared by Strafe/Backpedal so attacking player-model bots orbit.</summary>
        static void MoveWithFallback(EntityAlive me, Vector3 dir, float dist)
        {
            try
            {
                Vector3 before = me.position;
                try { me.MoveEntityHeaded(dir, false); } catch { }
                if (Vector3.Distance(me.position, before) <= 0.01f)
                    ManualStep(me, dir, dist);
            }
            catch (Exception ex) { WarnMoveFailed("MoveWithFallback", me, ex); }
        }
        public static void FaceTowards(EntityAlive me, Vector3 pos)
        {
            try
            {
                Vector3 dir = pos - me.position; dir.y = 0;
                if (dir.sqrMagnitude < 0.01f) return;
                dir.Normalize();
                float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                try { me.SetRotation(new Vector3(0, yaw, 0)); } catch { }
                try { me.SetLookPosition(pos + Vector3.up * 1.12f); } catch { }
            }
            catch (Exception ex) { WarnMoveFailed("FaceTowards", me, ex); }
        }
        /// <summary>Move in an arbitrary direction (R10 neural movement): the net
        /// composes forward/lateral from (retreat, strafe) and this applies it.</summary>
        public static void MoveDir(EntityAlive me, Vector3 dir)
        {
            try
            {
                dir.y = 0;
                if (dir.sqrMagnitude < 0.0001f) return;
                dir.Normalize();
                float dist = 0.8f;
                MoveWithFallback(me, dir, dist);
            }
            catch (Exception ex) { WarnMoveFailed("MoveDir", me, ex); }
        }
        /// <summary>Orbit the target on a forward/side blend, the shape both
        /// combat steps take: Strafe leans on the lateral term, Backpedal on
        /// the reverse-forward one.</summary>
        static void LateralStep(EntityAlive me, EntityAlive target, int dirSign, float forwardWeight, float sideWeight, string op)
        {
            try
            {
                Vector3 toTarget = target.position - me.position; toTarget.y = 0;
                if (toTarget == Vector3.zero) return; toTarget.Normalize();
                Vector3 strafe = Vector3.Cross(Vector3.up, toTarget) * dirSign;
                Vector3 dir = (toTarget * forwardWeight + strafe * sideWeight).normalized;
                float dist = Mathf.Max(0.3f, Vector3.Distance(me.position, target.position) * 0.2f);
                MoveWithFallback(me, dir, dist);
            }
            catch (Exception ex) { WarnMoveFailed(op, me, ex); }
        }
        public static void Strafe(EntityAlive me, EntityAlive target, int dirSign) { LateralStep(me, target, dirSign, 0.22f, 0.78f, "Strafe"); }
        public static void Backpedal(EntityAlive me, EntityAlive target, int dirSign) { LateralStep(me, target, dirSign, -0.55f, 0.45f, "Backpedal"); }
        public static Vector3 FindCover(EntityAlive me, EntityAlive threat, World world)
        {
            // Doom3 idAASFindCover port: sample the 8 compass directions and
            // keep candidates a threat's LOS cannot reach
            Vector3 best = Vector3.zero; float bestScore = -1f;
            Vector3 myPos = me.position;
            for (int i = 0; i < 8; i++)
            {
                float ang = i * 45f * Mathf.Deg2Rad;
                Vector3 cand = myPos + new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang)) * 10f;
                // ground it
                for (int y = 6; y >= -2; y--)
                {
                    var bv = world.GetBlock(new Vector3i(Mathf.FloorToInt(cand.x), Mathf.FloorToInt(myPos.y + y), Mathf.FloorToInt(cand.z)));
                    if (bv.type != 0) { cand.y = myPos.y + y + 1.8f; break; }
                }
                // must be not visible from threat
                if (HasLineOfSight(threat.position + Vector3.up * 1.45f, cand + Vector3.up * 0.5f, world)) continue;
                // must be reachable (not inside wall)
                var bv2 = world.GetBlock(new Vector3i(Mathf.FloorToInt(cand.x), Mathf.FloorToInt(cand.y), Mathf.FloorToInt(cand.z)));
                if (bv2.type != 0 && Block.list[bv2.type] != null && Block.list[bv2.type].IsCollideMovement) continue;
                float score = 10f - Vector3.Distance(myPos, cand) * 0.2f; // prefer nearer cover
                if (score > bestScore) { bestScore = score; best = cand; }
            }
            return best;
        }
        public static void JumpOrStrafe(EntityAlive me)
        {
            try
            {
                try { me.StartJump(); } catch { }
                Vector3 fwd = me.transform != null ? me.transform.forward : new Vector3(0, 0, 1);
                fwd.y = 0; fwd.Normalize();
                Vector3 strafe = Vector3.Cross(Vector3.up, fwd) * (((me.entityId ^ 0x9E3779B9) & 1) == 0 ? -1 : 1);
                me.MoveEntityHeaded(strafe, false);
            }
            // The unjumped/stationary case is the point of the call, so a
            // throw here is the unstick that silently stopped working.
            catch (Exception ex) { WarnMoveFailed("JumpOrStrafe", me, ex); }
        }
        static float WanderHash01(int entityId, int salt) { return Lcg.Seeded((uint)entityId * 2654435761u + (uint)salt * 97u + 1u).Next01(); }
        public static Vector3 PickWanderTarget(EntityAlive me, World world, float radius, float rollAng01, float rollDist01)
        {
            // Rolls come from the bot's per-slot LCG (zdtd parity); later attempts
            // and the fallback jitter derive from WanderHash01, so nothing here
            // reads the wall clock.
            float ang0 = rollAng01 * (float)Math.PI * 2f;
            float dist0 = rollDist01 * radius * 0.7f + radius * 0.3f;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                float ang = attempt == 0 ? ang0 : WanderHash01(me.entityId, 31 + attempt) * (float)Math.PI * 2f;
                float dist = attempt == 0 ? dist0 : WanderHash01(me.entityId, 71 + attempt) * radius * 0.7f + radius * 0.3f;
                Vector3 cand = me.position + new Vector3(Mathf.Cos(ang) * dist, 0, Mathf.Sin(ang) * dist);
                cand = new Vector3(cand.x, me.position.y, cand.z);
                try
                {
                    var bv = world.GetBlock(new Vector3i(Mathf.FloorToInt(cand.x), Mathf.FloorToInt(cand.y), Mathf.FloorToInt(cand.z)));
                    if (bv.type != 0 && Block.list[bv.type] != null && Block.list[bv.type].IsCollideMovement) continue;
                    var bv2 = world.GetBlock(new Vector3i(Mathf.FloorToInt(cand.x), Mathf.FloorToInt(cand.y + 1), Mathf.FloorToInt(cand.z)));
                    if (bv2.type != 0 && Block.list[bv2.type] != null && Block.list[bv2.type].IsCollideMovement) continue;
                    return cand;
                }
                catch { return cand; }
            }
            // Fallback jitter is also deterministic (hash-based).
            float jx = WanderHash01(me.entityId, 101) * 6f - 3f, jz = WanderHash01(me.entityId, 103) * 6f - 3f;
            return me.position + new Vector3(jx, 0, jz);
        }
    }
}
