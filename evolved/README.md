# `evolved/`: Trained-Bot Artifacts

This directory holds the *outcome* of `docs/research/00..06`. It is not hand-edited.

## Files

| Path | Meaning | Committed? |
|---|---|---|
| `best.json` | Champion weights (flat `float[W]` + meta) loaded by `BotNeuralBrain.TryLoad` | yes, when promoted |
| `best.meta.json` | `{ generation, fitness, activation, configHash }` (as written by `ga.save_best`) | yes, alongside `best.json` |
| `report.html` | `report.py` output for the current run set (multi-MB embedded base64) | no (artifact; regenerate from `runs/`) |
| `sweeps/` | Figures and JSON a `docs/research/REPORT-*.md` cites as evidence | yes (PNG/JSON only; `report_*.html` is ignored) |
| `runs/<ts>/` | One dir per training run: `config.json`, `gen_*.json`, `fitness.csv` | no (artifact) |
| `archive/` | Manual holding pen for a superseded `best.json` (nothing writes here automatically; `evolve.py` overwrites `best.json` in place once its held probe beats the incumbent) | no |

## Contract

- Weights order is canonical: `W1 row-maj(16×14) | b1(16) | W2 row-maj(5×16) | b2(5)`, see `docs/research/01` §4 and `Source/BotMod/AI/BotNeuralBrain.cs`.
- JSON version field must match the loader's `kVersion`; mismatch → fallback to heuristic.
- `activation` records which hidden pass trained the weights. The mod's forward
  pass is tanh-only, so `BotNeuralBrain.TryLoad` rejects any other value rather
  than scoring relu-trained weights as tanh. An artifact written before this
  field existed carries no `activation` and is read as tanh.
- The mod never writes here at runtime. It reads `best.json` when
  `UseNeuralBrain=true`: at world start (`ModApi.OnGameStartDone`) and on
  demand via `bot neural on|reload [path]` or the web API's `neural` action.

## How to promote a new champion

Promotion is automatic, not manual: a non-dry `evolve.py` run rewrites
`evolved/best.json` in place when its held-out probe (seed 999, 40 matches)
scores at least as high as the incumbent recomputed from the committed weights.
A `--dry-run` never promotes, and a failed probe scores `-inf` and loses to any
incumbent. A `--activation relu` run never promotes: it writes
`runs/<ts>/best_relu.json` instead, which the mod cannot load.

So the manual step is review and commit whatever the run wrote:

```bash
git add evolved/best.json evolved/best.meta.json
git commit -m "evolved: promote gen <N> fit <x>"
git push
```

Before a multi-day run, `make backup` (see `docs/recovery.md`): it snapshots
the champion `evolve.py` is overwriting in place, so a run that dies before
promotion still leaves a restorable copy. `evolve.py` itself only writes
`evolved/best.json`; `runs/<ts>/` stays unbacked.

Operators then `git pull` and `bot neural reload`.

## Git

- `best.json` ships so a fresh clone works without re-training.
- Everything else is git-ignored to avoid repo bloat: `runs/`, `archive/`, and the
  generated `report.html` / `sweeps/report_*.html`. A `.gitignore` here keeps the
  noise down while the directory stays tracked.
