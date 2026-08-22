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

### Changed
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
- Config writes are atomic (temp file + rename) so a crash mid-persist cannot
  tear `botmod.json`; if the primary file is unreadable at load, the mod
  restores the `.bak` last-known-good copy and logs a WARN instead of silently
  resetting all persisted settings to defaults.
- Reinstalling or upgrading via `make install` preserves your operator state:
  `Config/botmod.json` (and its `.bak`) are staged out and restored rather
  than overwritten by the shipped default.
- Server log lines use WARN for degraded-but-running problems and ERR for
  broken functionality; each web API mutation logs one audit line.

### Added
- Optional idempotency key for `POST /api/bot`: send `"requestId":"<unique>"`.
  Retries reusing the key replay the recorded response within the ledger
  retention window instead of executing twice; a concurrent duplicate gets
  `409 REQUEST_IN_PROGRESS`; failures are not cached and may be retried.
  Requests without `requestId` behave exactly as before.

### Performance
- Neural/LOS evaluations memoized per tick and O(1) bot lookup in
  `BotManager`.

### Fixed
- Character lookup strips the `[Bot]` prefix so non-Grunt bot names resolve;
  aggro honors the `bot vs` gates; wandering skips allies and teammates.

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
