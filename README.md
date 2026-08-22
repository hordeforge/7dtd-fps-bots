# 7DTD Bot - Dedicated FPS Bots (like Quake 3)

Server-side mod that spawns real FPS bots. Names are prefixed `[Bot] Grunt_42` so they are instantly distinguishable in the player list and HUD. Bots spawn with weapons, pathfind, hunt and shoot players, zombies and each other. Vanilla clients need no mod. Bots obey the same physics/collision/move caps as a normal player (no godmode, no no-clip, same weight/drag/capsule bounds) — only spawn loadout differs. Default 6 mixed-loadout bots, DM spawnpoints, difficulty 0-4.

## What it does

- Keeps `TargetBotCount` bots alive (auto-respawns one per second).
- Each bot bodies up as a **human player model** (`npcTraderJoel` → vanilla `EntityTrader`; the dedi rejects custom SDCS/Npc/Bandit appends with a negative EntityClass id, so we reuse the working vanilla human trader that renders a `player_maleRagdoll` body). Bots hold and fire real ranged weapons.
- Weapons from `BotWeapon=mixed` → random from `LoadoutPool` (pistol/shotgun/AK/sniper/auto-shotgun/SMG) with per-weapon `WeaponProfile` (fire rate, burst 2-9, spread, damage, effective range, pellets).
- FPS combat loop (docs/research/00..06): wide `VisionAngle` cone → `Physics.Raycast` + voxel LOS → leading aim (velocity prediction) → burst fire with reaction delay; pellets/headshots via `DamageSourceEntity`.
- **FPS tactics**: active combat-seeking when idle (hunt nearest enemy), weapon-range standoff (snipers hold ~73m, shotguns close), squad flanking (split around shared target), cover-advance (peek from cover while chasing), instant target re-acquisition after a kill, finish-the-kill (commit when the enemy is critically wounded), wounded-target priority.
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

Config is `Mods/BotMod/Config/botmod.json` (repo default `config/botmod.json`). Edit and `bot reload` live. EAC off: `<property name="EACEnabled" value="false"/>`.

## Web dashboard

`make build` also compiles the TypeScript panel (`Source/BotMod/WebMod/bundle.ts`)
into `Mods/BotMod/WebMod/`, which the stock web dashboard serves as a **Bot**
sidebar entry (admin login required; hidden while logged out, same pattern as
7dtd-apm-bridge):

- Enable / disable bots (persists to the config, applies live)
- Spawn N bots / remove all
- **Spawn near player**: pick an online player (dropdown fed by the API) +
  count + optional weapon -> bots spawn 12-30m from that player, out-of-sight
  preferred (same path as `bot player <name>`)
- Toggle **static AI vs GA brain** (`bot neural on/off`, reloads the weights)
- Scoreboard: per-bot kills (players/zombies), deaths, score, level, health

API: authenticated `GET /api/bot` (status + online `players` list +
scoreboard), `POST /api/bot` with
`{"action":"enable|disable|spawn|spawnNear|remove|neural", ...}`
(permission level 0). `spawnNear` takes
`{"action":"spawnNear","player":"<name|id>","count":N,"weapon":"<gunId|mixed>"}`
and responds `{"spawned":N,"found":bool,"player":"<name>"}`. World-touching
actions are dispatched to the game's main thread.

## Console commands

```
bot help
bot status            # config + alive (class/weapon/diff/vision/attack/BotVs)
bot list              # id, weapon, state, pos, target, hp, burst
bot spawn [n] [x z] [weapon] | bot player <name|id> [n] [weapon]  # e.g. bot spawn 2 gunShotgunT1DoubleBarrel
bot player Kira              # 1 bot near Kira (12-30m, out-of-sight preferred)
bot player Kira 3 gunMGT1AK47 # 3 AK bots near Kira
bot player me               # from in-game console, spawns near you
# note: test/LiteNetLib clients (loadgen bots) have an empty EntityName - match
# them by their numeric entity id (bot player 322) or the literal "EntityPlayer".
bot weapon <gunId|mixed>      # default for next spawns
bot skill <0-4>               # 0 bot, 1 easy, 2 normal, 3 hard, 4 nightmare
bot count <n>                 # keep n alive
bot remove all | bot remove <id>
bot neural <on|off|reload|status>  # toggle/reload the GA-evolved neural controller
bot reload | bot enable | bot disable
```

## Tuning (`config/botmod.json`)

- `Difficulty` 0-4 drives `AimJitterDegrees`, `ReactionTimeSec`, `HeadshotChance`, `VisionRange/AttackRange` (see `BotConfig.ApplyDifficulty`).
- `BotEntityClass` (default `npcTraderJoel`, a human player-model body), `BotWeapon`/`LoadoutPool`/`BotAmmo`, `BotHealth`.
- `VisionRange/VisionAngle/LoseTargetRange/Time`, `AttackRange` per weapon, `StrafeChance/DodgeOnHitChance`.
- `PathRecalcIntervalSec/StuckTimeoutSec/RandomWanderRadius/Interval`, `SpawnRadius/NearPlayerChance/UseSpawnpoints`, `RespawnDelaySec=3/SpawnProtectionSec`.
- `TargetBotCount=6 MaxBots=16`.

Quake-style names by default: `Grunt/Ranger/Phobos/Dozer/...` (12).

## Validation

```bash
make build && make install
./7DaysToDieServer.x86_64 -logfile /tmp/bot.log -quit -batchmode -nographics -dedicated -configfile /tmp/serverconfig.eacoff.xml
# expect:
# [BotMod] BotMod v0.2.0 loading... diff=2 weapon=mixed
# [BotMod] DM spawns: 8 from .../Data/Worlds/Navezgane/spawnpoints.xml (world=Navezgane)
# [BotMod] Bot spawned: [Bot] Grunt_42 [gunMGT1AK47] id=xxxx at (163,62,818) ...
# [BotMod] Bots alive: 6/6
```

Damage hook on `EntityAlive.DamageEntity` gates `BotVs*` and also routes hits on bots to `Bot.OnDamaged` for dodge/aggro swap. Bots respect blocks/doors and are visible to vanilla clients.
