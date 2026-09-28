# 🤖 Clanker (7DTD FPS Bots Mod)

> **Part of [HordeForge](https://github.com/hordeforge)**: High-Performance Systems Engineering for 7 Days to Die.

![CI](https://github.com/hordeforge/7dtd-fps-bots/actions/workflows/ci.yml/badge.svg)
![license](https://img.shields.io/github/license/hordeforge/7dtd-fps-bots)
![release](https://img.shields.io/github/v/release/hordeforge/7dtd-fps-bots)
![languages](https://img.shields.io/github/languages/count/hordeforge/7dtd-fps-bots)
![top language](https://img.shields.io/github/languages/top/hordeforge/7dtd-fps-bots)

Server-side mod that spawns real FPS bots in 7 Days to Die dedicated servers. Names are prefixed `[Bot] Grunt_42` so they are instantly distinguishable in the player list and HUD. Bots spawn with weapons, pathfind, hunt and shoot players, zombies and each other. Vanilla clients need no mod. Default 6 mixed-loadout bots, DM spawnpoints, difficulty 0-4.

## What it does

- Keeps `TargetBotCount` bots alive (auto-respawns one per second).
- Each bot bodies up as a **zombie soldier** (`BotEntityClass=mixed` pins the `zombieSoldier` class; the dedi rejects custom SDCS/Npc/Bandit appends with a negative EntityClass id and mod-spawned trader bodies render nothing, so soldiers are the working visible FPS bodies). Bots hold and fire real ranged weapons.
- Weapons from `BotWeapon=mixed` → random from `LoadoutPool` (pistol/shotgun/AK/sniper/auto-shotgun/SMG) with per-weapon `WeaponProfile` (fire rate, burst 1-9, spread, damage, effective range, pellets).
- FPS combat loop (docs/research/00..06): wide `VisionAngle` cone → `Physics.Raycast` + voxel LOS → leading aim (velocity prediction) → burst fire with reaction delay; pellets/headshots via `DamageSourceEntity`.
- **FPS tactics**: active combat-seeking when idle (hunt nearest enemy), weapon-range standoff (snipers work out to their ~70m effective range and backpedal inside ~24m, shotguns close), squad flanking (split around shared target), cover-advance (peek from cover while chasing), instant target re-acquisition after a kill, finish-the-kill (commit when the enemy is critically wounded), wounded-target priority.
- **Neural controller** (optional `UseNeuralBrain`): a GA-evolved `14→16→5` net drives aim-bias/fire/strafe/retreat in every engagement when `evolved/best.json` is loaded (see `tools/ga/`). Heuristic is the fallback.
- Movement: `MoveEntityHeaded` with a manual-position fallback for trader bodies (trader motors ignore the call), continuous `Strafe`/`Backpedal` circling in `Attack`, dodge on hit, unstuck jump.
- DM spawns: reads `Data/Worlds/<World>/spawnpoints.xml` (far-from-players farthest spawn, bot/bot avoidance), falls back to radius jitter near spawn point.
- Per-bot difficulty preset (`Difficulty 0-4`) scales aim jitter, reaction, vision, headshot like Q3 bot `skill`.

## Install

```bash
make build        # mcs (or dotnet SDK) against your Steam Dedicated Managed DLLs
make install      # copies to Dedicated Server Mods/BotMod
make backup       # snapshots Mods/BotMod/Config + evolved champions to backups/<stamp>
# then restart the dedicated server (EAC must be off for code mods)
```

Config is `Mods/BotMod/Config/botmod.json` (repo default `config/botmod.json`; `BOTMOD_CONFIG` overrides the path). Edit and `bot reload` live. EAC off: `<property name="EACEnabled" value="false"/>`. Offline LAN/loadgen clients with synthetic Steam ids additionally need `"AllowSyntheticAuthBypass": true` (off by default; the bypass accepts ids in a fixed test range without Steam auth). `make uninstall` snapshots the config before deleting it; `make backup` (point `BOTMOD_STATE_BACKUP_DIR` off-host) is the copy that also survives host loss, and `make restore SNAPSHOT=backups/<stamp>` puts it back. `make test-recovery` runs the backup, verify, restore and their failure paths against scratch trees, in `make check` and CI. Details in `docs/recovery.md`.

The server needs the TFP Harmony mod (`Mods/0_TFP_Harmony/0Harmony.dll`): BotMod ships no copy and every patch types off HarmonyLib. `make install` refuses to deploy when it is missing, when the payload lacks `ModInfo.xml`, `Config/botmod.json` or `WebMod/bundle.js`, or when an extracted release zip fails its `MANIFEST.sha256`, so a broken install fails at the swap rather than at the next server start.

## Web dashboard

`make build` also compiles the TypeScript panel (`Source/BotMod/WebMod/bundle.ts`)
into `Mods/BotMod/WebMod/`, which the stock web dashboard serves as a **Bot**
sidebar entry (admin login required; hidden while logged out, same pattern as
7dtd-server-apm-bridge):

- Enable / disable bots (persists to the config, applies live)
- Spawn N bots / remove all
- **Spawn near player**: pick an online player (dropdown fed by the API) +
  count + optional weapon -> bots spawn near that player, out-of-sight
  preferred (11-42m via DM spawnpoints with a ~22m sweet spot, else a 14-30m
  ring; same path as `bot player <name>`)
- Toggle **static AI vs GA brain** (`bot neural on/off`, reloads the weights)
- **Squad mode** (`bot team on/off`): all bots become one team and never
  target/damage each other (players/zombies still fair game)
- **Team drag-and-drop**: team buckets (FFA + Team 1..N, `teamCount` default 2)
  with colored headers and member chips; drag a scoreboard row or a chip onto a
  bucket to assign it. Each row also has a team `<select>` as a fallback.
  Assignments key on bot name, persist to the config, and apply to live bots
  immediately (same-team bots never fight; `+`/`−` buttons change `teamCount`,
  "Clear teams" resets).
- **Shoot-at toggles** (`bot vs bot|zombie|player on/off`): which target
  classes bots engage; all three on = free-for-all. Squad mode overrides vs bot.
- Scoreboard: per-bot kills (players/zombies), deaths, score, level, health,
  team (colored dot + select)
- Every command reports its outcome in a result line under the header
  ("Spawn 2 bots: done", or a failure the user dismisses), and the actions
  that remove bots ("Remove all", "Clear teams") ask for a second click to
  confirm. Toggles (Enable/Disable, Skill, Brain, Squad, Shoot at) act on one
  click; the scoreboard scrolls sideways inside its own box on a narrow
  sidebar.

The stock webserver serves `WebMod/bundle.js` and `WebMod/styling.css`
uncompressed, so their shipped sizes are the whole download per panel open:
14,325 and 8,874 bytes. `make check` holds both under a wire budget (14 KiB
and 12 KiB, the initial congestion window) so the panel arrives in one round
trip. State comes from one same-origin `GET /api/bot` polled every 5 s, with
only the fields the panel reads in the response.

API: authenticated `GET /api/bot` (status + online `players` list +
scoreboard), `POST /api/bot` with
`{"action":"enable|disable|spawn|spawnNear|remove|removeOne|skill|neural|team|vs|setTeam|teamCount|clearTeams", ...}`
(`remove` accepts the alias `clear`; permission level 0; `skill` takes
`{"action":"skill","level":0-4}`, same as
`bot skill`). `removeOne` takes
`{"action":"removeOne","entityId":N}` and removes that single bot.
`spawnNear` takes
`{"action":"spawnNear","player":"<name|id>","count":N,"weapon":"<gunId|mixed>"}`
and responds `{"spawned":N,"found":bool,"player":"<name>"}`. `team` takes
`{"action":"team","on":bool}`; `vs` takes
`{"action":"vs","target":"bot|zombie|player","on":bool}`; teams take
`{"action":"setTeam","name":"<botName>","team":N}` (N=0 free-for-all),
`{"action":"teamCount","count":N}`, `{"action":"clearTeams"}`. All persist to
`config/botmod.json` and apply live. World-touching
actions are dispatched to the game's main thread.

Request validation: optional numeric fields (`count`, `level`, `team`) fall
back to their documented defaults only when omitted; a value that is present
but malformed rejects the request with `400` and a named code instead of
executing something else, as do missing required fields (`spawnNear`
`player`, `removeOne` `entityId`, `setTeam` `name`, `vs` `target`) and the
toggles' required `on` flag. Each action checks the field that names its
target before the fields that qualify it, so a body missing several reports
the one identifying the target.
Rejection codes: `INVALID_ACTION`, `INVALID_COUNT`, `INVALID_ENTITY_ID`,
`INVALID_LEVEL`, `INVALID_NAME`, `INVALID_ON`, `INVALID_PLAYER`,
`INVALID_REQUEST_ID`, `INVALID_TARGET`, `INVALID_TEAM`, `INVALID_WEAPON`.
Range clamps match
the console (`count` 1..16, `skill` 0..4, teams 0..8). Send an optional
client-generated `"requestId"` with mutations so a retried POST replays the
recorded response instead of executing twice; a concurrent duplicate gets
`409 REQUEST_IN_PROGRESS`; the same requestId reused for a *different* body
gets `409 REQUEST_ID_REUSED` (a key identifies one request, so the new one
must carry a new key); and a requestId that is present but empty or over
128 chars is rejected `400 INVALID_REQUEST_ID` (your retry protection would
not be active). Failures return a generic `500 ERROR` envelope; detail goes
to the server log only.

Success bodies are per-action and carried in the stock webserver envelope's
`data`; the rejection code above arrives as `meta.errorCode` with the status
code. Every response, on both verbs and for every status, carries
`Cache-Control: no-store`: `GET` is live world state and every `POST` mutates
config or the world, so nothing here may be replayed from a cache.

| action | `data` on success |
|---|---|
| `enable` / `disable` | `{"enabled":bool}` |
| `spawn` | `{"spawned":N}` |
| `spawnNear` | `{"spawned":N,"found":bool,"player":"<resolved name>"}` |
| `remove` / `clear` | `{"removed":N}` |
| `removeOne` | `{"removed":bool,"entityId":N}` |
| `skill` | `{"difficulty":0-4}` (post-clamp value actually applied) |
| `neural` | `{"neural":bool,"loaded":bool,"reason":"<load failure, else empty>"}` |
| `team` | `{"team":bool}` |
| `vs` | `{"vs":"<target class>","on":bool}` |
| `setTeam` | `{"name":"<base name>","team":N}` (post-clamp bucket) |
| `teamCount` | `{"teamCount":N}` (post-clamp value actually applied) |
| `clearTeams` | `{"cleared":true}` |

`removed` is a count on `remove` and a flag on `removeOne`; read it as the
action's own table row says, not as one shared field.

Every `POST` response, on every status, carries `X-BotMod-Request-Id`: the
client's `requestId` when one was sent, else a server-side `auto-N` tag. The
same tag is on the server's audit line for that request, so a rejected call is
traceable to the log entry that recorded it.

`GET` returns the config summary (`enabled`, `alive`, `difficulty`, `neural`,
range/chance settings, the three `botVs*` toggles, `botTeam`, `teamCount`,
`botHealth`, `targetBotCount`, `maxBots`, `neuralLoaded`, `neuralPath`) plus
`players` (`{name, entityId}`) and `bots` (`{name, entityId, team, weapon,
status, health, deaths, zombies, players, score, level, nearestPlayer,
nearestPlayerDist}`; `nearestPlayerDist` is `-1` when no live player is
online). Fields are added over time, so read unknown keys as ignored rather
than absent, and treat a missing optional field the same way.

### Player data

Clanker holds no player records of its own. The only personal data it touches is
what the game already holds for connected players, and only on an admin surface:

- **Read**: online player display name + entity id, read live from the world
  for the dashboard's spawn-near target list, for `bot player <name|id>`, for
  `bot players`, and for the kill feed. All of them are permission level 0 and
  operator-run. A `bot player` miss names no one: it points at `bot players`
  (or the vanilla `lp`) instead of printing the roster of everyone connected.
- **Not stored**: nothing player-derived is written to disk.
  `config/botmod.json` holds bot names, team assignments and tuning only.
- **Not transferred**: the panel is served same-origin, keeps nothing in
  `localStorage`/`sessionStorage`, and loads no third-party script. The mod
  makes no outbound request.
- **In the server log**: player-chosen names appear where gameplay needs them
  (kill feed, spawn-near, admin mutations) and pass through `LogSanitizer`
  (`Source/BotMod/Foundation/LogSanitizer.cs`) so they cannot forge log lines. The
  synthetic-auth bypass (`AllowSyntheticAuthBypass`, off by default) logs the
  connection's entity id only, not the Steam id or client IP
  (`Source/BotMod/Patches/BotPatches.cs`).
- **In memory**: the idempotency ledger caches response bodies, which for
  `spawnNear` include a player name, for at most `Retention` (10 min) and 256
  keys (`Source/BotMod/Web/IdempotencyLedger.cs`).

## Console commands

```
bot help
bot status            # config + alive (class/weapon/diff/vision/attack/BotVs)
bot list              # id, weapon, state, pos, target, hp, burst
bot players           # online players (name#id), the ids `bot player` accepts
bot spawn [n] [x z] [weapon] | bot player <name|id> [n] [weapon]  # e.g. bot spawn 2 gunShotgunT1DoubleBarrel
bot player Kira              # 1 bot near Kira (out-of-sight preferred, ~22m ideal)
bot player Kira 3 gunMGT1AK47 # 3 AK bots near Kira
bot player me               # from in-game console, spawns near you
# note: test/LiteNetLib clients (loadgen bots) have an empty EntityName - match
# them by their numeric entity id (bot player 322) or the literal "EntityPlayer".
bot weapon <gunId|mixed>      # default for next spawns
bot skill <0-4>               # 0 bot, 1 easy, 2 normal, 3 hard, 4 nightmare
bot count <n>                 # keep n alive
bot remove all | bot remove <id>
bot neural <on|off|reload|status>  # toggle/reload the GA-evolved neural controller
bot vs bot|zombie|player <on|off>  # bots shoot that target class (all on = FFA)
bot team <on|off>                  # squad mode: all bots one team, never fight each other
bot team assign <name> <id>        # put that bot on team id (0 = free-for-all)
bot team list | bot team clear     # show / clear team assignments
bot teams <0-8>                    # number of teams (0 = free-for-all only)
bot reload | bot enable | bot disable
```

## Tuning (`config/botmod.json`)

The file is read from `Mods/BotMod/Config/botmod.json` (the repo default is
`config/botmod.json`). Set `BOTMOD_CONFIG` to read and persist a different
path, for a config mounted outside the mod directory; unset or blank means the
path beside the assembly. `characters.json` is looked up next to whichever
botmod.json is in use. If no config file is found at all, the mod logs a WARN
naming the paths it tried and runs on built-in defaults.

To see what the server is actually running, `bot config` prints the file it
read plus the effective values, and the same dump is logged at startup and on
`bot reload`. That dump is post-clamp: it shows values `Normalize` corrected
and the difficulty preset moved, which the file on disk does not.

- `Difficulty` 0-4 drives `AimJitterDegrees`, `ReactionTimeSec`, `HeadshotChance`, `VisionRange/AttackRange` (see `BotConfig.ApplyDifficulty`). A `bot skill` change recomputes them from the values your `botmod.json` carried, so it always moves the whole way: `bot skill 0` then `bot skill 2` really does return to the normal reaction time. Setting `ReactionTimeSec` or `AimJitterDegrees` to something other than the stock value in `botmod.json` pins it and drops it out of the preset.
- Combat feel: `HeadshotChance/HeadshotMultiplier/BurstMin/BurstMax/BurstPauseSec`.
- Announcements/loot: `AnnounceSpawns`, `BotAnnounceKillsInChat` (bot frags to chat), `DropLootOnDeath`.
- `BotEntityClass` (default `mixed` = pinned `zombieSoldier`, the rendering bot bodies), `BotWeapon`/`LoadoutPool`/`BotAmmo`, `BotHealth`.
- `BotVsBot/BotVsZombie/BotVsPlayer` (which classes bots shoot; `bot vs <t> <on|off>`), `BotTeam` (squad mode; `bot team <on|off>`).
- `BotTeamCount` (number of teams, default 2) and `TeamAssignments` (bot base name -> team id; `bot team assign <name> <id>`). Team 0 = free-for-all; same-team bots never fight.
- `VisionRange/VisionAngle/LoseTargetRange/Time`, `AttackRange` per weapon, `StrafeChance/DodgeOnHitChance`.
- `PathRecalcIntervalSec/StuckTimeoutSec/RandomWanderRadius/Interval`, `SpawnRadius/NearPlayerChance/UseSpawnpoints`, `SpawnProtectionSec`.
- `TargetBotCount=6 MaxBots=16`.
- `Seed` (any int, default `12648430` = `0xC0FFEE`) seeds the spawn picks (bot
  name, gun, spawn spot, mixed loadout). Each bot's own decisions come from its
  entity id, so this is the only randomness a world start introduces. It is
  applied once per world start, printed in the `BotManager ready` log line, and
  is not reapplied by `bot reload`, so a run is reproduced by starting the same
  world with the same seed and issuing the same spawns in the same order.
- Neural controller: `UseNeuralBrain` (built-in default `false`; the shipped
  config enables it) and `BotNeuralWeightPath` (default `evolved/best.json`,
  resolved against the mod folder, then the server working directory). A
  weights file that is missing or fails validation logs a WARN and bots fall
  back to the heuristic brain; `bot neural status` shows the reason.
- Personalities: `Config/characters.json` (Q3-style per-name skill blocks,
  see `Source/BotMod/Config/BotCharacter.cs`). A missing or unparseable file
  logs a WARN and bots use built-in default characteristics; misspelled trait
  keys are reported as WARNs and keep the built-in default.

Quake-style names by default: `Grunt/Visor/Ranger/Phobos/Dozer/...` (13 in
`config/characters.json`). Only `AimAccuracy`, `AimSkill`, `Aggression`,
`SelfPreservation` and `Camper` change behavior; the remaining Q3 slots are
carried so the file matches the Q3 bot layout.

Admin mutations persist: `bot enable|disable`, `bot count`, `bot skill`,
`bot weapon`, `bot neural on/off`, `bot vs ...`, `bot team ...`, `bot teams`
and the web API equivalents write the changed key back to
`Config/botmod.json` (atomic write, `.bak` last-known-good), so a restart or
`bot reload` keeps them. Unknown keys in `botmod.json` (e.g. typos) are
reported as a WARN line at load and ignored.

## Development

`make help` lists all targets. `CONTRIBUTING.md` covers the prerequisites, the
commit and changelog conventions, and how a new test suite is registered. The
common loop:

```bash
make test          # C# unit tests (tests/BotMod.Tests, mcs + mono)
make test-list     # names of the individual C# suites
make test SUITE=lcg # one suite (SUITE="lcg bottext" for several)
make build         # full build: BotMod.dll + web bundle into dist/BotMod
make package       # reproducible zip of dist/BotMod -> dist/BotMod-<version>.zip
make verify-reproducible  # build and package twice, then compare bytes
make check         # what CI runs (shellcheck, yamllint, vnu HTML lint, tsc/oxlint/bundle freshness, ruff)
make ci            # the full local gate: make check then make test
make coverage      # line coverage of the pure-BCL suites (needs the dotnet SDK + dotnet-coverage)
```

`make test` with no `SUITE=` runs every C# suite (~1 minute, longer with a game
install); a single suite is a fraction of a second, so name it while iterating.
Suites that need the game DLLs (`neuralfuzz` and later in `make test-list`)
self-skip without a game install and say so.

`make package` refuses to archive a partial payload: a `make build` that
fails after the C# compile leaves a `dist/BotMod` without `WebMod/`, and
that is named rather than shipped. `make package` output is byte-stable:
entry order is sorted, every archive
timestamp is `SOURCE_DATE_EPOCH` (default: the HEAD commit time), and uid/gid
and permissions are normalized. Two packages of the same commit compare equal
with `sha256sum`, regardless of build machine or directory. The zip carries the
repo `LICENSE` and a `MANIFEST.sha256` covering every payload file; run
`sha256sum -c MANIFEST.sha256` inside the extracted directory to verify it
offline, or let `make install` do it for you.
`make verify-reproducible` runs that claim end to end: it builds the payload,
rebuilds it from a different absolute path, diffs the two trees, then packages
twice and compares the archive hashes. It needs the same prerequisites as
`make build`, so CI does not run it.

Released versions and upgrade notes are documented in `CHANGELOG.md`.

CI runs `make check` plus `scripts/test-idempotency.sh`; `make ci` is the same
pair locally. The workflow installs
mono for it, and the pinned ruff and yamllint for `make lint-python` and
`make lint-yaml` via `uv tool install`; `make preflight` names the tools
`make check` needs (shellcheck, yamllint, java, bun, ruff, curl) and where
their pins live, and fails when the installed ruff or yamllint is not the
pinned one, so a version that lints differently than CI is caught before a
push. Locally
`make test` needs mono, and
`make build` needs the game's Managed DLLs (`SEVENDTD_DS_DIR`/`SEVENDTD_GAME_DIR`
override the Steam paths scripts/build.sh probes). After editing
`Source/BotMod/WebMod/bundle.ts`, run `make build` so the committed bundle.js
(the minified emit, regenerated by `scripts/webmod-minify.sh`) passes the
freshness gate and the wire budget in `make check`.

Durability: what state survives which disaster, and the restore steps, are in
`docs/recovery.md` (reinstalls preserve the operator config; persists are
atomic with a `.bak` last-known-good).

## Validation

```bash
make build && make install
./7DaysToDieServer.x86_64 -logfile .scratch/bot.log -quit -batchmode -nographics -dedicated -configfile .scratch/serverconfig.eacoff.xml
# expect:
# [BotMod] BotMod v0.7.1 loading. ModPath=.../Mods/BotMod Enabled=True DedicatedOnly=True AuthBypass=False
# [BotMod] BotManager ready. TargetBots=6 diff=4 weapon=mixed
# [BotMod] DM spawns: 8 from .../Data/Worlds/Navezgane/spawnpoints.xml (world=Navezgane)
# [BotMod] Bot spawned: [Bot] Grunt_42 [gunMGT1AK47] id=xxxx at (163,62,818) (1/6)
# [BotMod] Bots alive: 6/6
```

Damage hook on `EntityAlive.DamageEntity` gates `BotVs*` and teams: bot-on-bot
damage is blocked when `BotVsBot=false`, in squad mode (`BotTeam`), or when both
bots share a nonzero team. The hook also routes hits on bots to `Bot.OnDamaged`
for dodge/aggro swap. Bots respect blocks/doors and are visible to vanilla
clients.
