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
# then restart the dedicated server (EAC must be off for code mods)
```

Config is `Mods/BotMod/Config/botmod.json` (repo default `config/botmod.json`). Edit and `bot reload` live. EAC off: `<property name="EACEnabled" value="false"/>`. Offline LAN/loadgen clients with synthetic Steam ids additionally need `"AllowSyntheticAuthBypass": true` (off by default; the bypass accepts ids in a fixed test range without Steam auth).

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
`player`, `removeOne` `entityId`) and the toggles' required `on` flag.
Rejection codes: `INVALID_ACTION`, `INVALID_COUNT`, `INVALID_ENTITY_ID`,
`INVALID_LEVEL`, `INVALID_NAME`, `INVALID_ON`, `INVALID_PLAYER`,
`INVALID_REQUEST_ID`, `INVALID_TARGET`, `INVALID_WEAPON`. Range clamps match
the console (`count` 1..16, `skill` 0..4, teams 0..8). Send an optional
client-generated `"requestId"` with mutations so a retried POST replays the
recorded response instead of executing twice; a concurrent duplicate gets
`409 REQUEST_IN_PROGRESS`, and a requestId that is present but empty or over
128 chars is rejected `400 INVALID_REQUEST_ID` (your retry protection would
not be active). Failures return a generic `500 ERROR` envelope; detail goes
to the server log only.

## Console commands

```
bot help
bot status            # config + alive (class/weapon/diff/vision/attack/BotVs)
bot list              # id, weapon, state, pos, target, hp, burst
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

- `Difficulty` 0-4 drives `AimJitterDegrees`, `ReactionTimeSec`, `HeadshotChance`, `VisionRange/AttackRange` (see `BotConfig.ApplyDifficulty`).
- Combat feel: `HeadshotChance/HeadshotMultiplier/BurstMin/BurstMax/BurstPauseSec`.
- Announcements/loot: `AnnounceSpawns`, `BotAnnounceKillsInChat` (bot frags to chat), `DropLootOnDeath`.
- `BotEntityClass` (default `mixed` = pinned `zombieSoldier`, the rendering bot bodies), `BotWeapon`/`LoadoutPool`/`BotAmmo`, `BotHealth`.
- `BotVsBot/BotVsZombie/BotVsPlayer` (which classes bots shoot; `bot vs <t> <on|off>`), `BotTeam` (squad mode; `bot team <on|off>`).
- `BotTeamCount` (number of teams, default 2) and `TeamAssignments` (bot base name -> team id; `bot team assign <name> <id>`). Team 0 = free-for-all; same-team bots never fight.
- `VisionRange/VisionAngle/LoseTargetRange/Time`, `AttackRange` per weapon, `StrafeChance/DodgeOnHitChance`.
- `PathRecalcIntervalSec/StuckTimeoutSec/RandomWanderRadius/Interval`, `SpawnRadius/NearPlayerChance/UseSpawnpoints`, `SpawnProtectionSec`.
- `TargetBotCount=6 MaxBots=16`.

Quake-style names by default: `Grunt/Ranger/Phobos/Dozer/...` (12).

Admin mutations persist: `bot enable|disable`, `bot count`, `bot skill`,
`bot weapon`, `bot neural on/off`, `bot vs ...`, `bot team ...`, `bot teams`
and the web API equivalents write the changed key back to
`Config/botmod.json` (atomic write, `.bak` last-known-good), so a restart or
`bot reload` keeps them. Unknown keys in `botmod.json` (e.g. typos) are
reported as a WARN line at load and ignored.

## Development

`make help` lists all targets. The common loop:

```bash
make test          # C# unit tests (tests/BotMod.Web.Tests, mcs + mono)
make build         # full build: BotMod.dll + web bundle into dist/BotMod
make package       # reproducible zip of dist/BotMod -> dist/BotMod-<version>.zip
make check         # what CI runs (shellcheck, vnu HTML lint, tsc/oxlint/bundle freshness, ruff)
```

`make package` output is byte-stable: entry order is sorted, every archive
timestamp is `SOURCE_DATE_EPOCH` (default: the HEAD commit time), and uid/gid
and permissions are normalized. Two packages of the same commit compare equal
with `sha256sum`, regardless of build machine or directory. The zip carries a
`MANIFEST.sha256` covering every payload file; run `sha256sum -c
MANIFEST.sha256` inside the extracted directory to verify it offline.

Released versions and upgrade notes are documented in `CHANGELOG.md`.

CI runs `make check` plus `scripts/test-idempotency.sh` (the workflow installs
mono for it, and the pinned ruff for `make lint-python` via pipx); locally
`make test` needs mono, and
`make build` needs the game's Managed DLLs (`SEVENDTD_DS_DIR`/`SEVENDTD_GAME_DIR`
override the Steam paths scripts/build.sh probes). After editing
`Source/BotMod/WebMod/bundle.ts`, run `make build` so the committed bundle.js
passes the freshness gate in `make check`.

Durability: what state survives which disaster, and the restore steps, are in
`docs/recovery.md` (reinstalls preserve the operator config; persists are
atomic with a `.bak` last-known-good).

## Validation

```bash
make build && make install
./7DaysToDieServer.x86_64 -logfile /tmp/bot.log -quit -batchmode -nographics -dedicated -configfile /tmp/serverconfig.eacoff.xml
# expect:
# [BotMod] BotMod v0.4.0 loading. ModPath=.../Mods/BotMod Enabled=True DedicatedOnly=True AuthBypass=False
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
