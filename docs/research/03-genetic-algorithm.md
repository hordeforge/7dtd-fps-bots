# Genetic Algorithm: Operators & Hyperparameters

## 1. Representation

- **Genome:** `float[W]` with `W ≈ 325` (fixed MLP) or variable with NEAT. Optionally a small `ΔCharacter` tail (6 floats clamped to `[-0.2, 0.2]` offsets over `BotCharacter.Camper/Aggression/...`) so evolution can nudge personality alongside weights without editing `characters.json` by hand.
- **Population:** `P = 32` (default). Larger helps diversity but linearly scales eval cost; 32 fits the dedi's cores.
- **Elitism:** `k = 2` best genomes survive verbatim.
- **Archive:** Hall of Fame of the last `H = 8` champions; every 12 generations a random Hall-of-Famer is injected back into the population (freshness-gated so an entry already in the live pop is not duplicated) to prevent forgetting.

## 2. Operators

### 2.1 Selection: tournament(k=3) + rank

Rank-normalize fitness (see `02`), then:

```
parent = argmax fitness among 3 uniformly sampled genomes (with replacement)
```

High selection pressure would be k=5; k=3 is calmer and preserves diversity alongside mutation. Top-ranked but unlucky genomes still reproduce.

### 2.2 Crossover: uniform blend

- With prob `pc = 0.6` a child is a crossover of 2 parents, else it is a mutated clone of one parent.
- Uniform: each weight from parent A with prob 0.5, else B.
- For NEAT variable topologies, align by innovation number (excess/disjoint handled with the compatibility distance, not mangled).

### 2.3 Mutation

Mutation is the main explorer (GA literature: crossover is overrated for small nets; mutation does most work).

| Mutator | Probability per child | Effect |
|---|---|---|
| Gaussian weight noise | 0.92 every child | Each weight `w += N(0, σ)` with σ cosine-annealed over the run and scaled by rank: `σ = 0.05 * anneal * burst * (0.6 + 0.9*(1 - rankNorm))`, fitter parents mutate less |
| Sparse reset | 0.14 (0.22 stagnant) | Pick 1–3 weights, resample `U(-0.7, 0.7)` |
| Swap | 0.06 (0.12 stagnant) | Swap two hidden-unit weight blocks (positional robustness) |
| ΔCharacter nudge | not implemented | The genome carries no trait tail (`W = 325` weights only); trait nudges would need the §1 ΔCharacter encoding first |

No gradient; no momentum. When a plateau is detected the explorer burst fires
(σ × 1.8, higher reset/swap probs). Shipped constants live in
`tools/ga/ga.py::mutate`; they were tuned across R5-R11 (see the reports), so
this table defers to that file where the two ever disagree.

### 2.4 Speciation (phase 2, NEAT)

Compatibility distance `δ = c1·E/N + c2·D/N + c3·W̄` with `c = (1.0, 1.0, 0.4)`, threshold `δt = 3.0`. Fitness shared within species so new topologies are not killed in infancy. Species enter/exit the mating pool only within themselves; inter-species crossover at 5% keeps exploration alive.

## 3. Diversity hygiene

Evolution collapses fast on small arenas. Countermeasures (cheap, deterministic):

- **Hall-of-Fame injection.** Old champs re-enter the population, not the
  opponent pool: every 12 generations one random Hall-of-Famer is copied into
  a random slot (`evolve.py`, freshness-gated so a genome identical to the
  current best is not re-injected). This is the §1 archive, not an opponent
  re-encounter.
- **Map/weapon rotation.** See `02` sampling, a genome that memorized one map dies on the next draw. Shipped only in the mixed arenas: the duels pin env and weapon, so their memory pressure comes from skill cycling (`02` §2 status note).
- **Weight-space distance diagnostic.** Track mean pairwise Euclidean distance of top quartile; if it drops below 0.08 for 5 consecutive generations, boost `σ` by 1.5× for one generation (noise burst). **Not implemented**: no diversity metric is computed or logged (`02` §7 status, `06` §3), so the only σ boost that fires is the stagnation burst in §2.3. Kept as design intent.
- **Novelty bonus (optional).** `+0.01 * novelty(genome)` where novelty is k-NN distance in behavior space `(kills, timeAlive, damageEff)` archive of last 200 evaluations (Lehman & Stanley 2011). Disabled by default; enable if plateau persists.

## 4. Hyperparameters (single table to tune)

| Name | Symbol | Default | Range to sweep |
|---|---|---|---|
| Population | P | 32 | 16..64 |
| Elite | k | 2 | 1..4 |
| Tournament size | kt | 3 | 2..5 |
| Crossover prob | pc | 0.6 | 0.4..0.8 |
| Gaussian σ (base) | σ | 0.05 | 0.02..0.10 |
| Sparse reset prob | ps | 0.14 (0.22 stagnant, per `ga.py::mutate`) | 0.05..0.22 |
| Hall-of-Fame size | H | 8 | 4..16 |
| Islands | I | 1 (`evolve.py --islands`, range 1..8) | 1..8; split `max(8, pop // I)` per island, ring-migrate 2 migrants every 10 gens when `I > 1` (`ga.island_mix`) |
| Matches per genome | F | 36 train = dual-seed x2 draws over the 9-arena config mix (R9 draw regularization); eval gate pins F=18 | cost tradeoff |
| Generations (run) | G | 40 (`evolve.py --gens`) | 40..400 in practice |
| Stagnation burst trigger | plateau | 8 gens with no best improvement > 1e-6 (`evolve.py`: `stagnant = plateau >= 8`); fires the explorer burst, not NEAT | 5..15 |
| NEAT trigger: plateau gens | Gplat | not implemented (NEAT is §2.4 phase 2) | n/a |

Sweeping should touch at most 2 knobs per experiment; evolution is slow to evaluate so factorial sweeps are wasteful. Log every run; `evolved/runs/<ts>/config.json` freezes the table so results are reproducible.

## 5. Seeding and generation count

- **Generation 0:** He init + σ=0.02 jitter (`ga.init_population`, the stub
  formerly named `clone_heuristic`; the behavior-cloned warm-start of `01` §6
  is not implemented yet). One exact clone is the initial champion.
- **Generations 1..G:** full loop. Checkpoints: `gen_*.json` (top 3) is written
  when the generation improved on the best-so-far, not unconditionally;
  `fitness.csv` is appended every generation.
- **Early stop:** not implemented. `evolve.py` always runs to `--gens`; a flat
  best (`plateau >= 8`, improvement ≤ 1e-6) only fires the explorer burst
  (§2.3 stagnation arm). NEAT entry (§2.4) remains future work.
- **Reruns:** a run can be replayed from `gen_*.json` + the fitness seeds; determinism means rerunning the same config replays the same learning curve.

## 6. Flat-float vs framework

No DEAP/PyGAD import required on the mod side. The trainer (Python) owns these operators with ~200 lines of NumPy/standard-library code. The *mod* only ever sees `best.json` (a flat array). Keeping the GA off-framework avoids locking versions and avoids shipping a genetic library into the dedi.

## 7. Failure modes and mitigations

| Symptom | Likely cause | Fix |
|---|---|---|
| Fitness oscillates, no climb | Noisy arenas (F too small) | Raise F, rank-normalize |
| All genomes clone one cheesy strategy (camp forever) | Fitness rewards survival too much | Penalize `campTime`, raise DM weight |
| Weights explode (NaN forward) | σ too large, no clamp | Clamp weights to [-8, 8]; if NaN, discard child |
| NEAT grows huge nets for no gain | Add-connection prob too high | Halve add-connection, raise cap |
| Learns only vs weak opponents | Fixed pool too easy | Rotate in previous gen's champion as opponent |

## 8. What we are explicitly not using yet

- **CMA-ES / OpenAI-ES.** Excellent but require tuning `σ` schedules and population-normalized gradients. Swap in later if GA stalls; interface is the same (population → fitness → new population).
- **Quality-Diversity (MAP-Elites).** Overkill for first phase; novelty bonus is the light version.
- **Gradient-based fine-tuning.** Offline SGD on the cloned net is fine for warm-start, but not wired into the main loop yet.

## 9. Pseudocode (trainer side, Python-shaped)

```
pop = init_population(P, sigma=0.02)   # 1 exact, P-1 jittered
hof = [pop[0]]
for g in range(G):
    # evaluate
    fitness = [ eval(genome, seeds(g,i)) for i in range(P) ]
    rank = argsort(fitness)       # ascending
    norm = rank / (P-1)

    log(g, fitness, pop)         # CSV + gen_i.json
    # selection loop for next gen
    elite = [ pop[i] for i in topk(fitness, k) ]
    children = []
    while len(children) < P - k:
        p = tournament(pop, norm, k=kt)   # one or two parents
        child = crossover(p) if rand()<pc else copy(p)
        mutate(child, sigma, rank_norm=norm[parent_rank], generation=g,
               total_gens=G, stagnant=stagnant)   # anneal + burst + clip live in mutate
        # mutate() clamps to [-8,8] on return (ga.py::mutate)
        children.append(child)
    pop = elite + children
    hof = update_hof(hof, elite)
    if stagnant(fitness, Gplat): maybe_enter_neat()
best = pop[argmax(fitness)]
write("evolved/best.json", best)
```

Deterministic: one `numpy.random.Generator` per run (`np.random.default_rng(seed)`) drives selection, crossover, mutation, migration and HOF injection, so a run replays exactly from its seed.
