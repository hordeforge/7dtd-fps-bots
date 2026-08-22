# `evolved/` — Trained-Bot Artifacts

This directory holds the *outcome* of `docs/research/00..06`. It is not hand-edited.

## Files

| Path | Meaning | Committed? |
|---|---|---|
| `best.json` | Champion weights (flat `float[W]` + meta) loaded by `BotNeuralBrain.TryLoad` | yes, when promoted |
| `best.meta.json` | `{ generation, fitness, configHash }` (as written by `ga.save_best`) | yes, alongside `best.json` |
| `runs/<ts>/` | One dir per training run: `config.json`, `gen_*.json`, `fitness.csv`, `leaderboards.jsonl` | no (artifact) |
| `archive/` | Old `best.json` moved here before promotion | no |

## Contract

- Weights order is canonical: `W1 row-maj(16×14) | b1(16) | W2 row-maj(5×16) | b2(5)` — see `docs/research/01` §4 and `Source/BotMod/AI/BotNeuralBrain.cs`.
- JSON version field must match the loader's `kVersion`; mismatch → fallback to heuristic.
- The mod never writes here at runtime; only `bot neural reload` re-reads `best.json` if `UseNeuralBrain=true`.

## How to promote a new champion

After a validated run (`docs/research/04` §8):

```bash
cp evolved/runs/<ts>/best.json evolved/best.json
cp evolved/runs/<ts>/best.meta.json evolved/best.meta.json
git add evolved/best.json evolved/best.meta.json
git commit -m "evolved: promote gen <N> fit <x>"
git push
```

Operators then `git pull` and `bot neural reload`.

## Git

- `best.json` ships so a fresh clone works without re-training.
- Everything else (`runs/`, `archive/`) is git-ignored to avoid repo bloat. A `.gitignore` here keeps the noise down while the directory stays tracked.
