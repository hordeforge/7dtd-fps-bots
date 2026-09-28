# AGENTS.md: 7dtd-fps-bots

Clanker, the dedicated-server FPS combat bots mod for 7 Days to Die (Quake 3 inspired).

Canonical modding guide: [MODDING_BEST_PRACTICES.md](https://github.com/hordeforge/.github/blob/main/MODDING_BEST_PRACTICES.md)

## Owns

| Owns | Does not own |
|---|---|
| Server-side FPS combat bots (spawn, pathfinding, combat AI, neural brains, Web UI) | LiteNetLib demand bots (`7dtd-loadgen`) |
| Mod-local unit/integration tests for this mod | Stock-fidelity playtest suites (`7dtd-playtest/suites/*.json`) |
| Mod-specific playtest cases, local to this repo | Lab isolation / `sb` CLI (`7dtd-sandbox`) |
| Dedicated-server install surface for Clanker | Anti-cheat evidence pipeline (`7dtd-server-guard`) |

## Scope & Boundaries

- Server-side mod that spawns real FPS bots holding ranged weapons with pathfinding, combat AI, and neural decision controllers.
- Requires Easy Anti-Cheat off (`<property name="EACEnabled" value="false"/>` in the server config; see README Install).
- Vanilla clients require no client-side mod to play.
- Keep performance overhead bounded: physics raycasts, vision cone calculations, and GA neural network inference must run efficiently within the 20 TPS (50 ms) tick budget.
- Do not add declarative `suites/*.json` here for stock fidelity; those stay in `7dtd-playtest`.

## Source layout

`Source/BotMod/`, one namespace per directory, dependencies pointing down:

| Directory | Holds | May reference |
|---|---|---|
| `Foundation/` | engine-free primitives every layer shares: `Lcg`, `BotText`, `LogSanitizer`, `AtomicTextFile` | nothing else in the mod |
| `Config/` | the operator-config layer: `BotConfig`, `BotCharacter`, `WeaponProfile`, `CombatGates` | `Foundation` |
| `AI/` | decision-making: `BotBrain`, `BotCombat`, `BotNeuralBrain` | `Core`, `Config`, `Foundation` |
| `Core/` | runtime: `Bot`, `BotManager`, `BotSpawner`, `BotModVersion` | `AI`, `Config`, `Foundation` |
| `Commands/`, `Web/` | transport: console commands and the `/api/bot` handler | `Core` and below |
| `Patches/` | Harmony patches onto game types | `Config` |

`ModApi.cs` at the root is the mod entry point (load, config read, Web API
and console command registration). `WebMod/` holds the dashboard's TypeScript,
not C#; `scripts/build.sh` compiles it into the shipped `Mods/BotMod/WebMod/`.

## Commands

- `make check` is what CI runs: shellcheck, yamllint, vnu HTML lint, tsc + oxlint +
  committed-bundle freshness, ruff. `make preflight` names the tools it needs;
  `make ci` is `make check` plus `make test`, the full local gate.
- `make test` is not part of `make check`. CI installs mono and runs
  `bash scripts/test-idempotency.sh` separately. `make test SUITE=<name>` (names
  from `make test-list`) runs one suite, which is the edit-test loop; the script
  fails if its suite list drifts from the `run_suite` calls. `make lint-yaml` is
  `yamllint --strict` over `.github/workflows` (config: `.yamllint.yml`); CI
  installs the pin from `scripts/tool-versions.sh`.
- C# is `net48` at `LangVersion 7.2` (mcs `-langversion:7.2 -warnaserror`).
  Nullable reference types and post-7.2 syntax do not compile; the csproj
  carries the same constraints for the dotnet backend.
- `make build` needs the game Managed DLLs. Override the probe with
  `SEVENDTD_DS_DIR` or `SEVENDTD_GAME_DIR`.
- `make verify-reproducible` builds the payload twice, the second time from a
  different absolute path, then packages twice and compares bytes. Both build
  backends ship no debug symbols; `scripts/build.sh` fails the build if a `.pdb`
  or `.mdb` reaches the payload. CI cannot run it (no game install).
- Suites needing the game install (config parsers, full-mod compile) skip with a
  message instead of failing when no install is found. A skip is not a pass.

## Known deviations from the root rules

Recorded so they stay visible instead of being rediscovered as "someone forgot".

- **Empty `catch` blocks: 80 sites**, counted by
  `rg -U 'catch\s*(\(Exception\))?\s*\{\s*\}' Source/BotMod`
  (`BotBrain.cs` 18, `Bot.cs` 16, `BotSpawner.cs` 31, `BotCombat.cs` 13,
  and 2 in `MainThreadDispatch.cs`). They guard
  7DTD/Unity calls whose failure must not abort a bot tick or a spawn attempt.
  The root rule wants each one to name what it swallows and to wrap exactly one
  statement; most name nothing, and many wrap a whole loop. Fix them where a
  swallow can hide a defect, one file per change, never as a sweep: a bare
  `catch` removed from the tick path is a behavior change, not a comment
  change. `AtomicTextFile.cs`, `IdempotencyLedger.cs`, `BotPatches.cs` and
  `BotConsoleCommands.cs` are done: every site reports through a `Warn` sink
  (`ModApi.Warn` / `ModApi.WarnRateLimited`), wired to `ModApi.Warn` in
  `InitMod`.
- **Inline tuning constants** in the AI and spawner code (distances, score
  weights, timings) sit at their use site with a comment rather than as named
  constants. `BotCombat.cs` and `BotSpawner.cs` declare none at all. Promote
  them when the file they live in is next touched for behavior.
