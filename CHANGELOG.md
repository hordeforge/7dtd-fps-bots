# Changelog

All notable changes to BotMod are documented here. The project is 0.x: minor
releases may add features and behavior changes (including breaking ones),
patch releases are fixes only. Upgrade by replacing `Mods/BotMod/` with the
new build; your operator state in `Config/botmod.json` is preserved and
missing keys fall back to defaults.

The version lives in `Source/BotMod/Core/BotModVersion.cs` (canonical) and
must match `<Version>` in `Source/BotMod/ModInfo.xml`; `scripts/build.sh`
fails on drift between them.

## [Unreleased]

### Added
- `make test` now also pins the deny side of the authorization matrix
  (`tests/BotMod.Web.Tests/WebApiAuthzTests.cs`): `GET/POST /api/bot` must
  keep declaring permission level 0 for every request-method slot, and the
  `bot` console command must keep its default level 0. A change that widens
  either declaration fails the suite instead of silently handing bot control
  to lower-privileged callers.
- `make package` (scripts/package.sh) builds the release zip
  (`dist/BotMod-<version>.zip`) reproducibly: sorted entry order, all
  timestamps pinned to `SOURCE_DATE_EPOCH` (default: HEAD commit time),
  uid/gid stripped and permissions normalized, so two builds of the same
  commit produce identical bytes.
- The release zip carries `MANIFEST.sha256` (sha256 of every payload file,
  `sha256sum -c` format), so an extracted package can be verified offline.

### Changed
- The engine-free Config and AI layers no longer reference the `ModApi`
  entry-point type: `BotCharacterDB` warnings flow through the existing
  `BotConfig.Warn` sink, and neural weight-path resolution reads
  `BotNeuralBrain.ModRoot`, wired once during init. The headless unit suites
  compile without their previous test-only `ModApi` shims.
- Pinned npx tool versions (typescript, oxlint + plugin packages, vnu-jar,
  anti-slop commit) now live in `scripts/tool-versions.sh`, sourced by
  `scripts/build.sh`, `scripts/lint-webui.sh`, and `scripts/lint-html.sh`;
  previously the tsc pin was duplicated between build and lint with only a
  comment keeping them equal. Local env overrides behave as before.
- **The synthetic-id auth bypass is now opt-in** via the new config key
  `"AllowSyntheticAuthBypass"` (default `false`). In every release up to and
  including 0.4.0 this bypass was always on: offline LAN/loadgen clients with
  synthetic Steam ids (76561199000000000..10000) could join an EAC-off server
  running the mod without Steam authentication. After upgrading, such clients
  are rejected until you set `"AllowSyntheticAuthBypass": true` in
  `Config/botmod.json`. Leave it off on any publicly reachable server; the id
  range is predictable.
- `bot enable|disable` persists `Enabled` back to `Config/botmod.json`
  (previously a restart reverted the toggle).
- All admin mutations now persist, closing the same revert-on-restart gap for
  the rest of the surface: `bot count`, `bot skill`, `bot weapon`,
  `bot neural on/off`, plus web API actions `skill` and `neural` write
  `TargetBotCount`, `Difficulty`, `BotWeapon` and `UseNeuralBrain` back to
  `Config/botmod.json`.
- The startup log line names the `AllowSyntheticAuthBypass` state
  (`AuthBypass=True|False`) so an insecure config is visible in the log.
- Config writes are atomic (temp file + rename) so a crash mid-persist cannot
  tear `botmod.json`; if the primary file is unreadable at load, the mod
  restores the `.bak` last-known-good copy and logs a WARN instead of silently
  resetting all persisted settings to defaults.
- Reinstalling or upgrading via `make install` preserves your operator state:
  `Config/botmod.json` (and its `.bak`) are staged out and restored rather
  than overwritten by the shipped default.
- Server log lines use WARN for degraded-but-running problems and ERR for
  broken functionality; each web API mutation logs one audit line.
- `bot spawn <x> <z>` now spawns one bot at those coordinates. Previously the
  first coordinate was misread as a bot count (so `bot spawn 163 818` spawned
  up to 16 bots at a random position) and a trailing token that was neither
  count, coordinate pair nor gun id was silently ignored (`bot spawn 2 abc`,
  `bot player Kira xyz`); every leftover argument is now rejected with a
  usage error naming the offending token.

### Added
- Unknown keys in `Config/botmod.json` are reported as a WARN naming the key
  at load instead of being silently ignored (a misspelled key used to keep
  the built-in default with no signal).
- A missing `characters.json` logs a WARN instead of silently falling back to
  built-in bot characteristics.
- Optional idempotency key for `POST /api/bot`: send `"requestId":"<unique>"`.
  Retries reusing the key replay the recorded response within the ledger
  retention window instead of executing twice; a concurrent duplicate gets
  `409 REQUEST_IN_PROGRESS`; failures are not cached and may be retried.
  Requests without `requestId` behave exactly as before.
- Fuzz suites in `tests/BotMod.Web.Tests`, run by `scripts/test-idempotency.sh`
  (`make test`): a differential model fuzzer hammering the idempotency ledger
  with adversarial `requestId` shapes, clock jitter and capacity/retention
  pressure, a mutation fuzzer for the `evolved/best.json` weights-file parser,
  and `BotConfigLoadTests` pinning unknown-key detection, range clamping and
  `.bak` recovery (the latter two need the game install's Newtonsoft.Json.dll
  and are skipped when it is absent).

### Performance
- Neural/LOS evaluations memoized per tick and O(1) bot lookup in
  `BotManager`.

### Fixed
- Neural weights resolution joins path segments through `System.IO.Path`
  instead of embedding `/` separators, so `evolved/best.json` fallback
  candidates stay the platform API's job on every OS the dedicated server
  ships for.
- Character lookup strips the `[Bot]` prefix so non-Grunt bot names resolve;
  aggro honors the `bot vs` gates; wandering skips allies and teammates.
- `evolved/best.json` files whose `inputs` differs from the frozen v1
  observation layout (14) are rejected at load with a clear reason instead of
  loading successfully and then silently failing every bot tick.
- Null or empty entries in `LoadoutPool`/`BotNames` (hand-edited JSON) are
  dropped at load; previously a `"LoadoutPool": ["gunX", null]` made every
  `mixed` weapon pick throw a NullReferenceException, so spawning and the
  auto-respawn loop failed every second until the config was fixed by hand.
- `bot remove <anything-not-all-or-an-id>` now prints a usage error instead
  of silently removing ALL live bots (a typo like `bot remove al` used to
  wipe the roster).
- The web API's `spawnNear` action rejects an off-grammar `weapon` value with
  `400 INVALID_WEAPON` instead of silently ignoring it and spawning bots with
  random loadouts (same grammar as `bot player ... [weapon]`).

## [0.4.0] - 2026-08-23

Not yet git-tagged; cut from commit f97ab9d.

### Added
- Per-bot teams: assign bots to Team 0..N (0 = free-for-all); same-team bots
  never target or damage each other. Console: `bot team assign <name> <id>`,
  `bot team list`, `bot team clear`, `bot teams <0-8>`. Web API actions
  `setTeam`, `teamCount`, `clearTeams`; `GET /api/bot` status gains
  `teamCount` and per-bot `team`; the dashboard gets drag-and-drop team
  buckets plus a per-row team select.
- Persisted config keys `BotTeamCount` (default `2`) and `TeamAssignments`
  (map of bot base name to team id).

### Compatibility
- Existing `Config/botmod.json` files load unchanged; missing keys take
  defaults (`BotTeamCount=2`, empty assignments). No action needed to upgrade
  from 0.3.x.

## [0.3.0] - 2026-08-22

Tagged v0.3.0.

### Added
- Squad mode: `bot team <on|off>` puts all bots on one team that never fights
  itself (players and zombies unaffected). Persisted as `BotTeam`.
- Per-target toggles: `bot vs bot|zombie|player <on|off>` selects which target
  classes bots engage (all three on = free-for-all). Persisted as `BotVs*`.

## [0.2.0] - 2026-08-22

Tagged v0.2.0.

### Added
- Web dashboard sidebar entry (admin login required): enable/disable, spawn /
  remove all, spawn-near-player with online player dropdown, neural toggle,
  squad/vs toggles, scoreboard. Backed by authenticated `GET /api/bot` and
  `POST /api/bot` (permission level 0).
- `bot player <name|id> [n] [weapon]`: spawn bots 12-30m from a player,
  out-of-sight preferred (`spawnNear` web action mirrors it).
- GA-evolved neural controller: `UseNeuralBrain` + `evolved/best.json` drives
  aim-bias/fire/strafe/retreat in engagements; heuristic fallback when weights
  are absent or incompatible.
- Offline LAN/loadgen support: synthetic Steam ids in the range
  76561199000000000..10000 from loopback could join EAC-off servers without
  Steam authentication. Security note: this shipped always-on; since the
  Unreleased opt-in gate it must be enabled deliberately (see above).

### Changed
- Bot bodies render as `zombieSoldier*` models (trader/survivor bodies do not
  render reliably for clients on dedicated servers).
- Bots appear in the player list and scorecard; bot frags are broadcast to
  player chat.
- Bot names carry the `[Bot]` prefix; the mod runs on dedicated servers only
  by default (`DedicatedOnly`).

## [0.1.0] - 2026-08-12

Untagged initial release.

### Added
- Server-side FPS bots: spawn, pathfind, hunt and shoot players, zombies and
  each other; per-class targeting toggles; real ranged weapons with per-weapon
  profiles.
