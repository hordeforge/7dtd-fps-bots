# Layout Sweep on Combat Harness: Result (2026-08-19)

*Synthetic-stub sweeps said H08. Combat says something else.*

## Setup

Same combat sim (`tools/ga/combat_sim.py`, numba tick loop), same seed chain
(LCG `2654435761`), one fitness scalar `0.55*elo + 0.25*econ + 0.15*survival
- 0.05*stuck`, 9 matches per genome (4×1v1 + 4×FFA + 1×Horde).

| Sweep | Pop | Gens | Seed | Hidden | Activations | File |
|---|---|---|---|---|---|---|
| R1b-40 | 40 | 40 | 42 | 8,16,16,24 | tanh, relu | `tools/ga/sweeps/sweep_combat_H40_g40_s42.*` |

Sweep tool: `tools/ga/sweep.py --sweep builtin` (spawns one GA per layout,
plots all curves on one chart + JSON, `tools/ga/sweeps/`).

## Result (combat `H40_g40_s42`)

```
 layout        best   mean@last   Δ best-mean   FLOPs/bot   W
 ──────────────────────────────────────────────────────────
 H08-tanh      +12.341  ~11.0        ~1.3        304      165
 H16-relu      +17.991  12.0?        ,          608      325
 H16-tanh      +17.991  12.0?        :          608      325   ← winner (tied, tanh kept)
 H24-tanh      +17.945  ~11.8        ,          912      485
```

On the **toy synthetic stub** the ranking was `H08 > H16 > H24 > H32` by a
wide margin: the stub's decision surface is tiny, so fewer weights converges
faster. On the **combat stub** the ranking inverts in the middle: `H16` now
wins, `H24` trails closely, `H08` drops. The larger net's extra capacity only
pays when the task has actual LOS/wall/burst/zombie pressure.

## Decision stays H16 tanh

- Shipped shape is still `14→16(tanh)→5` (`325` weights, `608` FLOPs/bot,
  `~19 µs` on Mono, `~0.05 µs` more than H08). Doc, `BotNeuralBrain.cs`,
  `ga.py`, and `BotConfig` all say `16`.
- The synthetic H08 win is an artifact of the stub's small surface, not a
  reason to downsize before the real `ZBS2` harness. When the headless harness
  lands, rerun `sweep.py --hidden 8 12 16 24 32 --activations tanh relu
  --pop 32 --gens 80` and let the real sim pick, the ranking will move again
  if it should.
- `relu` vs `tanh` tied on the combat stub too (`17.648` each in the earlier
  60-gen check). Bounded `tanh` stays for small-net stability under mutation
  noise (see `docs/research/01` §9).
- Hard cap for the dedi stays ~600 weights / `W2` so the forward pass never
  becomes an allocation or native-dep story (see `evolved/README.md`).

## Artifacts

- Sweep chart: `tools/ga/sweeps/sweep_H40_g40_s42.png`
- Sweep JSON: `tools/ga/sweeps/sweep_combat_H40_g40_s42.json` (per-gen `best/mean`)
- This report: `docs/research/REPORT-2026-08-19-R1b-layout-sweep-combat.md`
- Repro: `python3 tools/ga/sweep.py --sweep builtin --pop 40 --gens 40 --seed 42`
