# `tools/ga/`: Offline Neuroevolution Harness

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
                         [--fit-elo/--fit-econ/--fit-surv/--fit-stuck];
                         eval and static-vs-neural subcommands evaluate
                         best.json (canonical promotion gate)
  sweep.py               activation sweep (H16-tanh vs H16-relu; numba bakes
                         H16, so hidden size is not sweepable)
  replay.py              match recorder + HTML renderer
  viz.py                 network diagram rendering
  report.py              per-run report.html generator
  dashboard.py           live training dashboard (docs/ga-dashboard.html)
  requirements.txt       numpy, numba, matplotlib (+ optional Pillow)
```

## Generated HTML

`docs/ga-dashboard.html` is committed: it is the visual record of the runs in
`evolved/runs/`, which a clone does not get. Everything `report.py` writes
(`evolved/report.html`, `evolved/runs/<ts>/report.html`) is git-ignored, being
a few MB of embedded base64 per file and reproducible from a run directory:

```bash
python tools/ga/report.py --runs evolved/runs/<ts> --out evolved/report.html
python tools/ga/dashboard.py --all --out docs/ga-dashboard.html
```

`make lint-html` runs vnu over the tracked HTML only, and warnings fail it, so
regenerating the committed dashboard must keep it warning-clean.

## How to run

Requires Python 3 with NumPy, numba and matplotlib. Pillow is optional
(report.py/dashboard.py use it, when present, to shrink embedded PNGs ~3-4x).
Use a project-local virtualenv so nothing leaks into your system Python:

```bash
uv venv .venv && . .venv/bin/activate
uv pip install -r tools/ga/requirements.txt   # numpy, numba, matplotlib
```

`evolve.py` imports `combat_sim.py`, which compiles its hot loops with
numba on first use (the first JIT pass takes a few seconds).

```bash
python tools/ga/evolve.py --pop 32 --gens 40 --seed 42
python tools/ga/evolve.py --pop 32 --gens 40 --seed 42 --fit-elo 0.65 --fit-econ 0.20
python tools/ga/evolve.py eval evolved/best.json
python tools/ga/evolve.py static-vs-neural --seeds 999 1234 4242 --matches 40
```

`python tools/ga/evolve.py --resume evolved/runs/<ts>` replays from the last
generation's checkpoint deterministically (same LCG chain as clanker/zdtd_bot).
The `eval` subcommand re-evaluates a single best.json on the held-out pool; the
`static-vs-neural` subcommand is the canonical promotion gate (champion vs
static baseline, prints GOAL MET). Both share the one canonical measuring stick
in harness.canonical_scores.

## Disk contract

See `evolved/README.md` and `docs/research/04` §3 for the `evolved/runs/<ts>/`
and `best.json` shapes. The flat weight order is `W1 row-maj(16×14) | b1(16) |
W2 row-maj(5×16) | b2(5)`: shared with `BotNeuralBrain.cs`.
