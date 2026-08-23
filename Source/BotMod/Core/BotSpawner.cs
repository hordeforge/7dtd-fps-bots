using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using BotMod.Config;
using UnityEngine;

namespace BotMod.Core
{
    public static class BotSpawner
    {
        // Deterministic LCG (zdtd parity: no wall-clock noise). Spawn helpers advance this
        // monotonically so consecutive `bot spawn` in the same tick still pick distinct
        // names/weapons/spots. Not per-bot (no entity yet) — global is fine for spawns.
        static uint _rng = 0xC0FFEEu;
        static uint RngNext() { _rng = _rng * 1103515245u + 12345u; return _rng; }
        static float Rng01() { return (RngNext() >> 8 & 0x00ffffffu) / 16777216f; }
        static int RngInt(int lo, int hi) { if (hi <= lo) return lo; return lo + (int)((RngNext() >> 8 & 0x00ffffffu) % (uint)(hi - lo)); }
        static int RngPick(int n) { if (n <= 0) return 0; return (int)((RngNext() >> 8 & 0x00ffffffu) % (uint)n); }
        static List<Vector3> _dmSpawns;
        static string _dmSpawnsWorld;

        public static string PickName(BotConfig cfg)
        {
            string raw;
            if (cfg.BotNames == null || cfg.BotNames.Length == 0) raw = "Bot_" + RngInt(1000, 9999);
            else raw = cfg.BotNames[RngPick(cfg.BotNames.Length)] + "_" + RngInt(10, 99);
            // OrdinalIgnoreCase: matches how BaseName strips the tag, so an
            // operator-configured "[bot] x" name is not double-tagged.
            if (raw.StartsWith("[Bot] ", StringComparison.OrdinalIgnoreCase)) return raw;
            return "[Bot] " + raw;
        }
        public static WeaponProfile PickWeapon(BotConfig cfg, string gunOverride = null)
        {
            string pick = gunOverride ?? cfg.BotWeapon;
            if (pick != null && pick != "mixed" && !string.IsNullOrEmpty(pick))
                return WeaponProfile.ForGun(pick, cfg);
            string gun = cfg.LoadoutPool[RngPick(cfg.LoadoutPool.Length)];
            return WeaponProfile.ForGun(gun, cfg);
        }

        // Spawn near a specific player: FPS-like, out-of-sight preferred. DM
        // spawnpoints within 11-42m of the player score best around ~22m; the
        // radial fallback ring is 14-30m; never on top of the player.
        public static Vector3 PickSpawnNearPlayer(World world, EntityPlayer player, BotConfig cfg)
        {
            if (player == null) return PickSpawnPosition(world, cfg);
            Vector3 pp = player.position;
            // Prefer DM spawnpoints that are near but not too near the player
            if (cfg.UseSpawnpoints)
            {
                var dm = GetDmSpawns(world, cfg);
                if (dm != null && dm.Count > 0)
                {
                    Vector3 best = Vector3.zero; float bestScore = float.MinValue;
                    for (int tries = 0; tries < Math.Min(10, dm.Count); tries++)
                    {
                        var cand = dm[RngPick(dm.Count)];
                        float d = Vector3.Distance(cand, pp);
                        if (d < 11f || d > 42f) continue; // not too close / not too far
                        // Prefer out-of-sight spawn (FPS spawn protection)
                        bool los = HasLineOfSightForSpawn(pp + Vector3.up * 1.45f, cand + Vector3.up * 0.5f, world);
                        float score = 0f;
                        if (!los) score += 9f;
                        score += 6f - Math.Abs(d - 22f) * 0.3f; // sweet spot ~22m
                        // Avoid stacking on other bots
                        try { foreach (var b in BotManager.Instance.Bots) { var e = world.GetEntity(b.EntityId) as EntityAlive; if (e != null && Vector3.Distance(cand, e.position) < 9f) score -= 7f; } } catch {}
                        if (score > bestScore) { bestScore = score; best = cand; }
                    }
                    if (best != Vector3.zero)
                    {
                        Vector3 pos = FindGround(world, best);
                        if (pos != Vector3.zero) return pos;
                        return best + Vector3.up * 1f;
                    }
                }
            }
            // Radial fallback: ring around player, try several angles/distances
            for (int attempt = 0; attempt < 12; attempt++)
            {
                float ang = (float)(Rng01() * Math.PI * 2);
                float dist = 14f + (float)Rng01() * 16f; // 14-30m
                Vector3 pos = pp + new Vector3(Mathf.Cos(ang) * dist, 0, Mathf.Sin(ang) * dist);
                pos = FindGround(world, pos);
                if (pos == Vector3.zero) continue;
                if (Vector3.Distance(pos, pp) < 10f) continue;
                // Prefer not in direct sight (so bot doesn't spawn in your face)
                if (HasLineOfSightForSpawn(pp + Vector3.up * 1.45f, pos + Vector3.up * 0.9f, world)) continue;
                if (!IsSpawnClear(world, pos, pp, cfg)) continue;
                return pos;
            }
            // Last resort: any ring even if visible
            for (int attempt = 0; attempt < 8; attempt++)
            {
                float ang = (float)(Rng01() * Math.PI * 2);
                float dist = 16f + (float)Rng01() * 14f;
                Vector3 pos = pp + new Vector3(Mathf.Cos(ang) * dist, 0, Mathf.Sin(ang) * dist);
                pos = FindGround(world, pos);
                if (pos != Vector3.zero && Vector3.Distance(pos, pp) >= 10f && IsSpawnClear(world, pos, pp, cfg)) return pos;
            }
            // Fallback to generic
            return PickSpawnPosition(world, cfg);
        }
        static bool HasLineOfSightForSpawn(Vector3 from, Vector3 to, World world)
        {
            try
            {
                Vector3 dir = to - from; float d = dir.magnitude; if (d < 0.1f) return true; dir /= d;
                Ray ray = new Ray(from, dir);
                if (Physics.Raycast(ray, out RaycastHit hit, d, -1))
                {
                    if (Vector3.Distance(hit.point, to) < 0.8f) return true;
                    var hitEnt = hit.collider != null ? hit.collider.GetComponentInParent<Entity>() : null;
                    if (hitEnt != null) return true;
                    return false;
                }
                // Voxel fallback - cheap
                int steps = Mathf.Clamp(Mathf.RoundToInt(d * 0.9f), 4, 40);
                for (int i = 1; i < steps; i++)
                {
                    Vector3 p = Vector3.Lerp(from, to, (float)i / steps);
                    var bv = world.GetBlock(new Vector3i(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z)));
                    if (bv.type != 0) { var block = Block.list[bv.type]; if (block != null && block.IsCollideMovement) return false; }
                }
                return true;
            } catch { return false; }
        }

        public static Vector3 PickSpawnPosition(World world, BotConfig cfg)
        {
            // DM: pick world spawnpoints first
            if (cfg.UseSpawnpoints)
            {
                var dm = GetDmSpawns(world, cfg);
                if (dm != null && dm.Count > 0)
                {
                    // Farthest-from-players spawn (avoid spawn stacking on someone) - FPS-like farthest spawn
                    Vector3 best = dm[RngPick(dm.Count)]; float bestDist = -1f;
                    List<Vector3> playerPos = new List<Vector3>();
                    try { if (world.Players != null && world.Players.list != null) foreach (var p in world.Players.list) if (p != null && !p.IsDead()) playerPos.Add(p.position); } catch { }
                    if (playerPos.Count == 0) best = dm[RngPick(dm.Count)];
                    else
                    {
                        for (int tries = 0; tries < Math.Min(6, dm.Count); tries++)
                        {
                            var cand = dm[RngPick(dm.Count)];
                            float minDist = float.MaxValue;
                            foreach (var pp in playerPos) minDist = Mathf.Min(minDist, Vector3.Distance(cand, pp));
                            // Also avoid spawning on top of existing bots
                            try
                            {
                                var bots = BotManager.Instance.Bots;
                                foreach (var b in bots) { var e = world.GetEntity(b.EntityId) as EntityAlive; if (e != null) minDist = Mathf.Min(minDist, Vector3.Distance(cand, e.position)); }
                            }
                            catch { }
                            if (minDist > bestDist) { bestDist = minDist; best = cand; }
                        }
                    }
                    Vector3 pos = FindGround(world, best);
                    if (pos != Vector3.zero) return pos;
                    return best + Vector3.up * 1f;
                }
            }
            // Near-player fallback with avoidance
            try
            {
                if (world.Players != null && world.Players.list != null && world.Players.list.Count > 0 && Rng01() < cfg.SpawnNearPlayerChance)
                {
                    var players = world.Players.list;
                    var list = new List<EntityPlayer>(players.Count);
                    foreach (var p in players) if (p != null && !p.IsDead() && p.IsAlive()) list.Add(p);
                    if (list.Count > 0)
                    {
                        var pl = list[RngPick(list.Count)];
                        for (int attempt = 0; attempt < 6; attempt++)
                        {
                            float ang = (float)(Rng01() * Math.PI * 2);
                            float dist = (float)(Rng01() * cfg.SpawnRadius + 10f);
                            Vector3 pos = pl.position + new Vector3(Mathf.Cos(ang) * dist, 0, Mathf.Sin(ang) * dist);
                            pos = FindGround(world, pos);
                            if (pos != Vector3.zero && IsSpawnClear(world, pos, pl.position, cfg)) return pos;
                        }
                    }
                }
            }
            catch { }
            // Near world spawn / 0,0
            for (int a = 0; a < 8; a++)
            {
                float ang = (float)(Rng01() * Math.PI * 2);
                float dist = (float)(Rng01() * cfg.SpawnRadius + 6f);
                Vector3 pos = new Vector3(Mathf.Cos(ang) * dist, 0, Mathf.Sin(ang) * dist);
                pos = FindGround(world, pos);
                if (pos != Vector3.zero) return pos;
            }
            return new Vector3(RngInt(-20, 20), 61f, RngInt(-20, 20));
        }

        static bool IsSpawnClear(World world, Vector3 pos, Vector3 avoid, BotConfig cfg)
        {
            if (Vector3.Distance(pos, avoid) < 8f) return false;
            try
            {
                var bv = world.GetBlock(new Vector3i(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y), Mathf.FloorToInt(pos.z)));
                if (bv.type != 0 && Block.list[bv.type] != null && Block.list[bv.type].IsCollideMovement) return false;
                var bv2 = world.GetBlock(new Vector3i(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y + 1), Mathf.FloorToInt(pos.z)));
                if (bv2.type != 0 && Block.list[bv2.type] != null && Block.list[bv2.type].IsCollideMovement) return false;
            }
            catch { }
            return true;
        }

        static Vector3 FindGround(World world, Vector3 pos)
        {
            try
            {
                int x = Mathf.FloorToInt(pos.x), z = Mathf.FloorToInt(pos.z);
                // Prefer a physics raycast down — finds terrain collider even when
                // voxels are air above a cave (which voxel scan would anchor to cave floor y≈5).
                try
                {
                    Vector3 probe = new Vector3(pos.x, Mathf.Clamp(pos.y + 25f, 60f, 120f), pos.z);
                    Ray ray = new Ray(probe, Vector3.down);
                    if (Physics.Raycast(ray, out RaycastHit hit, 130f, -1))
                    {
                        // hit on terrain/mesh — anchor just above impact
                        if (hit.point.y > 10f) return new Vector3(pos.x, hit.point.y + 1f, pos.z);
                    }
                } catch { }
                // Voxel scan fallback: prefer highest walkable surface, not cave floor.
                int top = Mathf.Clamp(Mathf.FloorToInt(pos.y) + 30, 0, 250);
                for (int y = top; y >= 0; y--)
                {
                    var bv = world.GetBlock(new Vector3i(x, y, z));
                    if (bv.type != 0 && Block.list[bv.type] != null && Block.list[bv.type].IsCollideMovement)
                    {
                        // skip if there's ceiling directly above (inside cave)
                        bool caved = false;
                        try { for (int yy = y + 3; yy <= y + 12 && yy <= 250; yy++) { var b2 = world.GetBlock(new Vector3i(x, yy, z)); if (b2.type != 0 && Block.list[b2.type] != null && Block.list[b2.type].IsCollideMovement) { caved = true; break; } } } catch { }
                        if (caved && y < 20) continue; // cave floor, keep scanning up
                        return new Vector3(pos.x, y + 2f, pos.z);
                    }
                }
                for (int y = top + 1; y <= 250; y++)
                {
                    var bv = world.GetBlock(new Vector3i(x, y, z));
                    if (bv.type != 0) return new Vector3(pos.x, y + 2f, pos.z);
                }
            }
            catch { }
            return Vector3.zero;
        }

        static List<Vector3> GetDmSpawns(World world, BotConfig cfg)
        {
            try
            {
                string worldName = GamePrefs.GetString(EnumGamePrefs.GameWorld) ?? GamePrefs.GetString(EnumGamePrefs.GameName) ?? "";
                if (!string.IsNullOrEmpty(worldName) && worldName == _dmSpawnsWorld && _dmSpawns != null) return _dmSpawns;
                // Try spawnpoints.xml under Data/Worlds/<WorldName> and under saves
                string managed = Path.GetDirectoryName(typeof(World).Assembly.Location) ?? "";
                string dataWorld = Path.Combine(Path.GetDirectoryName(managed) ?? "", "..", "Data", "Worlds", worldName, "spawnpoints.xml");
                // normalize ".." via GetFullPath
                try { dataWorld = Path.GetFullPath(dataWorld); } catch { }
                string[] roots = new[]
                {
                    dataWorld,
                    Path.Combine(GameIO.GetUserGameDataDir(), "GeneratedWorlds", worldName, "spawnpoints.xml"),
                    Path.Combine(GameIO.GetGameDir("Data"), "Worlds", worldName, "spawnpoints.xml"),
                };
                foreach (var path in roots)
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                    var doc = new XmlDocument(); doc.Load(path);
                    var list = new List<Vector3>();
                    foreach (XmlNode n in doc.SelectNodes("//spawnpoint"))
                    {
                        var posAttr = n.Attributes["position"];
                        if (posAttr == null) continue;
                        var parts = posAttr.Value.Split(',');
                        if (parts.Length < 3) continue;
                        // spawnpoints.xml is machine data with dot decimals; parse
                        // invariantly so a comma-decimal host locale cannot reject
                        // every spawnpoint (which silently drops DM spawn selection).
                        if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                            && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y)
                            && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                            list.Add(new Vector3(x, y, z));
                    }
                    if (list.Count > 0) { _dmSpawns = list; _dmSpawnsWorld = worldName; ModApi.Log($"DM spawns: {list.Count} from {path} (world={worldName})"); return list; }
                }
            }
            catch (Exception ex) { ModApi.Log("GetDmSpawns failed: " + ex.Message); }
            // Nothing found for THIS world: return null, never a list memoized
            // under another world's name. Hits above check _dmSpawnsWorld, so
            // the failure path must honor the same keying or bots would spawn
            // at coordinates from a different map. Callers treat null/empty as
            // "no DM spawns" and fall back to the radial ring.
            return null;
        }

        // Default pool: pinned to plain zombieSoldier (the entries are identical,
        // so this is a single class wearing a list). Kept as a pool only so variant
        // classes can be re-added without touching call sites. The vanilla humanoid
        // alternatives are NOT usable on this dedi: npcTraderJoel has a positive id
        // but mod-spawned traders render NOTHING for clients (verified), EntityPlayer
        // classes require the full player join path, and survivor/UMA classes
        // (npcSurvivor*) return negative ids on this dedi build. A custom player-mesh
        // class via the entityclasses patch is the path to true player models, pending
        // the negative-id wall being solved.
        static readonly string[] _botClassPool = new[] { "zombieSoldier", "zombieSoldier", "zombieSoldier", "zombieSoldier", "zombieSoldier", "zombieSoldier", "zombieSoldier", "zombieSoldier", "zombieSoldier", "zombieSoldier" };
        public static Entity SpawnBotEntity(World world, Vector3 pos, string entityClassName, string botName)
        {
            try
            {
                string want = entityClassName ?? "zombieSoldier";
                // BotEntityClass="mixed" -> full pool; any other value passes
                // through as-is (the zombieSoldier branch re-picks from the pool,
                // which is uniform today).
                if (want != null && want.IndexOf("mixed", StringComparison.OrdinalIgnoreCase) >= 0) want = _botClassPool[RngPick(_botClassPool.Length)];
                else if (want == "zombieSoldier") want = _botClassPool[RngPick(3)]; // pool is uniform today; pick kept for when variants return
                int classId = EntityClass.FromString(want);
                if (classId < 0)
                {
                    foreach (var alias in new[] { "zombieSoldier", "zombieBoe", "npcTraderJoel", "npcSurvivorRanged" })
                    {
                        classId = EntityClass.FromString(alias);
                        if (classId >= 0) { want = alias; ModApi.Log("Entity class '" + entityClassName + "' not found, using fallback '" + alias + "'"); break; }
                    }
                }
                if (classId < 0) { ModApi.Log("Unknown entity class: " + (entityClassName ?? "(null)") + " (resolved " + want + ")"); return null; }
                Entity e = null;
                try
                {
                    var ed = EntityFactory.SetupEntityCreationData(classId, pos);
                    try { ed.entityName = botName; } catch { }
                    e = EntityFactory.CreateEntity(ed);
                }
                catch { }
                if (e == null)
                {
                    try { e = EntityFactory.CreateEntity(classId, pos, Vector3.zero); } catch { }
                }
                if (e == null) return null;
                TrySetEntityName(e, botName);
                try { world.SpawnEntityInWorld(e); } catch (Exception ex) { ModApi.Log("SpawnEntityInWorld failed: " + ex.Message); return null; }
                var ent = world.GetEntity(e.entityId);
                if (ent != null) TrySetEntityName(ent, botName);
                return ent ?? e;
            }
            catch (Exception ex) { ModApi.Log("SpawnBotEntity failed: " + ex); return null; }
        }

        public static void ConfigureBotEntity(Entity e, BotConfig cfg, WeaponProfile wp, string botName = null)
        {
            try
            {
                if (e is EntityAlive alive)
                {
                    try { alive.Health = Mathf.RoundToInt(cfg.BotHealth); } catch { }
                    // Give the gun and actually equip it so the Avatar renders it. Without the holding-item write
                    // the inventory has the gun but the model walks empty-handed.
                    if (!string.IsNullOrEmpty(wp.GunId))
                    {
                        try
                        {
                            ItemValue iv = null;
                            try { iv = ItemClass.GetItem(wp.GunId, false); } catch { }
                            if (iv == null || iv.type == 0) { var ic = ItemClass.GetItemClass(wp.GunId, false); if (ic != null) iv = new ItemValue(ic.Id, false); }
                            if (iv != null && iv.type != 0)
                            {
                                var stack = new ItemStack(iv, 1);
                                try { alive.inventory.AddItem(stack); } catch { }
                                // Equip in hand so AvatarSDCS/UMA actually draws the rifle (rifle can't be seen if only in bag)
                                try { alive.inventory.SetHoldingItemIdx(0); } catch { }
                                try { alive.inventory.updateHoldingItem(); } catch { }
                                try { alive.inventory.ForceHoldingItemUpdate(); } catch { }
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
                            if (iv == null || iv.type == 0) { var ic = ItemClass.GetItemClass(cfg.BotAmmo, false); if (ic != null) iv = new ItemValue(ic.Id, false); }
                            if (iv != null && iv.type != 0) { var stack = new ItemStack(iv, cfg.BotAmmoCount); try { alive.bag.AddItem(stack); } catch { } try { alive.inventory.AddItem(stack); } catch { } }
                        }
                        catch { }
                    }
                    // Enforce player-like physics so bots aren't faster/slower or heavier than you.
                    // Match moveSpeed etc to vanilla playerMale defaults; no god/no-clip.
                    try
                    {
                        // Player-ish speeds (vanilla playerMale: moveSpeed 1.0-ish, we use cfg but cap to player bounds)
                        // Don't override A* but ensure not godmode/no-collision
                        try { alive.IsGodMode.Value = false; } catch {}
                        try { alive.IsNoCollisionMode.Value = false; } catch {}
                        try { alive.entityCollisionReduction = 0f; } catch {}
                        // Ensure normal capsule (zombie soldier is taller/wider; force player-like)
                        // We avoid touching physicsRB directly; just ensure weight/drag match player
                        try { alive.weight = 70f; } catch {}
                        // Health/stamina already set above; ensure not cheating with speed
                        try { alive.speedModifier = 1f; } catch {}
                    } catch {}
                    TrySetEntityName(alive, botName);
                    try { alive.Buffs.SetCustomVar("botmod_isBot", 1f); } catch { }
                    try { alive.Buffs.SetCustomVar("botmod_skill", cfg.Difficulty); } catch { }
                }
            }
            catch (Exception ex) { ModApi.Log("ConfigureBotEntity failed: " + ex.Message); }
        }

        static void TrySetEntityName(Entity e, string name)
        {
            if (e == null || string.IsNullOrEmpty(name)) return;
            try
            {
                if (e is EntityAlive alive) { try { alive.SetEntityName(name); return; } catch { } }
                try { e.SetEntityName(name); } catch { }
                // Fallback reflection: field _entityName / entityName
                try
                {
                    var t = e.GetType();
                    var fi = t.GetField("_entityName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                          ?? t.GetField("entityName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    if (fi != null && fi.FieldType == typeof(string)) fi.SetValue(e, name);
                }
                catch { }
            }
            catch { }
        }
    }
}
