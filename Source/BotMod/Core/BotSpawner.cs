using System;
using System.Collections.Generic;
using System.IO;
using BotMod.Config;
using BotMod.Foundation;
using UnityEngine;

namespace BotMod.Core
{
    public static class BotSpawner
    {
        // Deterministic LCG (zdtd parity: no wall-clock noise). Spawn helpers advance this
        // monotonically so consecutive `bot spawn` in the same tick still pick distinct
        // names/weapons/spots. Not per-bot (no entity yet), global is fine for spawns.
        static Lcg _rng = Lcg.Seeded((uint)BotConfig.DefaultSeed);
        static float Rng01() { return _rng.Next01(); }
        static int RngInt(int lo, int hi) { return _rng.Range(lo, hi); }
        static int RngPick(int n) { return _rng.Index(n); }

        /// <summary>Reseed the spawn picks (and the mixed-loadout counter) from
        /// <paramref name="seed"/>. Called once per world by
        /// BotManager.OnGameStartDone with the configured BotConfig.Seed, so a
        /// world's spawn sequence is a function of the logged seed plus the
        /// order the spawn requests arrive in, and nothing else. Reseeding on a
        /// config reload instead would shift the stream under a live world, so
        /// it deliberately does not happen there.</summary>
        public static void Reseed(uint seed)
        {
            _rng = Lcg.Seeded(seed);
            WeaponProfile.ReseedPickCounter(seed);
        }
        // Memoized spawnpoints.xml parse, keyed on the world name alone. The
        // name is the whole key, so anything that leaves the folder it was
        // parsed from behind the same name (a regenerated world, a world
        // reload in the same process) keeps serving the old coordinates for
        // the process lifetime. InvalidateDmSpawnCache is the write-side
        // hook: it runs from BotManager.OnWorldShuttingDown, the same
        // teardown that drops the bot registry.
        static List<Vector3> _dmSpawns;
        static string _dmSpawnsWorld;

        /// <summary>Drop the memoized spawnpoints.xml parse. Called when the
        /// world goes away so the next spawn re-reads the file rather than
        /// placing bots at coordinates from the world that just unloaded.</summary>
        public static void InvalidateDmSpawnCache()
        {
            _dmSpawns = null;
            _dmSpawnsWorld = null;
        }

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
        public static WeaponProfile PickWeapon(BotConfig cfg, string gunOverride)
        {
            string pick = gunOverride ?? cfg.BotWeapon;
            if (!string.IsNullOrEmpty(pick) && !pick.Equals(WeaponProfile.Mixed, StringComparison.OrdinalIgnoreCase))
                return WeaponProfile.ForGun(pick, cfg.LoadoutPool);
            string gun = cfg.LoadoutPool[RngPick(cfg.LoadoutPool.Length)];
            return WeaponProfile.ForGun(gun, cfg.LoadoutPool);
        }

        // Spawn near a specific player: FPS-like, out-of-sight preferred. DM
        // spawnpoints within 11-42m of the player score best around ~22m; the
        // radial fallback ring is 14-30m; never on top of the player.
        public static Vector3 PickSpawnNearPlayer(World world, EntityPlayer player, BotConfig cfg)
        {
            if (player == null) return PickSpawnPosition(world, cfg);
            Vector3 pp = player.position;
            // Positions of the bots already up, snapshotted once per call: the
            // candidate loop below scores every spawnpoint against them, and
            // resolving the roster inside that loop repeated a world-dictionary
            // lookup per bot per candidate (10 x bots for one spawn).
            var botPos = BotPositions(world);
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
                        for (int i = 0; i < botPos.Count; i++)
                            if (Vector3.Distance(cand, botPos[i]) < 9f) score -= 7f;
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
            } catch (Exception ex)
            {
                // "In sight", the opposite of the true-fallback BotBrain's LOS
                // uses: both call sites only penalize a positive (spawn
                // protection is off / the candidate is skipped), so a throw here
                // would quietly turn spawn protection off rather than merely
                // losing a candidate. Cheap to keep and worth saying out loud.
                ModApi.WarnRateLimited(() => "spawn line-of-sight check failed, treating the candidate as visible (spawn protection off): " + ex.Message);
                return false;
            }
        }

        /// <summary>Positions of every live bot, resolved once per call. The
        /// spawn pickers score several candidates against the same roster, so
        /// each one snapshots it up front; resolving per candidate made a spawn
        /// cost candidates x bots world-dictionary lookups, which is the N+1 the
        /// picking loops above were shaped around. Returns an empty list (never
        /// null) when the manager or the world lookup throws. That empty list is
        /// indistinguishable from a server with no bots, which silently turns
        /// the farthest-from-anyone pick into a uniform random one and stacks
        /// new bots on the spawnpoint of an existing one, so the failure is
        /// reported rather than absorbed.</summary>
        static List<Vector3> BotPositions(World world)
        {
            var positions = new List<Vector3>();
            try
            {
                foreach (var b in BotManager.Instance.Bots)
                {
                    if (b == null) continue;
                    var e = b.ResolveEntity(world);
                    if (e != null) positions.Add(e.position);
                }
            }
            catch (Exception ex) { ModApi.WarnRateLimited(() => "bot roster snapshot failed, spawn points are chosen without bot avoidance: " + ex.Message); }
            return positions;
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
                    try { if (world.Players != null && world.Players.list != null) foreach (var p in world.Players.list) if (p != null && !p.IsDead()) playerPos.Add(p.position); }
                    catch (Exception ex) { ModApi.WarnRateLimited(() => "player roster snapshot failed, spawnpoints are scored against bots only: " + ex.Message); }
                    // Resolved once, not once per candidate: the try loop scores
                    // every spawnpoint against the same roster snapshot, and
                    // resolving inside it repeated a world-dictionary lookup per
                    // bot per candidate (6 x bots for one spawn).
                    var botPos = BotPositions(world);
                    if (playerPos.Count > 0 || botPos.Count > 0)
                    {
                        for (int tries = 0; tries < Math.Min(6, dm.Count); tries++)
                        {
                            var cand = dm[RngPick(dm.Count)];
                            float minDist = float.MaxValue;
                            for (int i = 0; i < playerPos.Count; i++) minDist = Mathf.Min(minDist, Vector3.Distance(cand, playerPos[i]));
                            // Also avoid spawning on top of existing bots
                            for (int i = 0; i < botPos.Count; i++) minDist = Mathf.Min(minDist, Vector3.Distance(cand, botPos[i]));
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
            // The near-player ring is the whole reason a joiner meets a fight;
            // an exception here drops every spawn to the world-origin ring
            // below with nothing in the log to say the setting was skipped.
            catch (Exception ex) { ModApi.WarnRateLimited(() => "near-player spawn pick failed, falling back to the world-origin ring: " + ex.Message); }
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

        /// <summary>Snap an explicit spawn coordinate (from `bot spawn x z`) to the
        /// terrain surface. Every generated spawn path runs through FindGround; the
        /// console's raw "x, 60, z" triple skipped it, burying bots inside hills or
        /// dropping them from the air wherever the ground is not at y≈60. FindGround
        /// probes downward from the supplied y, so the column is raised first to also
        /// reach terrain above the caller's fallback height; when nothing walkable is
        /// found the original position is returned unchanged.</summary>
        internal static Vector3 GroundPosition(World world, Vector3 pos)
        {
            Vector3 probe = new Vector3(pos.x, Mathf.Max(pos.y, 90f), pos.z);
            Vector3 grounded = FindGround(world, probe);
            return grounded != Vector3.zero ? grounded : pos;
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
            // "Clear" is unverified, not verified: a throwing block query means
            // the candidate was never checked, and the caller spawns there.
            catch (Exception ex) { ModApi.WarnRateLimited(() => "spawn clearance check failed, spawning without it: " + ex.Message); }
            return true;
        }

        static Vector3 FindGround(World world, Vector3 pos)
        {
            try
            {
                int x = Mathf.FloorToInt(pos.x), z = Mathf.FloorToInt(pos.z);
                // Prefer a physics raycast down: finds terrain collider even when
                // voxels are air above a cave (which voxel scan would anchor to cave floor y≈5).
                try
                {
                    Vector3 probe = new Vector3(pos.x, Mathf.Clamp(pos.y + 25f, 60f, 120f), pos.z);
                    Ray ray = new Ray(probe, Vector3.down);
                    if (Physics.Raycast(ray, out RaycastHit hit, 130f, -1))
                    {
                        // hit on terrain/mesh: anchor just above impact
                        if (hit.point.y > 10f) return new Vector3(pos.x, hit.point.y + 1f, pos.z);
                    }
                } catch (Exception ex) { ModApi.WarnRateLimited(() => "ground raycast probe failed, using the voxel scan: " + ex.Message); }
                // Voxel scan fallback: prefer highest walkable surface, not cave floor.
                int top = Mathf.Clamp(Mathf.FloorToInt(pos.y) + 30, 0, 250);
                for (int y = top; y >= 0; y--)
                {
                    var bv = world.GetBlock(new Vector3i(x, y, z));
                    if (bv.type != 0 && Block.list[bv.type] != null && Block.list[bv.type].IsCollideMovement)
                    {
                        // skip if there's ceiling directly above (inside cave)
                        bool caved = false;
                        try { for (int yy = y + 3; yy <= y + 12 && yy <= 250; yy++) { var b2 = world.GetBlock(new Vector3i(x, yy, z)); if (b2.type != 0 && Block.list[b2.type] != null && Block.list[b2.type].IsCollideMovement) { caved = true; break; } } } catch (Exception ex) { ModApi.WarnRateLimited(() => "cave-ceiling scan failed, treating the surface as open to the sky: " + ex.Message); }
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
            // "No ground found" and "the scan threw" are the same answer to every
            // caller: the near-player and origin rings move on, and when all of
            // them come back empty the spawn lands at a hardcoded y=61, dropping
            // the bot out of the sky. Only the exception case is reported, since
            // a legitimately groundless column is a normal outcome.
            catch (Exception ex) { ModApi.WarnRateLimited(() => "ground scan failed, treating the column as groundless: " + ex.Message); }
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
                    // World files are shared, semi-trusted content: SpawnPointXml
                    // loads with DTD and entity resolution off (no file:// read,
                    // no http:// SSRF, no entity-expansion DoS) and drops
                    // non-finite coordinates. A malformed document throws and
                    // is reported by the catch below, which leaves the memoized
                    // list untouched.
                    var points = SpawnPointXml.Parse(File.ReadAllText(path));
                    var list = new List<Vector3>(points.Count);
                    foreach (var p in points) list.Add(new Vector3(p.X, p.Y, p.Z));
                    if (list.Count > 0) { _dmSpawns = list; _dmSpawnsWorld = worldName; ModApi.Log($"DM spawns: {list.Count} from {path} (world={worldName})"); return list; }
                }
            }
            // Rate-limited: the auto-respawn loop re-enters here every second
            // while population is short, so a corrupt spawnpoints.xml would
            // otherwise emit this line once per second indefinitely.
            catch (Exception ex) { ModApi.WarnRateLimited(() => "GetDmSpawns failed: " + ex.Message); }
            // Nothing found for THIS world: return null, never a list memoized
            // under another world's name. Hits above check _dmSpawnsWorld, so
            // the failure path must honor the same keying or bots would spawn
            // at coordinates from a different map. Callers treat null/empty as
            // "no DM spawns" and fall back to the radial ring.
            return null;
        }

        // Single body class, pinned to plain zombieSoldier. The vanilla humanoid
        // alternatives are NOT usable on this dedi: npcTraderJoel has a positive id
        // but mod-spawned traders render NOTHING for clients (verified), EntityPlayer
        // classes require the full player join path, and survivor/UMA classes
        // (npcSurvivor*) return negative ids on this dedi build. A custom player-mesh
        // class via the entityclasses patch is the path to true player models, pending
        // the negative-id wall being solved.
        const string BotClass = "zombieSoldier";
        public static Entity SpawnBotEntity(World world, Vector3 pos, string entityClassName, string botName)
        {
            // Hoisted past the try so the catch below can release a body that
            // was created but never completed its world spawn (see
            // DiscardOrphan); the local name is unchanged for the create block.
            Entity e = null;
            bool spawned = false;
            try
            {
                // "mixed" (the BotEntityClass default) resolves to the single soldier
                // body; any other value passes through as-is.
                string want = entityClassName ?? BotClass;
                if (want.IndexOf("mixed", StringComparison.OrdinalIgnoreCase) >= 0) want = BotClass;
                int classId = EntityClass.FromString(want);
                if (classId < 0)
                {
                    foreach (var alias in new[] { "zombieSoldier", "zombieBoe", "npcTraderJoel", "npcSurvivorRanged" })
                    {
                        classId = EntityClass.FromString(alias);
                        if (classId >= 0) { want = alias; ModApi.WarnRateLimited(() => "Entity class '" + entityClassName + "' not found, using fallback '" + alias + "'"); break; }
                    }
                }
                if (classId < 0) { string named = entityClassName; ModApi.WarnRateLimited(() => "Unknown entity class: " + (named ?? "(null)") + " (resolved " + want + ")"); return null; }
                Exception createEx = null;
                try
                {
                    var ed = EntityFactory.SetupEntityCreationData(classId, pos);
                    try { ed.entityName = botName; } catch { }
                    e = EntityFactory.CreateEntity(ed);
                }
                catch (Exception ex) { createEx = ex; }
                if (e == null)
                {
                    try { e = EntityFactory.CreateEntity(classId, pos, Vector3.zero); } catch (Exception ex) { if (createEx == null) createEx = ex; }
                }
                if (e == null)
                {
                    // Both creation paths failed: surface why here. The caller
                    // only reports the position ("Spawn failed at ..."), so a
                    // broken entity class would otherwise fail invisibly.
                    // Rate-limited with the rest of this spawn path: population
                    // maintenance retries every second, so an unthrottled
                    // warning is a line/s flood until the cause is fixed.
                    string cls = want; int cid = classId;
                    ModApi.WarnRateLimited(() => "CreateEntity failed class=" + cls + " id=" + cid + ": "
                        + (createEx != null ? createEx.Message : "returned null"));
                    return null;
                }
                TrySetEntityName(e, botName);
                try { world.SpawnEntityInWorld(e); spawned = true; }
                catch (Exception ex)
                {
                    ModApi.WarnRateLimited(() => "SpawnEntityInWorld failed: " + ex.Message);
                    DiscardOrphan(world, e);
                    return null;
                }
                var ent = world.GetEntity(e.entityId);
                if (ent != null) TrySetEntityName(ent, botName);
                return ent ?? e;
            }
            catch (Exception ex)
            {
                // A throw anywhere between the create and the completed spawn
                // (the world lookup below can fail too) leaves the caller with
                // a null bot and the body unowned: nothing in BotManager's
                // registry points at it, so only this release path reaches it.
                // After a successful spawn the body is live and deliberately
                // left alone: it is a real bot body, and the caller reached
                // this catch only after the world already took it.
                if (!spawned) DiscardOrphan(world, e);
                ModApi.WarnRateLimited(() => "SpawnBotEntity failed: " + ex);
                return null;
            }
        }

        /// <summary>Release a body that was created but whose world spawn never
        /// completed. CreateEntity allocates the entity; world.SpawnEntityInWorld
        /// is what hands it to the world, and only world.RemoveEntity deallocates
        /// one the world knows about. A throw in between therefore strands it:
        /// the spawn reports failure, the bot is never registered, and the body
        /// runs unmanaged as vanilla AI. MaintainPopulation retries every second
        /// for as long as the fault lasts, so one stranded body per attempt is
        /// unbounded world growth, not a one-off. The world-dictionary check
        /// first: an entity that never got registered has nothing to remove, and
        /// asking to remove it would throw on every failed create. Removal is
        /// best-effort, exactly like the rest of this path, but a throw here is
        /// reported rather than swallowed: it is the one case where the leak
        /// survives.</summary>
        static void DiscardOrphan(World world, Entity e)
        {
            if (world == null || e == null) return;
            try
            {
                if (world.GetEntity(e.entityId) == null) return;
                world.RemoveEntity(e.entityId, EnumRemoveEntityReason.Killed);
            }
            catch (Exception ex)
            {
                ModApi.WarnRateLimited(() => "unspawned bot entity " + e.entityId
                    + " could not be removed and stays in the world: " + ex.Message);
            }
        }

        /// <summary>Item value for an item id, or null when the id resolves to
        /// nothing. GetItem is the cheap lookup; the GetItemClass round trip is
        /// the fallback for ids it rejects. Both lookups can throw on an
        /// unknown id, so each is swallowed and the caller reports the miss.</summary>
        static ItemValue ResolveItem(string itemId)
        {
            try { var iv = ItemClass.GetItem(itemId, false); if (iv != null && iv.type != 0) return iv; } catch { }
            try
            {
                var ic = ItemClass.GetItemClass(itemId, false);
                if (ic != null) { var v = new ItemValue(ic.Id, false); if (v.type != 0) return v; }
            }
            catch { }
            return null;
        }

        /// <summary>Body setup: health, the weapon (resolved to an item
        /// value), the ammo stack, and the player-like physics the soldier
        /// class needs. <paramref name="gun"/> is null when the weapon id
        /// resolved to nothing, which is reported by the weapon branch.</summary>
        public static void ConfigureBotEntity(Entity e, BotConfig cfg, string gunId, string botName)
        {
            try
            {
                if (e is EntityAlive alive)
                {
                    // Every other hpFrac in the mod divides by cfg.BotHealth, so
                    // a rejected write leaves the body on its class default and
                    // the whole difficulty band is wrong for this bot with
                    // nothing in the log to say so.
                    try { alive.Health = Mathf.RoundToInt(cfg.BotHealth); }
                    catch (Exception ex) { ModApi.WarnRateLimited(() => "bot health write failed for " + botName + ", body keeps its class default: " + ex.Message); }
                    // Give the gun and actually equip it so the Avatar renders it. Without the holding-item write
                    // the inventory has the gun but the model walks empty-handed.
                    if (!string.IsNullOrEmpty(gunId))
                    {
                        try
                        {
                            ItemValue iv = ResolveItem(gunId);
                            if (iv != null)
                            {
                                var stack = new ItemStack(iv, 1);
                                // The "weapon not found" branch below only fires on a
                                // lookup miss; a throwing AddItem leaves the same
                                // unarmed body, so it needs the same report.
                                try { alive.inventory.AddItem(stack); }
                                catch (Exception ex) { ModApi.WarnRateLimited(() => "weapon '" + gunId + "' could not be given to " + botName + ", bot is unarmed: " + ex.Message); }
                                // Equip in hand so AvatarSDCS/UMA actually draws the rifle (rifle can't be seen if only in bag)
                                try { alive.inventory.SetHoldingItemIdx(0); } catch { }
                                try { alive.inventory.updateHoldingItem(); } catch { }
                                try { alive.inventory.ForceHoldingItemUpdate(); } catch { }
                            }
                            // Both lookups missed: the bot spawns holding nothing
                            // and the caller logs it as a normal spawn, so the
                            // bad gun id stays invisible until someone wonders
                            // why the bots are unarmed. Rate-limited because a
                            // mistyped BotWeapon repeats on every spawn.
                            else ModApi.WarnRateLimited(() => "weapon '" + gunId + "' not found; bot " + botName + " spawned without a gun");
                        }
                        catch (Exception ex) { ModApi.WarnRateLimited(() => "Give weapon failed: " + ex.Message); }
                    }
                    if (!string.IsNullOrEmpty(cfg.BotAmmo) && cfg.BotAmmoCount > 0)
                    {
                        try
                        {
                            ItemValue iv = ResolveItem(cfg.BotAmmo);
                            if (iv != null)
                            {
                                var stack = new ItemStack(iv, cfg.BotAmmoCount);
                                bool added = false;
                                // Both slots are tried (bag is the fallback when
                                // a full inventory rejects the stack); only when
                                // neither accepted it is the bot really unarmed.
                                try { alive.bag.AddItem(stack); added = true; } catch { }
                                if (!added) try { alive.inventory.AddItem(stack); } catch { }
                                if (!added) ModApi.WarnRateLimited(() => "ammo '" + cfg.BotAmmo + "' x" + cfg.BotAmmoCount + " added to neither bag nor inventory; bot " + botName + " cannot fire");
                            }
                            else ModApi.WarnRateLimited(() => "ammo '" + cfg.BotAmmo + "' not found; bot " + botName + " spawned with no ammo");
                        }
                        catch (Exception ex) { ModApi.WarnRateLimited(() => "Give ammo failed: " + ex.Message); }
                    }
                    // Pin the soldier body to vanilla player-like physics: no god/no-clip,
                    // and the weight/speed of a player rather than of a zombie.
                    try
                    {
                        // Left silent: a body that rejects one of these still
                        // fights and is tracked, it just behaves like the
                        // vanilla soldier in that one respect, and the warn
                        // gate has better uses than per-spawn physics notes.
                        try { alive.IsGodMode.Value = false; } catch {}
                        try { alive.IsNoCollisionMode.Value = false; } catch {}
                        try { alive.entityCollisionReduction = 0f; } catch {}
                        // The soldier capsule is taller/wider than a player's; the physicsRB is
                        // left alone, so the visual mismatch stays but movement matches.
                        try { alive.weight = 70f; } catch {}
                        // Health/stamina already set above; speed stays at the vanilla 1.0.
                        try { alive.speedModifier = 1f; } catch {}
                    }
                    catch (Exception ex) { ModApi.WarnRateLimited(() => "player-physics pinning failed for " + botName + ": " + ex.Message); }
                    TrySetEntityName(alive, botName);
                    // The custom-var marker is the record that survives on the
                    // body itself; a rejected write leaves the bot with no such
                    // mark, which is exactly the sort of thing that is only
                    // noticed weeks later.
                    try { alive.Buffs.SetCustomVar("botmod_isBot", 1f); }
                    catch (Exception ex) { ModApi.WarnRateLimited(() => "botmod_isBot marker write failed for " + botName + ": " + ex.Message); }
                    try { alive.Buffs.SetCustomVar("botmod_skill", cfg.Difficulty); }
                    catch (Exception ex) { ModApi.WarnRateLimited(() => "botmod_skill marker write failed for " + botName + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { ModApi.WarnRateLimited(() => "ConfigureBotEntity failed: " + ex.Message); }
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
