# Evolution Report — R1 Combat GA (2026-08-19)

*First crazy-good network from scratch — real PvP + zombie combat, not logits.*

## Summary

| Item | Value |
|---|---|
| Run dir | `evolved/runs/2026-08-19_013411_pop40_g80_s42/` |
| Params | `pop 40` × `gens 80` × `seed 42` × `W=325` (`14→16(tanh)→5`) |
| Harness | `tools/ga/combat_sim.py` (numba) + `harness.py` (9 matches: 4×1v1, 4×FFA, 1×Horde) |
| Best | **gen 37** · fitness **+18.90** · mean **12.47** · median **12.01** |
| Held-out (30 matches, seed 999) | **+12.09** mean (`stdev 0.50`) vs random **−0.00** (`stdev 0.08`) → **Δ +12.09** |
| Per-arena (seed 0xABCD) | 1v1 F+4.5 K1 D1 · FFA F+15.3 K5 D5 · Horde F+24.6 K9 D3 |
| Random on same arenas | all `F ~0.1` K0 D0 — validates that combat sim is discriminative |

## Charts

- Fitness band: `evolved/report.html` (IQR + mean/median/best, 80 gens).
- Net viz: `evolved/runs/…/net.png` (`W1` 16×14, `W2` 5×16 heatmaps + activation traces).

## What changed vs R0

- Harness replaced the synthetic `synthetic_fitness` stub with a numba tick loop:
  walls (3 segments, LOS-gated), physics-clamped moves, burst/strafe/backpedal,
  `skill_hit_chance` + `trait_jitter` + `aimRaw` penalty, zombie chase + melee,
  scalarized `0.55*elo + 0.25*econ + 0.15*survival - 0.05*stuck` with camp penalty.
  Seed chain stays `generation×genomeIdx×matchIdx` (LCG `2654435761`) so evolution
  is deterministic and `evolve.py` chdir fix ensures `evolved/` resolves to clanker
  root regardless of cwd.

- GA operators unchanged (tournament k=3, uniform crossover 0.6, Gaussian σ=0.05,
  `elite k=2`, rank-norm). They now optimize combat K/D, not logit proximity.

## Controls

- `UseNeuralBrain=false` by default in `config/botmod.json`.
  `bot neural status|on|off|reload [path]` toggles live; a broken `best.json`
  falls back to heuristic with one log. `BotNeuralBrain` still advisory-only
  (every output ANDed with LOS/range/reaction/burst/move caps — `05-integration.md`).

## Reproduce

```bash
python3 tools/ga/evolve.py --pop 40 --gens 80 --seed 42
python3 tools/ga/eval.py evolved/best.json --matches 30
python3 tools/ga/viz.py --run evolved/runs/2026-08-19_013411_pop40_g80_s42 --out /tmp/net.png
python3 tools/ga/report.py --runs evolved/runs/2026-08-19_013411_pop40_g80_s42 --out evolved/report.html
# dedi test drive:
bot neural reload evolved/best.json
bot neural on
# revert: bot neural off
```

## Next gates

- Phase 4 headless swap (zdtd `ZBS2` ticks) + full R1 curriculum sweep.
- H16 vs H08 on the *combat* fitness (not the synthetic stub) to re-pick the
  shipped shape — `sweep.py` is wired for `hidden=8,12,16,24`.
