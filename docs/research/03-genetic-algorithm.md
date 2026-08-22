# Genetic Algorithm — Operators & Hyperparameters

## 1. Representation

- **Genome:** `float[W]` with `W ≈ 325` (fixed MLP) or variable with NEAT. Optionally a small `ΔCharacter` tail (6 floats clamped to `[-0.2, 0.2]` offsets over `BotCharacter.Camper/Aggression/...`) so evolution can nudge personality alongside weights without editing `characters.json` by hand.
- **Population:** `P = 32` (default). Larger helps diversity but linearly scales eval cost; 32 fits the dedi's cores.
- **Elitism:** `k = 2` best genomes survive verbatim.
- **Archive:** Hall of Fame of the last `H = 8` champions; every 12 generations a random Hall-of-Famer is injected back into the population (freshness-gated so an entry already in the live pop is not duplicated) to prevent forgetting.

## 2. Operators

### 2.1 Selection — tournament(k=3) + rank

Rank-normalize fitness (see `02`), then:

```
parent = argmax fitness among 3 uniformly sampled genomes (with replacement)
```

High selection pressure would be k=5; k=3 is calmer and preserves diversity alongside mutation. Top-ranked but unlucky genomes still reproduce.

### 2.2 Crossover — uniform blend

- With prob `pc = 0.6` a child is a crossover of 2 parents, else it is a mutated clone of one parent.
- Uniform: each weight from parent A with prob 0.5, else B.
- For NEAT variable topologies, align by innovation number (excess/disjoint handled with the compatibility distance, not mangled).

### 2.3 Mutation

Mutation is the main explorer (GA literature: crossover is overrated for small nets; mutation does most work).

| Mutator | Probability per child | Effect |
|---|---|---|
| Gaussian weight noise | 0.92 every child | Each weight `w += N(0, σ)` with σ cosine-annealed over the run and scaled by rank: `σ = 0.05 * anneal * burst * (0.6 + 0.9*(1 - rankNorm))` — fitter parents mutate less |
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

- **Hall-of-Fame opponents.** Re-encountering old champs prevents cycling.
- **Map/weapon rotation.** See `02` sampling — a genome that memorized one map dies on the next draw.
- **Weight-space distance diagnostic.** Track mean pairwise Euclidean distance of top quartile; if it drops below 0.08 for 5 consecutive generations, boost `σ` by 1.5× for one generation (noise burst).
- **Novelty bonus (optional).** `+0.01 * novelty(genome)` where novelty is k-NN distance in behavior space `(kills, timeAlive, damageEff)` archive of last 200 evaluations (Lehman & Stanley 2011). Disabled by default; enable if plateau persists.

## 4. Hyperparameters (single table to tune)

| Name | Symbol | Default | Range to sweep |
|---|---|---|---|
| Population | P | 32 | 16..64 |
| Elite | k | 2 | 1..4 |
| Tournament size | kt | 3 | 2..5 |
| Crossover prob | pc | 0.6 | 0.4..0.8 |
| Gaussian σ (base) | σ | 0.05 | 0.02..0.10 |
| Sparse reset prob | ps | 0.10 | 0.05..0.20 |
| Hall-of-Fame size | H | 8 | 4..16 |
| Matches per genome | F | 36 train = dual-seed x2 draws over the 9-arena config mix (R9 draw regularization); eval gate pins F=18 | cost tradeoff |
| Generations (run) | G | 40 (`evolve.py --gens`) | 40..400 in practice |
| NEAT trigger: plateau gens | Gplat | 15 | 10..30 |

Sweeping should touch at most 2 knobs per experiment; evolution is slow to evaluate so factorial sweeps are wasteful. Log every run; `evolved/runs/<ts>/config.json` freezes the table so results are reproducible.

## 5. Seeding and generation count

- **Generation 0:** behavior-cloned net + σ=0.02 jitter (see `01` §6). One exact clone is the initial champion.
- **Generations 1..G:** full loop. Checkpoint every generation (`best.json` overwritten + `gen_*.json` retained for the top 3).
- **Early stop:** if best fitness is flat (no improvement > 0.005 norm units) for `Gplat` generations, stop or enter NEAT phase. Never run unbounded.
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
pop = clone_heuristic(P, sigma=0.02)   # 1 exact, P-1 jittered
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
        mutate(child, sigma * (1 - 0.5*norm[parent_rank]))
        clamp(child, [-8,8])
        children.append(child)
    pop = elite + children
    hof = update_hof(hof, elite)
    if stagnant(fitness, Gplat): maybe_enter_neat()
best = pop[argmax(fitness)]
write("evolved/best.json", best)
```

Deterministic if the per-generation RNG is the same LCG (`2654435761`) seeded from `run_seed + g`.
