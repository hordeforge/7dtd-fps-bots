# Changelog

All notable changes to BotMod are documented here. The project is 0.x: minor
releases may add features and behavior changes (including breaking ones),
patch releases are fixes only. Upgrade by replacing `Mods/BotMod/` with the
new build; your operator state in `Config/botmod.json` is preserved and
missing keys fall back to defaults.

The version lives in `Source/BotMod/Core/BotModVersion.cs` (canonical) and
must match `<Version>` in `Source/BotMod/ModInfo.xml`; `scripts/build.sh`
fails on drift between them.

## [0.5.0] - 2026-08-26

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
- Every `characters.json` load failure mode now reports the reason and the
  file path it probed: an unparseable file logs `parse failed (<path>): ...`
  and a bare JSON `null` body is reported instead of passing silently.
- Misspelled trait keys inside `characters.json` entries (e.g. `"Acuraccy"`)
  are reported as a WARN naming the entry and the key, same contract as the
  botmod.json unknown-key warning; Json.NET silently ignores them otherwise,
  leaving the built-in default in place with no signal.
- `bot spawn <x> <z>` now spawns one bot at those coordinates. Previously the
  first coordinate was misread as a bot count (so `bot spawn 163 818` spawned
  up to 16 bots at a random position) and a trailing token that was neither
  count, coordinate pair nor gun id was silently ignored (`bot spawn 2 abc`,
  `bot player Kira xyz`); every leftover argument is now rejected with a
  usage error naming the offending token.
- A null entry value in `characters.json` (`{"Grunt": null}`) is dropped
  instead of failing the whole file: previously one such entry threw during
  ingestion and every custom personality in the file silently fell back to
  built-in defaults behind a generic parse warning.
- `POST /api/bot` rejects malformed request bodies with named `400 INVALID_*`
  codes (new: `INVALID_COUNT`, `INVALID_ENTITY_ID`, `INVALID_LEVEL`,
  `INVALID_NAME`, `INVALID_ON`, `INVALID_PLAYER`, `INVALID_REQUEST_ID`) where
  it previously silently reinterpreted them:
  - a missing `spawnNear` `player` answered `200 {"found":false}` like a
    departed player instead of flagging the bad request;
  - a missing `removeOne` `entityId` ran a lookup for id 0 and answered
    `200 {"removed":false}`;
  - absent or non-boolean `on` on `neural`/`team`/`vs` read as `false` and
    flipped the live setting with a `200` (a malformed squad-mode call could
    disband teams);
  - present-but-unparseable numeric fields (`count`, `level`, `team`)
    substituted their defaults; omitted fields still take those defaults;
  - a `requestId` that is present but empty or over 128 chars degraded to
    keyless execution, so retries re-executed while the caller believed they
    would replay; it is now rejected so the caller can fix the key.
  Range clamps are unchanged (`count` 1..16, `skill` 0..4, teams 0..8) and
  stay shared with the console command's setters.
- `make lint-html` checks the HTML this repo actually ships (`git ls-files`)
  instead of walking the tree, and warnings now fail it (`vnu --Werror`), not
  just errors. A local `evolved/runs/<ts>/report.html` left over from training
  can no longer fail a gate CI never sees, and the accessibility warnings the
  old gate printed and ignored (missing `lang`, trailing slash on void
  elements) are now build failures.
- CI installs the pinned ruff with `uv tool install` via `astral-sh/setup-uv`
  (SHA-pinned like every other action) instead of relying on the runner image
  shipping pipx.
- The GA tools resolve the repo root by walking up for the root `Makefile`
  (`tools/ga/paths.py`) instead of counting `..` from `__file__`; moving a
  script one directory deeper used to silently point `evolved/runs/...` at the
  wrong tree.
- `report.py` output no longer embeds the absolute path of the machine that
  generated it, and declares `<html lang="en">`; `dashboard.py` renders a
  missing run-config key as `n/a` rather than `None` or an em dash, so a value
  the run never recorded cannot read as a measured one.

### Fixed
- A failed `characters.json` reload (missing, unparseable, or null body) now
  rebuilds the pristine default table like the missing-file case already did.
  Previously an exception kept the previous load's instances untouched while
  a null body re-applied the difficulty lerp onto them, so every `bot reload`
  while the file stayed broken marched aim accuracy toward 1.0 and reaction
  time toward its floor; both paths now converge on the documented defaults.
- Target selection's finish-the-wounded bias now scales target health as a
  fraction of `BotHealth` like every other health fraction in the mod;
  previously it divided by a hardcoded 100, so with a tuned `BotHealth`
  (config accepts 10..10000) wounded-target preference was mis-scaled by up
  to 100x and bots effectively ignored who was hurt.
- `bot count`, `bot skill`, `bot teams` and `bot team assign` parse their
  numeric argument in the invariant culture (entity ids and coordinates
  already did): under a comma-decimal host locale these commands no longer
  depend on `int.TryParse`'s current-culture behavior.
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
- `evolved/report.html`, `evolved/sweeps/report_*.html` and the generated
  `dist/BotMod/Config/entityclasses.xml` are no longer committed (4.6 MB of
  regenerable artifacts, two of which embedded the generating machine's home
  directory). `evolved/README.md` and `tools/ga/README.md` document how to
  regenerate them; `docs/ga-dashboard.html` stays committed because the
  `evolved/runs/` data behind it does not ship.

### Performance
- Neural/LOS evaluations memoized per tick and O(1) bot lookup in
  `BotManager`.

## [0.4.0] - 2026-08-23

Tagged `v0.4.0` at commit a4a8bf3. The `v0.4.1` tag and GitHub release
point at that same commit and ship 0.4.0; no 0.4.1 build exists.

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
