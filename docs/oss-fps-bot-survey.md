# OSS FPS bot brains beyond Q3/D3 — survey for the 7DTD bots

Companion to [`q3-inspiration-notes.md`](q3-inspiration-notes.md). That doc
covers the id lineage (ioquake3 `botlib` + DOOM 3 AAS) already ported into
`Source/BotMod`. This doc surveys the wider open-source FPS bot landscape for
things worth borrowing next.

**Verification note (2026-08-21):** compiled from knowledge of these well-known
projects. The network was unavailable at survey time, so nothing was re-cloned
or diffed; treat feature claims as directional until a repo is pulled and the
specific file is inspected (the pattern used for `/tmp/ioq3` and `/tmp/d3`).

**Verification pass (2026-08-21, network restored):** Podbot mm was cloned
(`APGRoboCop/podbot_mm`) and Unvanquished's bot source fetched. Corrections and
confirmations:

- Podbot's "camping" is CS-specific bomb/objective defense (`TASK_CAMP` with
  camp vectors and timers), not a generic anti-camping detector. Our bots
  already have an equivalent camp-hold; nothing new to port there.
- Q3 has no reload (infinite ammo), so "cover while reloading" is NOT a Q3
  behavior; it comes from the CS-bot lineage (Podbot's task stack covers
  reload + reposition). It is still a genuine gap in our bots, which reload
  standing in the open.
- Unvanquished confirmed: `src/shared/navgen/navgen.h` includes `Recast.h`,
  `DetourNavMesh.h`, `DetourTileCache.h` — the industry-standard OSS navmesh
  is the real-map navigation model to follow.

**Executed port (2026-08-21):** cover-while-reloading, from the CS-bot lineage:
an empty magazine with a live visible target seeks cover instead of standing
open. Implemented in the clanker heuristic bot (`Bot.cs`, gated by the
path-recalc cadence, reusing `FindCover`) and mirrored in the zdtd guest
(`bot_reload_ticks > 0` joins the cover-seeking branch); both rebuilt.

**Executed borrow (2026-08-21):** the real-map navigation recommendation landed
in zdtd as a host-side nav layer (`src/world/nav.zig`, Recast/Detour-inspired
but lightweight): a 4-block walkability grid over loaded chunks via
`Chunk.standableY`, alloc-free BFS pathfinding (64x64 region cap, 32-waypoint
cap), exposed to the bot guest as a `zdtd.query "path"` response the chase
follows, with direct-steer fallback. Tests green (`zig build test`). The full
Recast/Detour tile mesh remains a future option if the coarse grid proves too
coarse for dense POIs.

## Landscape

| Project | License | Engine lineage | Bot highlight |
|---|---|---|---|
| **Xonotic** (ex-Nexuiz) | GPL | DarkPlaces (Quake) | The flagship OSS FPS bot: navigation waypoints, team AI (formations, cover), skill-scaled aim/reaction, weapon selection |
| **ioquake3 botlib** | GPL | Quake 3 | Our existing base; the parts NOT yet borrowed: the AAS navigation layer and the `ai_dmq3.c` goal system (cover/camp/seek) |
| **OpenArena** | GPL | ioquake3 | Same botlib; a cleaner reference build, no new behavior |
| **Unvanquished** | GPL | Daemon (Tremulous fork) | Bots navigate **Recast/Detour navmeshes** (the industry-standard OSS navmesh); teamplay + commander goals |
| **ET: Legacy** | GPL | Wolfenstein: Enemy Territory | Waypoint-based objective bots; patterns for objective/team modes |
| **Sauerbraten / Red Eclipse** | Zlib | Cube 2 | Dynamic navmesh + compact skill bots; a small, readable reference for navmesh + skill scaling |
| **AssaultCube** | Zlib | Cube 1 | Minimal waypoint bots; the simplest possible waypoint approach |
| **Podbot mm** | GPL | CS 1.6 | Waypoints + **camping detection** + skill-based aim/reaction |
| **RealBot** | GPL | CS 1.6 | Podbot-class; per-bot skill config |
| **Warsow / Tremulous** | GPL | Q3-derived | Same botlib lineage; little new |

## What is actually worth borrowing for the 7DTD bots

Our bots are arena combat brains on a direct-steer movement model in a voxel
world, with two ports (C# `BotMod` heuristic + Zig wasm guest). Ranked by value:

1. **Camping detection (Podbot).** We have a camp-hold behavior but no
   anti-camp detection. Podbot's pattern (flag a bot that stays in one area
   too long, then treat it as a camper) is a small, self-contained, directly
   portable technique for both ports. Cheapest real win.
2. **Navigation for real maps (Unvanquished Recast/Detour; Xonotic waypoints;
   Q3 AAS).** The documented next direction for the bots is real-map geometry
   (R10/R13 residuals). All three are usable models: Recast/Detour is the
   industry standard and engine-agnostic; Xonotic's waypoint system is lighter;
   Q3 AAS is the id-native option and matches the codebase we already ported
   from. This is the big borrow when the real-map harness lands.
3. **Q3 `ai_dmq3.c` goal system (cover/camp/seek).** We borrowed the combat
   layer (aim, attack, view angles) but not the goal layer. The goal state
   machine (cover while reloading, seek when idle, camp when defending) is
   portable and matches our existing structure.
4. **Team AI beyond flanking (Xonotic; Q3 team AI).** We have basic squad
   flanking. Xonotic's formations/cover-for-teammates and Q3's team goals add
   depth if team behavior becomes a priority.
5. **Aim smoothing (Xonotic).** Q3/D3 already give us jitter + challenge-mode
   smoothing; Xonotic's error model is a refinement, not a step change.

Not relevant: rocket-jump/jump-pad navigation (Xonotic), crouch/walker
posture tricks, commander/strategy layers (Unvanquished).

## Recommendation

- For the **current arena bots**: port Podbot-style camping detection and the
  Q3 `ai_dmq3.c` goal layer. Both are small, self-contained, and fit the
  existing two-port structure.
- For the **real-map direction**: pull **Unvanquished's Recast/Detour usage**
  as the navigation model (industry standard, engine-agnostic, well documented)
  and Xonotic's waypoint system as the lighter alternative. When the network is
  back, clone the specific repos and diff the bot files the same way
  `q3-inspiration-notes.md` was built from `/tmp/ioq3` and `/tmp/d3`.
