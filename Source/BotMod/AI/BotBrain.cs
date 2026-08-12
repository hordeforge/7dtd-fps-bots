using System;
using BotMod.Config;
using BotMod.Core;
using UnityEngine;

namespace BotMod.AI
{
    public static class BotBrain
    {
        public enum State { Wander, Chase, Attack }

        public static EntityAlive FindTarget(EntityAlive me, World world, BotConfig cfg)
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
                        if (dist > 8f && angle > cfg.VisionAngle * 0.5f) continue;
                        if (!HasLineOfSight(myPos + Vector3.up * 1.4f, alive.position + Vector3.up * 1.0f, world)) continue;
                        float score = dist;
                        if (alive is EntityPlayer) score *= 0.85f;
                        if (alive is EntityZombie) score *= 0.95f;
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
                    {
                        foreach (var p in world.Players.list)
                        {
                            if (p == null || p == me || p.IsDead()) continue;
                            if (IsFriendly(me, p, cfg)) continue;
                            float dist = Vector3.Distance(myPos, p.position);
                            if (dist > cfg.VisionRange) continue;
                            if (!HasLineOfSight(myPos + Vector3.up * 1.4f, p.position + Vector3.up * 1.0f, world)) continue;
                            if (dist * 0.85f < bestScore) { bestScore = dist * 0.85f; best = p; }
                        }
                    }
                    var alives = world.EntityAlives;
                    if (alives != null)
                    {
                        foreach (var a in alives)
                        {
                            if (a == null || a == me || a.IsDead()) continue;
                            if (!(a is EntityZombie)) continue;
                            if (IsFriendly(me, a, cfg)) continue;
                            float dist = Vector3.Distance(myPos, a.position);
                            if (dist > cfg.VisionRange) continue;
                            if (!HasLineOfSight(myPos + Vector3.up * 1.4f, a.position + Vector3.up * 1.0f, world)) continue;
                            if (dist < bestScore) { bestScore = dist; best = a; }
                        }
                    }
                }
                catch { }
            }
            return best;
        }

        static bool IsFriendly(EntityAlive me, EntityAlive other, BotConfig cfg)
        {
            bool otherIsBot = BotManager.Instance.IsBotEntity(other.entityId);
            if (otherIsBot && !cfg.BotVsBot) return true;
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
            return HasLineOfSight(me.position + Vector3.up * 1.4f, target.position + Vector3.up * 1.0f, world);
        }

        static bool HasLineOfSight(Vector3 from, Vector3 to, World world)
        {
            try
            {
                Vector3 dir = to - from;
                float dist = dir.magnitude;
                if (dist < 0.1f) return true;
                dir /= dist;
                Ray ray = new Ray(from, dir);
                if (Physics.Raycast(ray, out RaycastHit hit, dist, -1))
                {
                    if (Vector3.Distance(hit.point, to) < 0.6f) return true;
                    var hitEnt = hit.collider != null ? hit.collider.GetComponentInParent<Entity>() : null;
                    if (hitEnt != null) return true;
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
                Vector3 dir = to - from;
                float dist = dir.magnitude;
                if (dist < 1f) return true;
                dir /= dist;
                int steps = Mathf.Clamp(Mathf.RoundToInt(dist * 1.2f), 4, 64);
                for (int i = 1; i < steps; i++)
                {
                    float t = (float)i / steps;
                    Vector3 p = Vector3.Lerp(from, to, t);
                    var bv = world.GetBlock(new Vector3i(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z)));
                    if (bv.type != 0)
                    {
                        var block = Block.list[bv.type];
                        if (block != null && block.IsCollideMovement) return false;
                    }
                }
                return true;
            }
            catch { return true; }
        }

        public static void MoveTo(EntityAlive me, Vector3 pos)
        {
            try
            {
                Vector3 dir = pos - me.position; dir.y = 0;
                float dist = dir.magnitude;
                if (dist < 0.2f) return;
                dir.Normalize();
                // Dedi has no SetMoveVector; use MoveEntityHeaded + look
                try { me.MoveEntityHeaded(dir, false); } catch { }
                try { me.SetLookPosition(pos + Vector3.up * 1f); } catch { }
                if (dist > 6f)
                {
                    try { me.FindPath(pos, 1f, false, null); } catch { }
                }
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
                try { me.SetLookPosition(pos + Vector3.up * 1.1f); } catch { }
            }
            catch { }
        }

        public static void Strafe(EntityAlive me, EntityAlive target)
        {
            try
            {
                Vector3 toTarget = target.position - me.position; toTarget.y = 0;
                if (toTarget == Vector3.zero) return;
                toTarget.Normalize();
                Vector3 strafe = Vector3.Cross(Vector3.up, toTarget);
                if (new System.Random().Next(2) == 0) strafe = -strafe;
                Vector3 dir = (toTarget * 0.3f + strafe * 0.7f).normalized;
                me.MoveEntityHeaded(dir, false);
            }
            catch { }
        }

        public static void JumpOrStrafe(EntityAlive me)
        {
            try
            {
                try { me.StartJump(); } catch { }
                Vector3 fwd = me.transform != null ? me.transform.forward : new Vector3(0, 0, 1);
                fwd.y = 0; fwd.Normalize();
                Vector3 strafe = Vector3.Cross(Vector3.up, fwd);
                if (new System.Random().Next(2) == 0) strafe = -strafe;
                me.MoveEntityHeaded(strafe, false);
            }
            catch { }
        }

        public static Vector3 PickWanderTarget(EntityAlive me, World world, float radius)
        {
            var rng = new System.Random();
            for (int attempt = 0; attempt < 8; attempt++)
            {
                float ang = (float)(rng.NextDouble() * Math.PI * 2);
                float dist = (float)(rng.NextDouble() * radius * 0.7f + radius * 0.3f);
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
            return me.position + new Vector3((float)(rng.NextDouble() * 6 - 3), 0, (float)(rng.NextDouble() * 6 - 3));
        }
    }
}
