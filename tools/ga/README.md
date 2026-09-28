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
                         [--label <name>] [--islands N] [--curriculum ...]
                         [--activation ...]
                         [--fit-elo/--fit-econ/--fit-surv/--fit-stuck];
                         eval and static-vs-neural subcommands evaluate
                         best.json (canonical promotion gate)
  sweep.py               activation sweep (H16-tanh vs H16-relu; numba bakes
                         H16, so hidden size is not sweepable)
  replay.py              match recorder + HTML renderer
  viz.py                 network diagram rendering
  theme.py               palette, type scale and page CSS for the HTML below
  report.py              per-run report.html generator
  dashboard.py           live training dashboard (docs/ga-dashboard.html)
  determinism_check.py   runs the stack twice from one seed and diffs it
  requirements.txt       numpy, numba, matplotlib (+ optional Pillow)
```

## Determinism

A run is a function of `--seed` alone: one `numpy.random.Generator` per
process feeds selection, crossover, mutation, ring migration and HOF
re-injection, and every arena sim is keyed by a hash of
`(run_seed, generation, genome, match)`. Checkpoints carry that Generator's
state, so `--resume` continues the interrupted run instead of drawing a new
sequence, and two runs of the same seed produce byte-identical run dirs.

`--label <name>` replaces the UTC run-dir stamp when you want a replay to land
in a path the seed picks instead of the one the clock did. To check the claim
rather than trust it:

```bash
python tools/ga/determinism_check.py
python tools/ga/evolve.py --pop 8 --gens 6 --seed 7 --label a
python tools/ga/evolve.py --pop 8 --gens 6 --seed 7 --label b
diff -r evolved/runs/a_pop8_g6_s7 evolved/runs/b_pop8_g6_s7   # no output
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

The output is one file: the charts and the `--replays` payloads are embedded,
not linked, so the dashboard can be opened from disk or attached as is. Each
replay is decoded into its iframe when that card scrolls into view (a browser
without `IntersectionObserver` mounts them all at once), and the payload
script sits at the end of the document so the sections above it are parsed
first.

Both pages take their look from `theme.py`, which is the one place a color or
a size is set: a warm near-black console face for the dashboard, the same
tokens on warm paper for the report, a single rust hue for anything a reader
should look at first, and mono for every number. `report.py` imports the same
constants for its matplotlib series, so a chart and the page under it cannot
drift apart. Change the value in `theme.py`, rebuild both pages; do not
hardcode a hex in either generator.

## How to run

Requires Python 3 with NumPy, numba and matplotlib. Pillow is declared in
`requirements.txt` but only ever an optimization: report.py/dashboard.py use
it, when present, to shrink embedded PNGs ~3-4x, and fall back to plain RGBA
without it. The numpy cap in that file is below 2.0 on purpose (NumPy 2 moved
the Generator streams these runs are seeded from). Use a project-local
virtualenv so nothing leaks into your system Python:

```bash
uv venv .venv && . .venv/bin/activate
uv pip install -r tools/ga/requirements.txt   # numpy, numba, matplotlib, Pillow
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
generation's checkpoint deterministically (see Determinism above).
The `eval` subcommand re-evaluates a single best.json on the held-out pool; the
`static-vs-neural` subcommand is the canonical promotion gate (champion vs
static baseline, prints GOAL MET). Both share the one canonical measuring stick
in harness.canonical_scores.

## Exit codes

All six CLIs follow the same convention, and each one documents it under
`--help`:

| Code | Meaning |
|---|---|
| 0 | the command ran and did what it says |
| 1 | the command understood its arguments but could not finish: a missing or malformed `best.json`, a missing run dir, a failed promotion gate, a sweep with no usable curve |
| 2 | bad command line: unknown flag, missing required argument, or a value outside its documented range |

Data (scores, ranking tables, output paths) goes to stdout; diagnostics and
failures go to stderr, so `evolve.py static-vs-neural | tee gate.txt` stays
parseable and a CI step can branch on the exit code.

## Disk contract

See `evolved/README.md` and `docs/research/04` §3 for the `evolved/runs/<ts>/`
and `best.json` shapes. The flat weight order is `W1 row-maj(16×14) | b1(16) |
W2 row-maj(5×16) | b2(5)`: shared with `BotNeuralBrain.cs`.
