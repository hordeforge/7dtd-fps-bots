# `tools/ga/` — Offline Neuroevolution Harness

Training and evaluation tooling for `docs/research/04-training-pipeline.md`,
used for every experiment recorded in `docs/research/REPORT-*.md` (R0..R13).
The trainer is **Python-first** (NumPy + stdlib); fitness comes from the numba
combat sim in `combat_sim.py`, so no 7DTD binary is needed and evolved weights
drop straight into the live mod's `BotNeuralBrain.cs`.

## Layout

```
tools/ga/
  README.md              this file
  ga.py                  genome contract (INPUTS/HIDDEN/OUTPUTS = 14/16/5) +
                         operators: tournament, crossover, mutation
  combat_sim.py          numba-JIT PvP+zombie arena simulation
  harness.py             evaluation loop over combat_sim.py arenas
                         (the headless zdtd-binary bridge was the R0 stub)
  evolve.py              CLI trainer: --pop --gens --seed --resume <runDir>
                         [--islands N] [--curriculum ...] [--activation ...]
  eval.py                re-evaluates a single best.json on the held-out pool
  eval_static_vs_neural.py  canonical promotion gate (static vs neural,
                         seeds/matches on the CLI, prints GOAL MET)
  clone.py               behavioural-cloning warm-start stub (heuristic
                         traces -> cloned net)
  sweep.py               net-layout sweeps (writes tools/ga/sweeps/)
  fitness_sweep.py       scalarization-weight sweep over harness.FIT_*
  plot.py                fitness.csv -> plot.png (best/mean per generation)
  replay.py              match recorder + HTML renderer
  viz.py                 network diagram rendering
  report.py              per-run report.html generator
  dashboard.py           live training dashboard (docs/ga-dashboard.html)
  requirements.txt       numpy, numba, matplotlib (+ optional Pillow)
```

## How to run

Requires Python 3 with NumPy, numba and matplotlib. Pillow is optional
(report.py/dashboard.py use it, when present, to shrink embedded PNGs ~3-4x).
Use a project-local virtualenv so nothing leaks into your system Python:

```bash
python3 -m venv .venv && . .venv/bin/activate
pip install -r tools/ga/requirements.txt   # numpy, numba, matplotlib
```

`evolve.py`/`eval.py` import `combat_sim.py`, which compiles its hot loops with
numba on first use (the first JIT pass takes a few seconds).

```bash
python tools/ga/clone.py --heuristic-traces traces/heur.jsonl --out evolved/clone.json
python tools/ga/evolve.py --pop 32 --gens 40 --seed 42
python tools/ga/eval.py evolved/best.json
python tools/ga/plot.py evolved/runs/<ts>/fitness.csv
```

`evolve --resume evolved/runs/<ts>` replays from the last generation's
checkpoint deterministically (same LCG chain as clanker/zdtd_bot).

## Disk contract

See `evolved/README.md` and `docs/research/04` §3 for the `evolved/runs/<ts>/`
and `best.json` shapes. The flat weight order is `W1 row-maj(16×14) | b1(16) |
W2 row-maj(5×16) | b2(5)` — shared with `BotNeuralBrain.cs`.
