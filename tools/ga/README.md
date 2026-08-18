# `tools/ga/` — Offline Neuroevolution Harness

Phase R0 → R1 tooling for `docs/research/04-training-pipeline.md`. The trainer is
**Python-first** (NumPy + stdlib); a Zig trainer with the same protocol can be
added later. The harness reuses the game's own sense (`ZBS2`) and `bot <verb>`
commands, so evolved weights transfer with no sim-to-real gap.

## Layout

```
tools/ga/
  README.md        this file
  evolve.py        CLI: --pop --gens --seed --resume runs/<ts>
  eval.py          re-evaluates a single best.json on the held-out pool
  clone.py         behavioural-cloning warm-start (heuristic traces → cloned net)
  plot.py          fitness.csv → plot.png + Pareto scatter
  ga.py            operators: tournament, crossover, mutation (imported by evolve)
  harness.py       sense/act bridge to the headless zdtd binary (stubbed for R0)
  requirements.txt numpy, matplotlib
```

## How to run R0 (once the harness is wired to a headless binary)

```bash
python -m pip install -r tools/ga/requirements.txt
python tools/ga/clone.py --heuristic-traces traces/heur.jsonl --out evolved/clone.json
python tools/ga/evolve.py --pop 32 --gens 40 --seed 42
python tools/ga/eval.py evolved/best.json --held-out
python tools/ga/plot.py evolved/runs/<ts>/fitness.csv
```

`evolve --resume evolved/runs/<ts>` replays from the last generation's
checkpoint deterministically (same LCG chain as clanker/zdtd_bot).

## Disk contract

See `evolved/README.md` and `docs/research/04` §3 for the `evolved/runs/<ts>/`
and `best.json` shapes. The flat weight order is `W1 row-maj(16×14) | b1(16) |
W2 row-maj(5×16) | b2(5)` — shared with `BotNeuralBrain.cs`.
