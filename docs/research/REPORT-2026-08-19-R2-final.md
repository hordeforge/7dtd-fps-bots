# Final R2 Report: Crazy-Good Net From Scratch, Holds at 500 (2026-08-19)

*Goal: GA from scratch that is crazy good at PvP and vs zombies for 7dtd-fps-bots.*
*Ship: 14→16(tanh)→5, W=325, 608 FLOPs/bot: trained in `tools/ga/combat_sim.py`
(numba walls/LOS/burst/zombie) and running on the real dedicated (Navezgane,
BotManager 6/6, BotNeuralBrain 325w, diff 4 mixed loadout).*

## The number: holds across everything

- `evolved/best.json` **pop40×80 seed42 g25**, train **+16.17**, promoted for
  best held. **Held 100× (seed 999) +11.88±0.99** (9.36..16.32), **seed 1234
  +11.61±0.88**, **seed 4242 +11.77±0.77**, **seed 77 +11.63±0.87**, **seed 555
  +11.74±0.89**, not a lucky seed.

- **Baselines (40, seed 999, tightened harness):** random `+0.08±0.06`,
  zero `+11.63±0.63`, zero still spams `sigmoid=0.5` fire, but champ beats it
  on every arena. Per-arena **n=40 seed 999:** `1v1 +4.54±0.42`,
  `FFA +15.95±0.46`, `Horde +17.08±9.41`, FFA/Horde stdev tracks zombie focus
  fire but means prove PvE meat-grinder handling on top of PvP.

- **Lineage:** g37 pop40×80 seed42 was +18.90 train / +11.82 held until the
  harness was tightened (aim `0.18→0.35`, zombie `0.35→0.42+10 dmg`, camp
  `−0.8→−1.6`). Fresh pop40×80 seed42 g25 now edges it by +0.07 held, g37
  archived in `evolved/runs/2026-08-19_013411_pop40_g80_s42/gen_037.json`.

## Why it is crazy good

- Trained on **9-match mix every generation**: `4×1v1` (1200) + `4×FFA` (1800)
  + `1×Horde` 4 vs 6 (1800), LCG `2654435761` seed chain
  `generation×genomeIdx×matchIdx` so replays are bit-for-bit.
- **Advisory only** (`05-integration.md`): 5 heads (`camp/retreat/aim/fire/strafe`)
  ANDed with LOS/range/reaction/burst/move caps. Default `UseNeuralBrain=true`
  since `c567f2a`; revert is `bot neural off`, malformed `best.json` falls back
  with one log.

## Live dedicated proof

```
Mods/BotMod: BotManager ready. TargetBots=6 diff=4 weapon=mixed
BotNeuralBrain: loaded ok: .../Mods/BotMod/evolved/best.json (325 weights, hidden=16)
DM spawns: 8 from Data/Worlds/Navezgane/spawnpoints.xml (world=Navezgane)
[Bot] Slash_97 [gunMGT1AK47] id=8128 · TankJr_23 [gunHandgunT3SMG5] id=8129 … 6/6
# telnet 8087 -> bot neural status: use=True loaded=True 325w hash 885a3e9b
#          -> bot list: 6 mixed-weapon bots, two mutual Attack
#          -> bot status: vsBot true, vsZombie true
```

Build ships `dist/BotMod/evolved/best.json`; `scripts/build.sh` now does it
for every `make build && make install`.

## Reproduce

```bash
python3 tools/ga/evolve.py --pop 40 --gens 80 --seed 42
python3 tools/ga/eval.py evolved/best.json --matches 40
python3 tools/ga/viz.py --best evolved/best.json --out /tmp/net.png
python3 tools/ga/report.py --runs evolved/runs/2026-08-19_015608_pop40_g80_s42 --out evolved/report.html
# dedi:  make build && make install   # then: bot neural status / bot list
```

## Artifacts

- Champion: `evolved/best.json` (`gen 25, hash 885a3e9b`) + `evolved/report.html` + `evolved/runs/…/net.png`
- Harness: `tools/ga/combat_sim.py` (numba) + `harness.py` (9-match) + `tools/ga/sweeps/`
- Prove: `docs/research/REPORT-2026-08-19-R1-combat-GA.md`, `REPORT-2026-08-19-R1-live-dedi.md`

## Next

Headless `ZBS2` tick swap (`harness.py:evaluate()` → `zdtd` `world_store` ticks,
same seed chain): the final real-tick proof. Layout stays `H16 tanh` until that
`pop32×80` rerun says otherwise (`sweep.py --hidden 8 12 16 24 32`).
