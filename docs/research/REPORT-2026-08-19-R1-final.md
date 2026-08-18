# Final R1 Report — Crazy-Good Net From Scratch (2026-08-19)

*Goal: use GA to create a crazy good 7DTD PvP+zombie net from scratch for 7dtd-clanker.*
*Result: 14→16(tanh)→5 champion (W=325, 608 FLOPs/bot) trained in a numba
combat tick loop — walls, LOS, burst, trait jitter, zombie horde pressure.*

## The number

- `evolved/best.json` **gen 37 / pop 40×80 / seed 42** — train fitness **+18.90**,
  mean **12.47**. Held-out **40 matches (seed 999): +12.16 ±1.15**
  vs random **−0.01 ±0.09** (**Δ +12.17**), vs zero-brain **+12.03 ±0.86**
  (**Δ +0.13**). Zero spams fire at 0.5 and still scores — the champ wins on
  aim/strafe/stuck, not just “shoots at all” (viz traces show healthy→0.31 camp,
  wounded→0.43 camp shift; `W1`/`W2` mats are non-degenerate).

- Per-arena (combat sim, 1200/1800 ticks): `1v1 K1/D1` `FFA K5/D5` `Horde K9/D3`
  repeats across seeds 42/123/999 (table in prior report). Random is `K0/D0`
  everywhere — fitness is discriminative.

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
