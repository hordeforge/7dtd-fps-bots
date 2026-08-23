# AGENTS.md: 7dtd-fps-bots

Dedicated FPS combat bots mod for 7 Days to Die dedicated servers (Quake 3 inspired).

Canonical modding guide: [MODDING_BEST_PRACTICES.md](https://github.com/hordeforge/.github/blob/main/MODDING_BEST_PRACTICES.md)

## Scope & Boundaries

- Server-side mod that spawns real FPS bots holding ranged weapons with pathfinding, combat AI, and neural decision controllers.
- Requires Easy Anti-Cheat off (`-noeac`) on the dedicated server.
- Vanilla clients require no client-side mod to play.
- Keep performance overhead bounded: physics raycasts, vision cone calculations, and GA neural network inference must run efficiently within the 20 TPS (50 ms) tick budget.
