# Training Pipeline: From Weights to `best.json`

## 1. Bird's eye

The evolution loop is deliberately split into a **trainer** (owns GA, fitness, logging) and a **harness** (owns the sim tick). The trainer can be Python + harness bridge, or a Zig binary. As shipped (R1 pivot) the harness is the self-contained numba sim `tools/ga/combat_sim.py` driven by `harness.py`, no 7DTD binary; the `zdtd` headless bridge below is the planned fidelity upgrade, not the current pipeline. The mod itself never runs the GA; it only loads the outcome.

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
- Harness = world store, LOS, move caps, damage, replication: the ground truth.
- Mod = `BotNeuralBrain.cs` that does the 500-MAC forward pass and nothing else.

## 2. Trainer options

### 2.1 Python harness bridge (recommended first)

- Python calls the existing `zdtd` headless binary (or links `libzdtd` once it exists) via stdin/stdout or Python's `subprocess` + binary ZBS2 blobs on a pipe.
- Simpler to iterate on GA, plotting, and sweeps (NumPy + `matplotlib`).
- No native deps shipped into the mod. The mod still reads plain `best.json`.

### 2.2 Pure Zig trainer (phase 2, not taken)

- The GA itself is Zig (`tools/ga/`) so the entire pipeline is `zig build ga --best`. There is no Zig trainer in the tree; `tools/ga/` is Python (see §11).
- Useful for CI where Python is unavailable, and for embedding a background evolver thread in the dedicated server.

Either way the *protocol* between trainer and harness is the same: send `bot <verb>` commands, receive `sense` bytes. No shared struct.

## 3. Disk layout

Everything in `7dtd-fps-bots/evolved/` (per `evolved/.gitignore`: `runs/`, `archive/` and raw `*.bin`/`*.csv`/`*.jsonl` are git-ignored; the promoted champion pair and docs stay tracked):

```
evolved/
  best.json                 # flat float[] of the current champion (committed when promoted)
  best.meta.json            # { generation, fitness, configHash } (as written by ga.save_best)
  runs/<ts>/                # one dir per training run, never overwritten
    config.json             # full hyperparam table (03 §4) + run_seed
    gen_000.json            # state gen 000 ends in: top-3 + fitness, the full pool,
                            # the rng draw position and the measuring stick it was
                            # scored on, so --resume replays or refuses
                            # (`nextGen` is the generation the pool belongs to)
    gen_001.json ...
    fitness.csv             # per-gen best/mean/median/q25/q75/held-probe, append-only
    innovations.json        # NEAT only
    traces/<gen>_<idx>.bin  # optional: obs→action log for debugging
  archive/                  # old bests moved here before promoting a new one
```

`best.json` is small enough to commit (~8 KiB for the 325-float champion on disk). Keeping `runs/` git-ignored avoids repo bloat; CI uploads it as an artifact if needed.

## 4. Warm-start: behavioral cloning

Before the first GA generation, run a short trace dump:

1. Spawn heuristic bots (`BotCharacter` Stripe/Visor/Ranger) for ~5 minutes, deterministic seeds, mixed loadouts.
2. Log `(obs[14] → heuristicOutputs[5])` pairs at decision cadence (every `scanPeriod`).
3. Offline in the trainer, fit a 14→16→5 net to that trace via ~500 steps of Adam (learning rate 0.01): this is the only place gradient descent lives, and it is offline + optional.
4. `pop[0] = clonedWeights`, `pop[1..] = clonedWeights + N(0, 0.02)`.

If cloning regresses (net worse than heuristic), retry with fewer steps or skip cloning entirely. Log clone loss so we know warm-start helped.

## 5. Evaluation loop (determinism first)

For each genome `i` in generation `g`:

- Derive `seeds = LCG(runSeed, g, i, arena, match)`.
- For each of the 9 arena configs the shipped mix carries (see `02` §2):
  - `world_store` flat ground or sampled `spawnpoints.xml` patch (deterministic hash).
  - `BotManager` spawn with `spawnNamed` + `weapon_id` draw from the same LCG.
  - Drive harness ticks at 20 Hz for `matchDuration`, piping `sense → brain → bot <verb>` each tick.
  - Accumulate `kills/deaths/damage/timeAlive` from `BotManager.hp` and `sim` health.
- Aggregate to `fitness(i) = scalarized(F performances)` (see `02` §3).

**Cost knob:** `F` is linear in wall-clock, and it is the matches-per-genome
count, not the arena count: the shipped default is 9 configs × 2 seed streams
× 2 draws = `F=36` at 1200/1800 ticks per match. At 32 genomes that is ~1.4M
ticks/gen; on headless that's seconds, not minutes. The live dedi path is 20×
slower, hence we train headless.

**Parallelism:** genomes are independent. Shard across `N` workers (one sim per worker, or one process with `P` isolated `BotManager` instances). Seed independence keeps it deterministic regardless of shard order.

## 6. Curriculum

> Status (2026-08-21): the shipped curriculum is coarser than this 3-stage
> plan. `evolve.py --curriculum mixed|pvp_first|horde_first` gates the arena
> mix for roughly the first third of generations (`harness.CURRICULUM`,
> default `mixed`); there is no weapon-pinned stage A and no
> fitness-gated stage promotion. Kept as the design target.

Naive from-scratch DM is unstable early (random bots die instantly, no gradient signal). Use a 3-stage curriculum:

| Stage | Generations | Description |
|---|---|---|
| A: duels vs weak | 0..15 | 1v1 vs `Stripe` only (campy weak opponent), weapon fixed to pistol |
| B: mixed | 16..45 | Add `Visor` rusher + shotgun, then DM 4-bots |
| C: full | 45.. | FFA + Horde, mixed loadouts, FOV cone active |

Promotion to next stage is guard-railed: best fitness must have risen `> 0.08` norm units in the last 10 gens before curriculum advances. This is conservative on purpose, rushing curriculum just injects noise.

## 7. Checkpointing and resumability

- `fitness.csv` is appended every generation; `gen_*.json` checkpoints are
  written only when a generation improves on the best-so-far (`evolve.py`,
  see `03` §5). A checkpoint is written after reproduction, so it holds the
  pool and the rng draw position the next generation starts from: rerunning
  from the last checkpoint reproduces the interrupted run's `fitness.csv`
  row for row. Checkpoints predating that shape carry no `pop`/`rngState`,
  and `--resume` says so on stderr instead of pretending to replay.
- A checkpoint also carries the stick its numbers mean anything under:
  `activation`, `curriculum`, `seed` and `fitMix` (the scalarization).
  `--resume` compares them with the command line's and exits 2 on a
  mismatch. Carrying a tanh population into a relu run (or a run's own
  `best_fitness` into a run with another mix) makes every later generation
  compare scores from two different scales: `improved` never fires again, no
  further checkpoint is written, and the pre-resume `fitness.csv` rows are
  plotted against scores that were never on that axis. A checkpoint that
  carries none of the four (written before the fields existed) is not a
  mismatch and is carried with a warning. Islands are not compared: a resume
  flattens the loaded pool to one island by design.
- `tools/ga/determinism_check.py` runs the operators, the match kernel and the
  threaded harness twice from one seed and diffs the results; it is the
  executable form of this section.
- `best.meta.json` records `configHash = sha256(config.json)`. The loader does
  not enforce it (`configHash` never affects loadability, see `05` §4); it is
  informational, surfaced by `bot neural status`, so an operator can spot a
  champion trained under a different hyperparam table.
- A detached `evolve --resume runs/<ts>` flag replays the run without resetting generation 0.

## 8. Validation (not just "loss went down")

> Status (2026-08-21): shipped gates are held-seed based. `evolve.py`
> promotes only when the candidate's held40 probe beats the current
> champion's; the canonical promotion gate is
> `tools/ga/evolve.py static-vs-neural --seeds 999 1234 4242 --matches 40`
> (GOAL MET = champion beats static by >= +0.5 on every seed). The
> fresh-opponent-pool and human blind-test steps below remain design intent.

Each `best.json` is validated before it can be promoted:

1. Re-evaluate it 30 matches (more samples than training) vs a *fresh opponent pool* never seen during evolution (e.g., `Hunter`/`Wrack` + a new map patch). If mean fitness drops > one stdev, it is overfit, reject.
2. Human blind test: two DM replays (heuristic vs evolved) as video or trace CSV; operator picks which felt more human-like. Ship only when the evolved bot actually feels *better*, not just number-better.

## 9. Shipping to the mod

- Promote `runs/<ts>/best.json` → `evolved/best.json` + `best.meta.json`.
- Commit and push (`7dtd-fps-bots` repo). Operators `git pull` or download the asset.
- The mod's `ModApi` loads `evolved/best.json` on `OnGameStartDone` (or on config reload via `bot reload` / `bot neural reload`) through `BotNeuralBrain.TryLoad`. If the file is absent or malformed (`version`/`inputs`/weight-count mismatch), the mod falls back to the heuristic and logs `BotNeuralBrain: not loaded (<reason>), using heuristic`.

No Python ships, no extra DLL, no native module, just JSON.

## 10. Resource budget (honest numbers)

| Item | Approx |
|---|---|
| Gen 0..80, P=32, F=36 (9 arena configs x 2 seed streams x 2 draws), 1200/1800-tick matches, headless | ~40 min on a 8-core dev box (most of it is sim ticks, not GA math) |
| Same via live dedi | ~14 hours (physics + chunk IO), not used for training |
| Disk for `runs/<ts>` (no traces) | ~40 MiB |
| Disk with obs traces | ~400 MiB (optional; prune) |
| Mod runtime overhead | ~0.3 ms per tick for 16 bots (forward pass benchmarked at ~19 µs/bot, see `05` §6) |

> Status (2026-08-25): the wall-clock rows were measured before R9/R11
> widened the arena mix from the 3-arena F=9 setting; treat them as
> per-generation ballpark, not a re-benchmarked number.

## 11. Tools

> Status (2026-08-25): the shape below ships as Python CLIs under
> `tools/ga/`; the Zig variant was never needed. (2026-09-20, 0.7.0:
> `eval.py` and `eval_static_vs_neural.py` were folded into `evolve.py` as
> the `eval` and `static-vs-neural` subcommands, so all scoring paths share
> `harness.canonical_scores`; the standalone `plot.py` is gone, `report.py`
> and `dashboard.py` render the curves.)

| Tool | Shape |
|---|---|
| `tools/ga/evolve.py` | CLI that owns the loop; flags: `--pop 32 --gens 80 --seed 42 --resume` |
| `tools/ga/evolve.py eval <best.json>` | Re-evaluates a single `best.json` on the validation pool, prints report |
| `tools/ga/report.py` | Renders the `fitness.csv` best/mean curves into the HTML run report |
| `tools/ga/dashboard.py` | Cross-run dashboard, writes `docs/ga-dashboard.html` |
| `tools/ga/sweep.py` | Ablation sweep (currently the activation knob, §`06` §2) |
| `tools/ga/replay.py` | Deterministic arena recorder + top-down HTML replay (pre-R10 rules, visualization only) |
| `tools/ga/viz.py` | Renders the net topology/weight diagram to PNG |
| `tools/ga/determinism_check.py` | Drives operators, match kernel and harness twice from one seed and diffs (§7) |

All live under `7dtd-fps-bots/tools/ga/` so they ship with the mod's research and do not pollute the clean-room `zdtd` tree.
