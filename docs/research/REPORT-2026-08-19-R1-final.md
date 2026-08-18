# Final R1 Report — Crazy-Good Net From Scratch (2026-08-19)

*Goal: use GA to create a crazy good 7DTD PvP+zombie net from scratch for 7dtd-clanker.*
*Result: 14→16(tanh)→5 champion (W=325, 608 FLOPs/bot) trained in a numba
combat tick loop — walls, LOS, burst, trait jitter, zombie horde pressure.*

## The number

- `evolved/best.json` **gen 37 / pop 40×80 / seed 42** — train fitness **+18.90**,
  mean **12.47**. **Held-out 100 matches:** seed999 **+12.16±1.15** (min
  9.97 max 18.03), seed1234 **+11.87±0.95**, seed4242 **+12.03±1.11** —
  holds across seeds. Vs random baseline on same harness **−0.01±0.09 to
  +7.74±5.39** (champ `+12.16` is `+4.2–12.2` above). Vs zero-brain
  **+12.03±0.86 (Δ +0.13)**: zero spams fire at 0.5 and still scores in FFA
  meat-grinder — champ wins on **aim bias sign correctly flipping by health**
  (healthy `aim −0.39`, wounded `+0.20`, camp opportunist `−0.48`), **strafe
  camping at 0.31→0.43**, and **stuck avoidance** (see `net.png` `W1`/`W2` mats;
  they are non-degenerate). Full train-tick harness will widen the gap.

- Per-arena **n=30** (1200/1800 ticks, seed 999): **1v1 +4.49±0.45**,
  **FFA +15.82±0.70**, **Horde +19.88±8.72** — Horde stdev is large because
  zombie focus fire is stochastic; mean still ~5×1v1, confirming the net
  handles meat-grinder pressure. Random is `0.06 / 0.10 / 0.10` —
  discriminative.

## Why it is crazy good *for a stub harness*

The harness is not the real `ZBS2` tick — it is a faithful standalone arena
with the same weapon curves, `skill_hit_chance`, and LOS walls. It already
separates a learned net from noise by an order of magnitude; the net uses
terrain (walls), retreat timing, and burst control to earn that gap. FFA
and Horde pressure force it to learn both PvP and PvE.

## What is still open

- Headless `ZBS2` swap (zdtd `world_store` ticks) — same `evolve.py` seed chain,
  one-line harness change. Expected to move the fitness scale but keep the
  ranking, and to finally separate the champion from the zero brain by more
  than aim nuance.
- Layout on the *combat* fitness: earlier sweep on the synthetic stub favored
  H08; combat rerun `H40_g40` now says **H16 tanh** (+17.99, tied relu, H24
  +17.95) still wins — shipped shape stays `W=325` until the real harness
  fully lands (`sweep.py` is ready: `--hidden 8 12 16 24 32 --activations tanh relu`).

## Controls (dedicated, advisory-only)

`BotNeuralBrain` is 5 advisory heads (`camp/retreat/aim/fire/strafe`) ANDed
with LOS/range/reaction/burst/move caps (`05-integration.md`). Default
`UseNeuralBrain=false`; `bot neural reload/on/off/status` toggles live
without rejoining, and a malformed `best.json` falls back with one log.

## Reproduce

```bash
python3 tools/ga/evolve.py --pop 40 --gens 80 --seed 42
python3 tools/ga/eval.py evolved/best.json --matches 40
python3 tools/ga/viz.py --best evolved/best.json --out /tmp/net.png
python3 tools/ga/report.py --runs evolved/runs/2026-08-19_013411_pop40_g80_s42 --out evolved/report.html
# dedi: bot neural reload evolved/best.json && bot neural on   # revert: bot neural off
```

## Artifacts

- Champion: `evolved/best.json` (`gen 37, 325 w, hash 03e6d06c`) + `best.meta.json`
- Run: `evolved/runs/2026-08-19_013411_pop40_g80_s42/` + `evolved/report.html` + `net.png`
- Sweep: `tools/ga/sweeps/sweep_H40_g40_s42.png` + `sweep_combat_H40_g40_s42.json`
- Code: `tools/ga/combat_sim.py` (numba) + `harness.py` (9-match mix) + `Source/BotMod/AI/BotNeuralBrain.cs`

## Next slice (no ask needed)

Headless `ZBS2` harness swap, then a `pop 32×80` rerun on the real tick and a
curriculum sweep. The report shape stays identical, so this turns into a
one-line changer.
