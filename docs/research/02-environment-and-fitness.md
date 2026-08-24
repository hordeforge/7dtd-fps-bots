# Environment & Fitness for 7DTD Bot Evolution

## 1. Observation recap

Defined in `01-neuroevolution-architecture.md` §2: 14-16 normalized scalars from data the tick already has. No new engine sensors. The trainer sees the same vector as the live brain; there is no trainer-only cheat feature.

Additional raw that stays *fitness-only* (never fed to the net, only scored):

- `kills`, `deaths`, `damageDealt`, `damageReceived`, `shotsFired`, `hits`, `headshots`, `timeAlive`.
- `distanceTravelled`, `stuckTicks`, `campTime`, `retreatCount`.
- Per-match wall-clock (to bound runaway sims).

## 2. Arenas

The fitness landscape is meaningless without a fixed test suite. Three arena types, composed into a single scalar.

> Status (2026-08-21, R9/R11): the shipped mix is 9 fixed arena configs per
> seed-stream (`tools/ga/harness.py`) — duels vs static `(2,1,0)` ×3 + one
> evolved-vs-evolved duel `(2,2,0)`, FFA `(6,6,0)` ×3, horde `(4,4,6)` ×2 —
> every match weighted equally into the scalar mean, not the 40/40/20 split
> below. Opponents are the static no-brain policy (`_OPP_STATIC`, all-zero
> weights) with skill cycling 1..4 across matches, not named bot characters;
> duels pin a 50-unit spawn gap, open env and equal AKs (R11 rework).

### 2.1 1v1 duels (40% of fitness)

- Bot vs a *fixed opponent pool* (not vs itself): `VanillaBot(Diff2)`, `HardBot(Diff4)`, `CampBot(Stripe)`, `RushBot(Visor)`.
- Small flat map patch near a spawnpoint, 60 s or first death. Walls are a single line so LOS matters.
- Forces aim bias / fire gate / strafe to improve, not just wandering.

### 2.2 FFA DM (40%)

- 6 evolved bots + 2 fixed opponents, 90 s, `TargetBotCount=8` disabled (we control the roster exactly).
- Captures target selection (`FindTarget` preference), retreat, and not dying stupidly in crossfire.

### 2.3 Horde / mixed (20%)

- 4 bots vs 6 zombies (`BotVsZombie true`), 90 s.
- Prevents degenerate "only good at bot-vs-bot" genomes.

### 2.4 Map / weapon sampling

- Each evaluation runs F matches per genome, each match samples a `spawnpoints.xml` patch and a `LoadoutPool` entry. Average over samples so the net cannot overfit one gun (sniper-only cheese) or one map seam.
- Deterministic sampler: `LCG(generation, genomeIdx, matchIdx)` — same samples every generation.

## 3. Fitness function

We use a scalarized multi-objective so GA selection stays simple. Weights are configurable; defaults below are from paper-parity tuning (Q3 bot skill calibration) and kept explicit in `tools/ga/harness.py` (`FIT_ELO/FIT_ECON/FIT_SURV/FIT_STUCK/FIT_CAMP`); the R7 sweep (`tools/ga/fitness_sweep.py`) overrides them programmatically per mix, and the canon 0.55/0.25/0.15/0.05 was confirmed Pareto.

```
elo = kills - deaths * 1.0                // raw K/D
econ = damageDealt / max(1, max(1, damageReceived))
      - 0.05 * shotsFired/(hits + 1)       // aim efficiency, anti-spam
survival = mean(timeAlive / matchDuration)

fitness = 0.55 * norm(elo) + 0.25 * norm(econ) + 0.15 * survival
        - 0.05 * stuckFrac                 // penalty, not a head
        - 1.0 * campPen                    // camper with no kills (combat_sim camp_pen: 1.6 flat)

norm(x) is rank-normalized per generation (see §3.2).
```

Headshot rate is reported but not directly scored — it is a diagnostic for aim-bias drift. We score *damage dealt* instead so the GA cannot game headshot RNG on low-HP targets.

### 3.1 Why scalarized, not Pareto

Full NSGA-II is valid but heavier to implement and to explain to operators. Scalarized fitness plus a *diversity bonus* (§`03-genetic-algorithm.md`) is enough for phase 1. We still log the Pareto front (K/D, econ) for analysis, and can switch to NSGA-II without changing arenas.

### 3.2 Rank normalization

Raw kills vary wildly by map draw; raw normalizing is noisy. Instead:

1. Rank genomes 0..P-1 by raw fitness within the generation.
2. `norm = rank / (P-1)` → [0,1] uniform.
3. Selection sees `norm`, not raw.

This makes fitness scale-invariant and prevents a lucky map from exploding weights.

### 3.3 Credit assignment hygiene

- Each genome fights the *same* opponent pool and map samples (same seeds). Differences in fitness are genomic, not matchup luck.
- `F >= 3` matches per phase (so 9+ per genome total). More matches = less variance but linearly more sim cost; F is the main cost knob.
- The *champion* (prev best) replays all its matches each generation so we can compare generations apples-to-apples.

## 4. Determinism harness

The current `zdtd` passes already rely on this (stuck, LOS, wander hash). Evolution inherits it:

- Per-match seed: `hash(generation, genomeIdx, matchIdx, arenaKind)`.
- That seed drives: spawn assignment, weapon draw, initial facing, any `Rng01()` that the net might use (it shouldn't, but the fallback is already deterministic via the per-bot LCG `Bot._rng`).
- The forward pass itself is pure.

*Result:* rerunning the same generation on the same build replays byte-for-bit. Debugging a "why did genome 7 beat 12" is a matter of diffing traces.

## 5. Running the environment

> Status (2026-08-19, R1 pivot): training actually runs on the self-contained
> numba sim `tools/ga/combat_sim.py` driven by `harness.py` — no 7DTD binary.
> The zdtd-headless path below is the planned higher-fidelity step (see R8 §7
> residuals), not the current pipeline.

### 5.1 Headless sim (recommended)

- Use the `zdtd` headless path (`src/server/scenarios.zig` / `world_store` + `src/server/game/bot.zig`) without graphics, not the live 7DTD dedicated server, for speed (no physics sleep, no chunk streaming).
- The ZBS2 sense snapshot is the observation; `BotManager.move/look/shoot` is the action. The harness is the same code the live bots use, so evolved weights transfer with no sim-to-real gap beyond the physics we already skip.

### 5.2 Live dedi (slower, realistic)

- `BotNeuralBrain` can run inside the real mod (planned trace logging of
  `evolved/match_*.jsonl` is not implemented in the mod today), and the trainer
  can harvest those logs. Useful for final validation but an order of magnitude
  slower than headless.

## 6. Shaping and guardrails

- **Anti-camp exploitation:** `campTime > 0.6` fraction in DM penalizes the survival term (a camper who never fights scores low on elo even if alive). The fitness does not reward hiding.
- **Anti-suicide-rush:** damage efficiency term punishes dying fast even with a kill.
- **Health-gated retreat:** a genome that never retreats (< 0.35 hp) bleeds survival; one that always retreats bleeds elo. Both extremes are naturally selected against.

## 7. Data logged per evaluation

Per genome per match: `genomeId, matchSeed, kills, deaths, damageDealt/Received, shots, hits, headshots, timeAlive, distanceTravelled, stuckTicks, campFrac, weaponId, mapSample`. Per generation: best/mean/median fitness, diversity (mean pairwise Hamming/Euclid in weight space), and the 10 best genomes' weights. See `04-training-pipeline.md` for disk layout.

## 8. Ablation to run before freezing

1. Fitness with/without `stuckFrac` penalty (does it actually prevent wall-hugging?).
2. Behavior cloning warm-start vs random init (does cloning halve generations to parity?).
3. Single scalar vs Pareto selection (does scalar overtune to one weapon?).
