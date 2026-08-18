# Report — Harness Ceiling & Zero-Brain Smoke Test (2026-08-19)

*Follow-up to R1-final. A failed push shows the current harness's limits.*

## What we tried

After promoting g37 (pop40×80 seed42, W=325, +18.90 train, held +11.82±1.08
over 30 matches seed999), we ran a fresh GA:

- **pop40×60 seed99 → g59** held **+11.39±0.72** (30) and **+11.40±0.72** (40) —
  worse held-out than g37 despite similar training fitness (+16.39 peak).
  The run is archived at `evolved/runs/2026-08-19_014542_pop40_g60_s99/` and
  *not* promoted. Best.json is restored to g37.

- Tuned the combat harness mid-run (aim penalty 0.18→0.35, zombie speed
  0.35→0.42, `econ` weight tweak) to separate the zero brain. Zero still
  scores **+11.58±0.69** vs champ **+11.82±1.08** — only **Δ +0.24**. The
  zero net (`w=0`, `sigmoid=0.5`, fires at threshold) burns ~60 shots for
  ~30 hits and still rides the econ term.

## Diagnosis

The **combat_sim stub is too kind**: LOS has only 3 walls, movement is free,
and every bot shares the same weapon draw (`bot_weapon=0` pistol). A net that
does “always fire when in range” scores almost as well as one that times aim
and strafe. The GA cannot learn `Camp/Retreat` nuance when the arena never
punishes the camp flag strongly enough.

**This is expected at R1.** The harness is a methodology validator (deterministic
seed chain, 9-match mix, scalarized fitness pipeline, `sweep.py` proving layout
discrimination). The real differentiator is the **headless ZBS2 tick** (`zdtd`
world_store + LOS + real weapon spread). The plan is `docs/research/04` §5:
swap `harness.py:evaluate()`'s `simulate_match` body for that tick loop — one
line change, same GA.

## Controls remain safe

No code change ships to the dedi. `UseNeuralBrain=false` default, `bot neural`
gates stay advisory (all 5 heads ANDed with LOS/range/reaction/burst caps).

## Next

Headless ZBS2 harness swap, then rerun `sweep.py --hidden 8 12 16 24 32` on the
real tick. The report shape (`fitness.csv`, `net.png`, `report.html`) stays
identical so this becomes a one-line changer.

## Reproduce

```bash
python3 tools/ga/evolve.py --pop 40 --gens 60 --seed 99
python3 tools/ga/eval.py evolved/best.json --matches 30
python3 tools/ga/viz.py --best evolved/best.json --out /tmp/net.png
```
