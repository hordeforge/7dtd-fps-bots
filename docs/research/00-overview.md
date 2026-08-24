# Self-Improving 7DTD Bots via Neuroevolution — Overview

*Status: research brainstorm, 2026-08-18 — lives in `7dtd-fps-bots/docs/research/` so it travels with the bot mod, not the clean-room server.*

## 1. Goal

Replace the hand-tuned heuristic loop (`Bot.Tick` → `BotBrain.FindTarget` / `LeadAimPoint` / `TryShootBurst` + `WantsToCamp/Retreat`) with a **self-improving controller** that gets better without a human editing numbers. The controller is a small neural net; a genetic algorithm (GA) breeds the weights across generations. Each generation is scored in headless fights; the best genomes survive and mutate.

> Q3/Doom3 heuristic bots are the *bootstrap*, not the ceiling. We already have deterministic, weapon-aware, LOS-gated bots (clanker `WeaponProfile`, `BotCharacter` 13 traits, per-bot LCG). Neuroevolution starts from that genome and then drifts beyond it.

## 2. Why neuroevolution and not gradient RL

| Constraint | Why GA wins here |
|---|---|
| **No differentiable simulator.** The "environment step" is `7DaysToDieServer` tick + physics + raycasts + `DamageEntity`. No gradient flows through it. | GA only needs a fitness scalar, not a gradient. |
| **Sparse, delayed reward.** Kills happen seconds after decisions; credit assignment is messy. | Tournament fitness (K/D, survival time) is a clean scalar even when per-tick rewards are sparse. |
| **Small net, cheap eval.** Target is ~200-600 weights, not a deep conv net. | Gaussian mutation + uniform crossover on flat vectors is trivial on a dedi box, no PyTorch needed at runtime. |
| **Determinism.** Clanker already proved per-bot LCG (`Config.Lcg`, multiplier 1103515245) makes fights replayable. | Determinism makes fitness stable generation-over-generation; same seed → same outcome. |
| **No client mod, EAC-off dedi.** We cannot ship a Python training loop into the mod. | Trainer can run *out-of-process* (Python or Zig harness) and export only `float[] weights` into the mod. |

Gradient RL (PPO/SAC) is still viable *offline* with a surrogate simulator, but for a first self-improving loop GA is simpler, debuggable, and fits the C# mod's constraints (no native libs, `netstandard` only). NEAT (evolving topology) and OpenAI-ES / CMA-ES are natural upgrades — see `01-neuroevolution-architecture.md`.

## 3. The self-improving loop

```
          ┌─────────────────────────────────────────────────┐
          │              generation g (population P)         │
          │  genome = { weights W, trait tweaks ΔCharacter}  │
          └───────────┬─────────────────────────────────────┘
                      │ spawn P bots into N arenas
                      ▼
          ┌─────────────────────────────────────────────────┐
          │  headless evaluation (deterministic, parallel)  │
          │  each genome fights F matches (DM or 1v1,       │
          │  sampled maps / weapons / difficulties)          │
          │  → fitness scalar f(genome)                     │
          └───────────┬─────────────────────────────────────┘
                      │ selection + crossover + mutation
                      ▼
          ┌─────────────────────────────────────────────────┐
          │           generation g+1                        │
          │  elite k survive, rest are children             │
          │  log: best f, mean f, diversity, Pareto front   │
          └─────────────────────────────────────────────────┘
                      │ best genome → BotNeuralBrain.cs
                      └─► live server (can keep evolving in background thread
                          or freeze to best.json and ship)
```

**Modes:**

- **Offline evolution** (recommended first): run the trainer on a dev box / CI, produce `evolved/best.json`, commit it, mod loads it on `GameStartDone`. Repeat nightly. Fully reproducible, no live-server risk.
- **Online background evolution** (phase 2): the mod spawns a low-priority thread that continuously evaluates mutated copies against the current champ in spare ticks, promoting winners via `ModApi.Log` + `bot reload`. Guarded by a feature flag so it never blocks the main tick.

## 4. What "self-improving" means here

Not magic. The bot improves *within the physics envelope*:

- Same move caps / LOS / weapon range as a player (we already enforce that).
- Only the *decision* changes: when to chase vs camp, how much to lead, how long to hold fire, strafe phase, FOV usage, retreat threshold.

Improvement is measured as Pareto fitness (§`02-environment-and-fitness.md`), not vibes: kills, deaths, damage dealt/received, time-to-kill vs unseen opponent pools. A genome that only farms easy targets is culled.

## 5. How this doc set is organized

- `01-neuroevolution-architecture.md` — network topology, genome encoding, NEAT vs fixed, why 14→16→5 fits on dedi.
- `02-environment-and-fitness.md` — observation vector, action heads, arenas, reward shaping, determinism harness.
- `03-genetic-algorithm.md` — selection, crossover, mutation, speciation, hyperparams, diversity tricks.
- `04-training-pipeline.md` — offline trainer design (Python + headless sim bridge), checkpointing, curriculum, data layout.
- `05-integration.md` — how `BotNeuralBrain.cs` plugs into `Bot.Tick` without breaking `BotBrain` fallback, serialization, dedi safety.
- `06-experiments-and-roadmap.md` — phased experiments, ablations, metrics, risks, open questions.

## 6. Non-goals (for this research phase)

- No end-to-end vision CNN. Inputs are already the `sense`-like feature vector the heuristic uses (distances, health fractions, LOS, weapon). Raw voxel sight is a later step.
- No real-time backprop on the dedi. Mutation-only in production; gradient steps happen offline if ever.
- No player impersonation. Bots stay `[Bot]`-prefixed, `BotVsBot/Player/Zombie` gates remain.

## 7. How to read this

Start with `01`, then `02`. `03` and `04` are the build manual. `05` is the integration contract so the research never drifts from what the mod can actually ship.
