# Training Pipeline — From Weights to `best.json`

## 1. Bird's eye

The evolution loop is deliberately split into a **trainer** (owns GA, fitness, logging) and a **harness** (owns the sim tick). The trainer can be Python + harness bridge, or a Zig binary. As shipped (R1 pivot) the harness is the self-contained numba sim `tools/ga/combat_sim.py` driven by `harness.py` — no 7DTD binary; the `zdtd` headless bridge below is the planned fidelity upgrade, not the current pipeline. The mod itself never runs the GA; it only loads the outcome.

```
┌──────────────────┐      queued text SimCommands      ┌──────────────────┐
│  trainer          │  ──► bot spawn/move/look/shoot ──►  harness         │
│  (Python or Zig)  │  ◄── sense bytes (ZBS2) ─────────  (zdtd headless)  │
│                   │      + match logs                    │  bot.zig      │
└──────────────────┘                                     └──────────────────┘
         ▲   best.json                                          │
         └── commits to repo ◄──────────────────────────────────┘
```

- Trainer = GA operators (§`03`), fitness (§`02`), checkpointing, curriculum.
- Harness = world store, LOS, move caps, damage, replication — the ground truth.
- Mod = `BotNeuralBrain.cs` that does the 500-MAC forward pass and nothing else.

## 2. Trainer options

### 2.1 Python harness bridge (recommended first)

- Python calls the existing `zdtd` headless binary (or links `libzdtd` once it exists) via stdin/stdout or Python's `subprocess` + binary ZBS2 blobs on a pipe.
- Simpler to iterate on GA, plotting, and sweeps (NumPy + `matplotlib`).
- No native deps shipped into the mod. The mod still reads plain `best.json`.

### 2.2 Pure Zig trainer (phase 2)

- The GA itself is Zig (`tools/ga/`) so the entire pipeline is `zig build ga --best`.
- Useful for CI where Python is unavailable, and for embedding a background evolver thread in the dedicated server.

Either way the *protocol* between trainer and harness is the same: send `bot <verb>` commands, receive `sense` bytes. No shared struct.

## 3. Disk layout

Everything in `7dtd-fps-bots/evolved/` (git-ignored except `best.json` when promoted):

```
evolved/
  best.json                 # flat float[] of the current champion (committed when promoted)
  best.meta.json            # { generation, fitness, configHash } (as written by ga.save_best)
  runs/<ts>/                # one dir per training run, never overwritten
    config.json             # full hyperparam table (03 §4) + run_seed
    gen_000.json            # top-3 genomes of that gen (weights + fitness)
    gen_001.json ...
    fitness.csv             # per-gen best/mean/median/diversity, append-only
    innovations.json        # NEAT only
    traces/<gen>_<idx>.bin  # optional: obs→action log for debugging
  archive/                  # old bests moved here before promoting a new one
```

`best.json` is small enough to commit (1-5 KiB). Keeping `runs/` git-ignored avoids repo bloat; CI uploads it as an artifact if needed.

## 4. Warm-start: behavioral cloning

Before the first GA generation, run a short trace dump:

1. Spawn heuristic bots (`BotCharacter` Stripe/Visor/Ranger) for ~5 minutes, deterministic seeds, mixed loadouts.
2. Log `(obs[14] → heuristicOutputs[5])` pairs at decision cadence (every `scanPeriod`).
3. Offline in the trainer, fit a 14→16→5 net to that trace via ~500 steps of Adam (learning rate 0.01) — this is the only place gradient descent lives, and it is offline + optional.
4. `pop[0] = clonedWeights`, `pop[1..] = clonedWeights + N(0, 0.02)`.

If cloning regresses (net worse than heuristic), retry with fewer steps or skip cloning entirely. Log clone loss so we know warm-start helped.

## 5. Evaluation loop (determinism first)

For each genome `i` in generation `g`:

- Derive `seeds = LCG(runSeed, g, i, arena, match)`.
- For each of `F × 3` arenas (see `02`):
  - `world_store` flat ground or sampled `spawnpoints.xml` patch (deterministic hash).
  - `BotManager` spawn with `spawnNamed` + `weapon_id` draw from the same LCG.
  - Drive harness ticks at 20 Hz for `matchDuration`, piping `sense → brain → bot <verb>` each tick.
  - Accumulate `kills/deaths/damage/timeAlive` from `BotManager.hp` and `sim` health.
- Aggregate to `fitness(i) = scalarized(F performances)` (see `02` §3).

**Cost knob:** `F` is linear in wall-clock. `F=9` at ~90 ticks/match → ~810 ticks per genome. At 32 genomes that's ~26k ticks/gen; on headless that's seconds, not minutes. The live dedi path is 20× slower — hence we train headless.

**Parallelism:** genomes are independent. Shard across `N` workers (one sim per worker, or one process with `P` isolated `BotManager` instances). Seed independence keeps it deterministic regardless of shard order.

## 6. Curriculum

Naive from-scratch DM is unstable early (random bots die instantly, no gradient signal). Use a 3-stage curriculum:

| Stage | Generations | Description |
|---|---|---|
| A: duels vs weak | 0..15 | 1v1 vs `Stripe` only (campy weak opponent), weapon fixed to pistol |
| B: mixed | 16..45 | Add `Visor` rusher + shotgun, then DM 4-bots |
| C: full | 45.. | FFA + Horde, mixed loadouts, FOV cone active |

Promotion to next stage is guard-railed: best fitness must have risen `> 0.08` norm units in the last 10 gens before curriculum advances. This is conservative on purpose — rushing curriculum just injects noise.

## 7. Checkpointing and resumability

- Every generation writes `gen_*.json` + appends `fitness.csv`. On crash, rerun from the last generation's checkpoint (pop is deterministic from checkpoint + seeds).
- `best.meta.json` records `configHash = sha256(config.json)` so a stale `best.json` is never loaded if config drifted.
- A detached `evolve --resume runs/<ts>` flag replays the run without resetting generation 0.

## 8. Validation (not just "loss went down")

Each `best.json` is validated before it can be promoted:

1. Re-evaluate it 30 matches (more samples than training) vs a *fresh opponent pool* never seen during evolution (e.g., `Hunter`/`Wrack` + a new map patch). If mean fitness drops > one stdev, it is overfit — reject.
2. Human blind test: two DM replays (heuristic vs evolved) as video or trace CSV; operator picks which felt more human-like. Ship only when the evolved bot actually feels *better*, not just number-better.

## 9. Shipping to the mod

- Promote `runs/<ts>/best.json` → `evolved/best.json` + `best.meta.json`.
- Commit and push (`7dtd-fps-bots` repo). Operators `git pull` or download the asset.
- The mod's `ModApi` loads `evolved/best.json` on `OnGameStartDone` (or on config reload via `bot reload` / `bot neural reload`) through `BotNeuralBrain.TryLoad`. If the file is absent or malformed (`version`/`inputs`/weight-count mismatch), the mod falls back to the heuristic and logs `BotNeuralBrain: not loaded (<reason>), using heuristic`.

No Python ships, no extra DLL, no native module — just JSON.

## 10. Resource budget (honest numbers)

| Item | Approx |
|---|---|
| Gen 0..80, P=32, F=9, 90s matches, headless | ~40 min on a 8-core dev box (most of it is sim ticks, not GA math) |
| Same via live dedi | ~14 hours (physics + chunk IO) — not used for training |
| Disk for `runs/<ts>` (no traces) | ~40 MiB |
| Disk with obs traces | ~400 MiB (optional; prune) |
| Mod runtime overhead | ~0 (forward pass is already benchmarked < 1 µs/bot) |

## 11. Tools to build

| Tool | Shape |
|---|---|
| `tools/ga/evolve` (Zig or Python) | CLI that owns the loop; flags: `--pop 32 --gens 80 --seed 42 --resume` |
| `tools/ga/eval` | Re-evaluates a single `best.json` on the validation pool, prints report |
| `tools/ga/plot` | Plots `fitness.csv` + Pareto fronts (Python) |

All live under `7dtd-fps-bots/tools/` so they ship with the mod's research and do not pollute the clean-room `zdtd` tree.
