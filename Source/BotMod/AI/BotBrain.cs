using System;
using BotMod.Config;
using BotMod.Core;
using UnityEngine;

namespace BotMod.AI
{
    public static class BotBrain
    {
        public enum State { Wander, Chase, Attack }

        public static EntityAlive FindTarget(EntityAlive me, World world, BotConfig cfg, int preferredId = -1, float preferredScale = 1f)
        {
            if (world == null || me == null) return null;
            EntityAlive best = null;
            float bestScore = float.MaxValue;
            Vector3 myPos = me.position;

            try
            {
                var entities = world.GetEntitiesInBounds(null, new Bounds(myPos, new Vector3(cfg.VisionRange * 2f, 40f, cfg.VisionRange * 2f)));
                if (entities != null)
                {
                    foreach (var e in entities)
                    {
                        if (e == null || e == me) continue;
                        if (!(e is EntityAlive alive)) continue;
                        if (alive.IsDead() || !alive.IsAlive()) continue;
                        if (IsFriendly(me, alive, cfg)) continue;
                        float dist = Vector3.Distance(myPos, alive.position);
                        if (dist > cfg.VisionRange) continue;
                        Vector3 dir = (alive.position - myPos); dir.y = 0;
                        if (dir == Vector3.zero) continue;
                        dir.Normalize();
                        Vector3 fwd = me.transform != null ? me.transform.forward : Vector3.forward;
                        fwd.y = 0; if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward; else fwd.Normalize();
                        float angle = Vector3.Angle(fwd, dir);
                        // Wide FOV for FPS feel; close targets always spotted
                        float fov = cfg.VisionAngle * (dist < 12f ? 1.1f : 1f);
                        if (dist > 7f && angle > fov * 0.5f) continue;
                        if (!HasLineOfSight(myPos + Vector3.up * 1.45f, alive.position + Vector3.up * 1.05f, world)) continue;
                        float score = dist;
                        if (alive is EntityPlayer) score *= 0.82f;
                        if (BotManager.Instance.IsBotEntity(alive.entityId)) score *= 0.9f;
                        score -= (alive.Health / 100f) * -2f; // prefer wounded slightly
                        // Retaliation bias (zdtd_bot grudge parity): the bot keeps
                        // re-acquiring whoever shot it while the grudge is fresh,
                        // instead of forgetting the instant they leave LOS.
                        if (preferredId >= 0 && alive.entityId == preferredId) score *= preferredScale;
                        if (score < bestScore) { bestScore = score; best = alive; }
                    }
                }
            }
            catch { }

            if (best == null)
            {
                try
                {
                    if (world.Players != null && world.Players.list != null)
                        foreach (var p in world.Players.list)
                        {
                            if (p == null || p == me || p.IsDead()) continue;
                            if (IsFriendly(me, p, cfg)) continue;
                            float dist = Vector3.Distance(myPos, p.position);
                            if (dist > cfg.VisionRange) continue;
                            if (!HasLineOfSight(myPos + Vector3.up * 1.45f, p.position + Vector3.up * 1.05f, world)) continue;
                            float score = dist * 0.82f;
                            if (preferredId >= 0 && p.entityId == preferredId) score *= preferredScale;
                            if (score < bestScore) { bestScore = score; best = p; }
                        }
                    var alives = world.EntityAlives;
                    if (alives != null)
                        foreach (var a in alives)
                        {
                            if (a == null || a == me || a.IsDead() || !a.IsAlive()) continue;
                            // Bot bodies are EntityTrader here, so do NOT restrict to zombies.
                            if (IsFriendly(me, a, cfg)) continue;
                            float dist = Vector3.Distance(myPos, a.position);
                            if (dist > cfg.VisionRange) continue;
                            if (!HasLineOfSight(myPos + Vector3.up * 1.45f, a.position + Vector3.up * 1.05f, world)) continue;
                            float score = dist;
                            if (preferredId >= 0 && a.entityId == preferredId) score *= preferredScale;
                            if (score < bestScore) { bestScore = score; best = a; }
                        }
                }
                catch { }
            }
            return best;
        }

        // Q3 LTG/NBG analog: long-term seek (kill/item/camp) + nearby pickup weight
        public enum GoalType { Kill, GetItem, Camp, Roam }
        public static GoalType DecideGoal(EntityAlive me, BotConfig cfg, BotCharacter ch)
        {
            float hp = me.Health / System.Math.Max(1f, cfg.BotHealth);
            if (ch.WantsToRetreat(hp, 12f, false)) return GoalType.Camp;
            if (ch.Camper > 0.6f && hp > 0.7f && ((me.entityId * 2654435761u) % 100 < (uint)(ch.Camper*12))) return GoalType.Camp;
            if (ch.EasyFragger > 0.5f && ((me.entityId * 1103515245u) % 100 < (uint)(ch.EasyFragger*25))) return GoalType.Kill; // quick frag
            return GoalType.Kill;
        }
        public static bool ShouldChase(EntityAlive me, EntityAlive enemy, BotConfig cfg, BotCharacter ch)
        {
            float hp = me.Health / System.Math.Max(1f, cfg.BotHealth);
            float dist = UnityEngine.Vector3.Distance(me.position, enemy.position);
            // Low health + high selfpreservation => don't chase far
            if (hp < 0.3f && ch.SelfPreservation > 0.6f && dist > 26f) return false;
            if (ch.Aggression < 0.35f && dist > 34f) return false;
            return true;
        }
        static bool IsFriendly(EntityAlive me, EntityAlive other, BotConfig cfg)
        {
            bool otherIsBot = BotManager.Instance.IsBotEntity(other.entityId);
            if (otherIsBot && !cfg.BotVsBot) return true;
            // A mod-managed bot uses a trader body (npcTraderJoel) for the player model.
            // It is a combat bot, NOT a friendly NPC trader — so don't apply the
            // EntityTrader-friendly exemption to it.
            if (otherIsBot) return false;
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
            catch { return true; }
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
            catch { return true; }
        }

        public static Vector3 LeadAimPoint(Vector3 from, Vector3 targetPos, Vector3 targetVel, BotConfig cfg, WeaponProfile wp)
        {
            try
            {
                // Hitscan leading: move aim ahead by (dist / bulletSpeed) * velocity. Bullet is hitscan so speed is virtual.
                // Use 90 m/s virtual + distance factor; strafe prediction scales with difficulty.
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
            catch { return targetPos; }
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
                // Trader bodies (npcTraderJoel bots) ignore MoveEntityHeaded — the engine only
                // moves them through their AI moveHelper, which we don't drive. Detect that the
                // position didn't change and step it directly so player-model bots patrol/chase.
                if (Vector3.Distance(me.position, before) <= 0.01f)
                    ManualStep(me, dir, dist);
            }
            catch { }
        }
        /// <summary>Direct position step used when the entity's motor ignores MoveEntityHeaded
        /// (trader bodies). Steps me.position toward `dir` at a fixed speed; used by move/strafe.</summary>
        static void ManualStep(EntityAlive me, Vector3 dir, float dist)
        {
            try
            {
                float stepSpeed = 1.6f;
                Vector3 step = dir * (stepSpeed * UnityEngine.Time.deltaTime);
                if (step.magnitude > dist) step = dir * dist;
                Vector3 np = me.position + step;
                try { me.position = np; } catch { }
            }
            catch { }
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
            catch { }
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
            catch { }
        }
        public static void Strafe(EntityAlive me, EntityAlive target, int dirSign)
        {
            try
            {
                Vector3 toTarget = target.position - me.position; toTarget.y = 0;
                if (toTarget == Vector3.zero) return; toTarget.Normalize();
                Vector3 strafe = Vector3.Cross(Vector3.up, toTarget) * dirSign;
                Vector3 dir = (toTarget * 0.22f + strafe * 0.78f).normalized;
                float dist = Mathf.Max(0.3f, Vector3.Distance(me.position, target.position) * 0.2f);
                MoveWithFallback(me, dir, dist);
            }
            catch { }
        }
        public static void Backpedal(EntityAlive me, EntityAlive target, int dirSign)
        {
            try
            {
                Vector3 toTarget = target.position - me.position; toTarget.y = 0;
                if (toTarget == Vector3.zero) return; toTarget.Normalize();
                Vector3 strafe = Vector3.Cross(Vector3.up, toTarget) * dirSign;
                Vector3 dir = (-toTarget * 0.55f + strafe * 0.45f).normalized;
                float dist = Mathf.Max(0.3f, Vector3.Distance(me.position, target.position) * 0.2f);
                MoveWithFallback(me, dir, dist);
            }
            catch { }
        }
        public static Vector3 GroundSplashTarget(EntityAlive me, EntityAlive target, World world)
        {
            try
            {
                Vector3 origin = target.position; Vector3 end = origin + Vector3.down * 64f;
                Ray ray = new Ray(origin, Vector3.down);
                if (Physics.Raycast(ray, out RaycastHit hit, 70f, -1))
                {
                    Vector3 ground = hit.point + Vector3.up * 4f;
                    // trace from eye to ground
                    Vector3 eye = me.position + Vector3.up * 1.45f;
                    Vector3 dir = ground - eye; float dist = dir.magnitude; dir /= Mathf.Max(0.01f, dist);
                    Ray ray2 = new Ray(eye, dir);
                    if (Physics.Raycast(ray2, out RaycastHit hit2, dist, -1))
                    {
                        if (Vector3.Distance(hit2.point, ground) < 60f) return ground;
                    }
                    else if (VoxelLineClear(eye, ground, world)) return ground;
                }
                // fallback voxel down trace
                for (int i = 1; i < 16; i++)
                {
                    Vector3 probe = origin + Vector3.down * (i * 4f);
                    var bv = world.GetBlock(new Vector3i(Mathf.FloorToInt(probe.x), Mathf.FloorToInt(probe.y), Mathf.FloorToInt(probe.z)));
                    if (bv.type != 0) { var block = Block.list[bv.type]; if (block != null && block.IsCollideMovement) return probe + Vector3.up * 7f; }
                }
            } catch {}
            return Vector3.zero;
        }
        public static bool TraceClear(EntityAlive me, Vector3 aim, World world, EntityAlive intended)
        {
            try
            {
                Vector3 eye = me.position + Vector3.up * 1.45f;
                Vector3 dir = aim - eye; float dist = dir.magnitude; if (dist < 0.1f) return true; dir /= dist;
                Ray ray = new Ray(eye, dir);
                if (Physics.Raycast(ray, out RaycastHit hit, dist, -1))
                {
                    if (Vector3.Distance(hit.point, aim) < 0.9f) return true;
                    var hitEnt = hit.collider != null ? hit.collider.GetComponentInParent<Entity>() : null;
                    if (hitEnt != null)
                    {
                        // teammate abort like Q3
                        if (hitEnt is EntityPlayer || BotMod.Core.BotManager.Instance.IsBotEntity(hitEnt.entityId))
                        {
                            if (intended != null && hitEnt.entityId == intended.entityId) return true;
                            // hit someone else - if friendly per config, fail
                            // For now, only block if intended was hittable and we hit another bot/player on same team
                        }
                        return true;
                    }
                    return false;
                }
                return VoxelLineClear(eye, aim, world);
            } catch { return true; }
        }
        public static Vector3 FindCover(EntityAlive me, EntityAlive threat, World world)
        {
            // Doom3 idAASFindCover port: sample 6 directions + up, check PVS-ish via LOS blocked from threat
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
            catch { }
        }
        static float WanderHash01(int entityId, int salt) { uint h = (uint)entityId * 2654435761u + (uint)salt * 97u + 1u; h = h * 1103515245u + 12345u; return (h >> 8 & 0x00ffffffu) / 16777216f; }
        public static Vector3 PickWanderTarget(EntityAlive me, World world, float radius, float rollAng01 = -1f, float rollDist01 = -1f)
        {
            // Deterministic when rolls are supplied (from the bot's per-slot LCG, zdtd parity);
            // fall back to a cheap hash of (entityId, pos) so the result is still not wall-clock noise.
            if (rollAng01 < 0f) rollAng01 = WanderHash01(me.entityId, 11);
            if (rollDist01 < 0f) rollDist01 = WanderHash01(me.entityId, 23);
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
