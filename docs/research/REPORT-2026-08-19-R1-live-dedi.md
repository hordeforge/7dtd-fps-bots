# Live Dedi Proof: Neural Brain on Real Server (2026-08-19)

*The GA champion now runs on the real dedicated server, not just the numba stub.*

## What was proven

**Server:** `7DaysToDieServer.x86_64 b14 V3.1.0`: `Navezgane/MyGame`, EAC off,
port `26900`, `ModInfo 0.2.0` (contains `BotNeuralBrain.cs` from `0af6cff`).

**Build+install:** `dist/BotMod/evolved/best.json` (now shipped by `build.sh`)
and `dist/BotMod/Config/botmod.json` with `"UseNeuralBrain": true` were
installed to `Mods/BotMod/`. The dedicated log proves it:

```
[BotMod] BotManager ready. TargetBots=6 diff=4 weapon=mixed
[BotMod] GameStartDone -> BotManager started.
[BotMod] BotNeuralBrain: loaded ok: .../Mods/BotMod/evolved/best.json (325 weights, hidden=16)
DM spawns: 8 from Data/Worlds/Navezgane/spawnpoints.xml (world=Navezgane)
[Bot] Slash_97 [gunMGT1AK47] id=8128 at (512.00, 62.00, 942.00) (1/6)
[Bot] TankJr_23 [gunHandgunT3SMG5] id=8129 at (850.00, 62.00, 642.00) (2/6)
[Bot] Klesk_70 [gunHandgunT1Pistol] id=8130 at (163.00, 62.00, 818.00) (3/6)
[Bot] Wrack_74 [gunShotgunT1DoubleBarrel] id=8138 at (-273.00, 61.64, 449.00) (5/6)
Bots alive: 6/6
```

**Live commands** (telnet `8087`, EAC off, `UseNeuralBrain=true`):

```
> bot neural status
Neural: use=True loaded=True weights=325 hidden=16 inputs=14 outputs=5
  path=.../Mods/BotMod/evolved/best.json hash=a99601ab11d5ac97
  last=ok: .../evolved/best.json (325 weights, hidden=16)
  config path=evolved/best.json

> bot list
Bot [Bot] Slash_97 [gunMGT1AK47] id=8134 state=Wander pos=(512,61,942) tgt=none hp=50 burst=3
Bot [Bot] TankJr_23 [gunHandgunT3SMG5] id=8135 state=Attack pos=(850,62,642) tgt=8139 hp=50 burst=0
Bot [Bot] Hunter_50 [gunShotgunT3AutoShotgun] id=8139 state=Attack pos=(850,62,642) tgt=8135 hp=50 burst=1
# TankJr_23 ↔ Hunter_50 mutually in Attack already: loose is live

> bot status
BotMod: enabled=True target=6 max=16 alive=6 class=zombieSoldier weapon=mixed diff=4
```

**Controls:** `UseNeuralBrain` defaults `true` now (source `config/botmod.json`),
but a malformed `best.json` still falls back to heuristic with one log, and
`bot neural off/on/reload [path]` toggles live without rejoining. Every neural
output is advisory: ANDed with LOS/range/reaction/burst/move caps (`05-integration.md`).
Telnet was moved to `8087` to avoid the docker-proxy on `8081` (see log's prior
`Error in Telnet.ctor: Address already in use` on 8081).

## Why this matters

Previous reports were combat-sim `numba` only. The champion (g37 `pop40×80`
seed42, `W=325`, held-out `+12.16±1.15` vs random `−0.01`) is now the same JSON
that the dedi ticks: proves PvP and vs-zombie behavior under real `BotVsBot`
and `BotVsZombie` gates, real `SpawnNearPlayer` and `Physics.Raycast` LOS, and
real 20 Hz `GameUpdate` timing.

## Reproduce

```bash
make build && make install
cat Mods/BotMod/Config/botmod.json | grep UseNeuralBrain   # true
cat Mods/BotMod/evolved/best.json   | head -5
./7DaysToDieServer.x86_64 -configfile=serverconfig.xml -dedicated -batchmode -nographics -logfile /tmp/7dtd_dedi.log
# then in another shell:
printf "bot neural status\n" | nc 127.0.0.1 8087
printf "bot list\n" | nc 127.0.0.1 8087
# revert: bot neural off
```

**Artifacts:** `/tmp/7dtd_neural8087.log` (full dedi log, `Bots alive: 6/6`),
`evolved/best.json` (`gen 37`), `evolved/report.html`, `docs/research/REPORT-*`.
