# AGENTS.md: 7dtd-fps-bots

Dedicated FPS combat bots mod for 7 Days to Die dedicated servers (Quake 3 inspired).

Canonical modding guide: [MODDING_BEST_PRACTICES.md](https://github.com/hordeforge/.github/blob/main/MODDING_BEST_PRACTICES.md)

## Owns

| Owns | Does not own |
|---|---|
| Server-side FPS combat bots (spawn, pathfinding, combat AI, neural brains, Web UI) | LiteNetLib demand bots (`7dtd-loadgen`) |
| Mod-local unit/integration tests for this mod | Stock-fidelity playtest suites (`7dtd-playtest/suites/*.json`) |
| Mod-specific playtest cases via local `IScenarioProvider` when needed | Lab isolation / `sb` CLI (`7dtd-sandbox`) |
| Dedicated-server install surface for Clanker | Anti-cheat evidence pipeline (`7dtd-server-guard`) |

## Scope & Boundaries

- Server-side mod that spawns real FPS bots holding ranged weapons with pathfinding, combat AI, and neural decision controllers.
- Requires Easy Anti-Cheat off (`-noeac`) on the dedicated server.
- Vanilla clients require no client-side mod to play.
- Keep performance overhead bounded: physics raycasts, vision cone calculations, and GA neural network inference must run efficiently within the 20 TPS (50 ms) tick budget.
- Do not add declarative `suites/*.json` here for stock fidelity; those stay in `7dtd-playtest`. Mod-only scenarios stay local via `IScenarioProvider`.

## Known deviations from the root rules

Recorded so they stay visible instead of being rediscovered as "someone forgot".

- **Empty `catch` blocks: 89 sites** (`BotSpawner.cs` 31, `BotBrain.cs` 18,
  `Bot.cs` 17, `BotCombat.cs` 13, `BotPatches.cs` 4, and 6 in the engine-free
  layers). They guard 7DTD/Unity calls whose failure must not abort a bot tick
  or a spawn attempt. The root rule wants each one to name what it swallows and
  to wrap exactly one statement; most name nothing, and many wrap a whole loop.
  Fix them where a swallow can hide a defect (the engine-free
  `Config/`, `Web/`, `Commands/` sites first, since `make test` covers those),
  one file per change, never as a sweep: a bare `catch` removed from the tick
  path is a behavior change, not a comment change.
- **Inline tuning constants** in the AI and spawner code (distances, score
  weights, timings) sit at their use site with a comment rather than as named
  constants. Promote them when the file they live in is next touched for
  behavior.
