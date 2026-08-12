using System;
using System.Collections.Generic;
using BotMod.Config;
using UnityEngine;

namespace BotMod.Core
{
    public static class BotSpawner
    {
        static readonly System.Random Rng = new System.Random();

        public static string PickName(BotConfig cfg)
        {
            if (cfg.BotNames == null || cfg.BotNames.Length == 0) return "Bot_" + Rng.Next(1000, 9999);
            return cfg.BotNames[Rng.Next(cfg.BotNames.Length)] + "_" + Rng.Next(10, 99);
        }

        public static Vector3 PickSpawnPosition(World world, BotConfig cfg)
        {
            try
            {
                if (world.Players != null && world.Players.list != null && world.Players.list.Count > 0 && Rng.NextDouble() < cfg.SpawnNearPlayerChance)
                {
                    var players = world.Players.list;
                    var list = new List<EntityPlayer>(players.Count);
                    foreach (var p in players) if (p != null && !p.IsDead() && p.IsAlive()) list.Add(p);
                    if (list.Count > 0)
                    {
                        var pl = list[Rng.Next(list.Count)];
                        Vector3 pp = pl.position;
                        float ang = (float)(Rng.NextDouble() * Math.PI * 2);
                        float dist = (float)(Rng.NextDouble() * cfg.SpawnRadius + 6f);
                        Vector3 pos = pp + new Vector3(Mathf.Cos(ang) * dist, 0, Mathf.Sin(ang) * dist);
                        pos = FindGround(world, pos);
                        if (pos != Vector3.zero) return pos;
                        return pp + new Vector3(Mathf.Cos(ang) * dist, 2f, Mathf.Sin(ang) * dist);
                    }
                }
            }
            catch { }

            try
            {
                Vector3 center = Vector3.zero;
                float ang = (float)(Rng.NextDouble() * Math.PI * 2);
                float dist = (float)(Rng.NextDouble() * cfg.SpawnRadius + 4f);
                Vector3 pos = center + new Vector3(Mathf.Cos(ang) * dist, 0, Mathf.Sin(ang) * dist);
                pos = FindGround(world, pos);
                if (pos != Vector3.zero) return pos;
                return pos;
            }
            catch { return Vector3.zero; }
        }

        static Vector3 FindGround(World world, Vector3 pos)
        {
            try
            {
                int x = Mathf.FloorToInt(pos.x);
                int z = Mathf.FloorToInt(pos.z);
                for (int y = 250; y >= 0; y--)
                {
                    var bv = world.GetBlock(new Vector3i(x, y, z));
                    if (bv.type != 0)
                        return new Vector3(pos.x, y + 2f, pos.z);
                }
            }
            catch { }
            return new Vector3(pos.x, 60f, pos.z);
        }

        public static Entity SpawnBotEntity(World world, Vector3 pos, string entityClassName, string botName)
        {
            try
            {
                int classId = EntityClass.FromString(entityClassName);
                if (classId < 0)
                {
                    // Fallback aliases: old default was npcSurvivorRanged which is //commented in vanilla
                    foreach (var alias in new[] { "zombieSoldier", "zombieSoldierFeral", "zombieArlene", "zombieNurse" })
                    {
                        classId = EntityClass.FromString(alias);
                        if (classId >= 0) { ModApi.Log("Entity class '" + entityClassName + "' not found, using fallback '" + alias + "'"); break; }
                    }
                }
                if (classId < 0)
                {
                    ModApi.Log("Unknown entity class: " + entityClassName);
                    return null;
                }
                // 3-arg overload: (classId, pos, rot) exists and is the most reliable on dedi
                Entity e = null;
                try { e = EntityFactory.CreateEntity(classId, pos, Vector3.zero); } catch { }
                if (e == null)
                {
                    try
                    {
                        var ed = EntityFactory.SetupEntityCreationData(classId, pos);
                        try { ed.entityName = botName; } catch { }
                        e = EntityFactory.CreateEntity(ed);
                    }
                    catch { }
                }
                if (e == null) return null;

                // Try to set entityName on creation data style if still default
                // Entity itself has no entityName field; use world spawn path then try EntityName via reflection/game API
                try
                {
                    world.SpawnEntityInWorld(e);
                }
                catch (Exception ex)
                {
                    ModApi.Log("SpawnEntityInWorld failed: " + ex.Message);
                    return null;
                }
                // EntityName is not a direct field on Entity on this build; leave default. We track name in BotManager.
                var ent = world.GetEntity(e.entityId);
                return ent ?? e;
            }
            catch (Exception ex)
            {
                ModApi.Log("SpawnBotEntity failed: " + ex);
                return null;
            }
        }

        public static void ConfigureBotEntity(Entity e, BotConfig cfg)
        {
            try
            {
                if (e is EntityAlive alive)
                {
                    try { alive.Health = Mathf.RoundToInt(cfg.BotHealth); } catch { }
                    if (!string.IsNullOrEmpty(cfg.BotWeapon))
                    {
                        try
                        {
                            ItemValue iv = null;
                            try { iv = ItemClass.GetItem(cfg.BotWeapon, false); } catch { }
                            if (iv == null || iv.type == 0)
                            {
                                var ic = ItemClass.GetItemClass(cfg.BotWeapon, false);
                                if (ic != null) iv = new ItemValue(ic.Id, false);
                            }
                            if (iv != null && iv.type != 0)
                            {
                                var stack = new ItemStack(iv, 1);
                                try { alive.inventory.AddItem(stack); } catch { }
                            }
                        }
                        catch (Exception ex) { ModApi.Log("Give weapon failed: " + ex.Message); }
                    }
                    if (!string.IsNullOrEmpty(cfg.BotAmmo) && cfg.BotAmmoCount > 0)
                    {
                        try
                        {
                            ItemValue iv = null;
                            try { iv = ItemClass.GetItem(cfg.BotAmmo, false); } catch { }
                            if (iv == null || iv.type == 0)
                            {
                                var ic = ItemClass.GetItemClass(cfg.BotAmmo, false);
                                if (ic != null) iv = new ItemValue(ic.Id, false);
                            }
                            if (iv != null && iv.type != 0)
                            {
                                var stack = new ItemStack(iv, cfg.BotAmmoCount);
                                try { alive.bag.AddItem(stack); } catch { }
                                try { alive.inventory.AddItem(stack); } catch { }
                            }
                        }
                        catch { }
                    }
                    try { alive.Buffs.SetCustomVar("botmod_isBot", 1f); } catch { }
                }
            }
            catch (Exception ex) { ModApi.Log("ConfigureBotEntity failed: " + ex.Message); }
        }
    }
}
