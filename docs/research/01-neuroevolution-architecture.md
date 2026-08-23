# Neuroevolution Architecture for 7DTD Bots

## 1. Where the net sits

Today `Bot.Tick(dt, world)` does:

```
sense (FindTarget / CanSee / BotCharacter) → decisions (DecideGoal / WantsToCamp)
→ move intent (MoveTo / Strafe / Backpedal) → aim (LeadAimPoint + _aimBiasYaw)
→ shoot gate (TryShootBurst)
```

The neural controller replaces the **decision + aim-bias + throttle** slice, not the whole tick. Physics, LOS, and damage stay host-authoritative. Two integration shapes exist; we pick #1 first.

| Shape | Net replaces | Fallback if net fails |
|---|---|---|
| **A — policy head** (chosen) | `WantsToCamp/Retreat/DecideGoal`, `_aimBiasYaw`, `TryShootBurst` gate, `_strafeDir` flip | Heuristic runs if `BotNeuralBrain` throws or weights missing |
| **B — full motor** | Also `MoveTo` vector | Riskier; needs safety clamp |

## 2. Observation vector (14 inputs, frozen)

Built from data `Bot.Tick` already computes; no new sensors needed for v1.

> Shipped shape is frozen at **14 inputs** (`W = 325`; `best.json` carries
> `inputs: 14`). Rows 14-15 below were never wired and stay reserved. Since R8,
> the trainer fills slot 4 with fire-spread fraction and slot 12 with
> rounds-left fraction (they were dead placeholders,
> `tools/ga/combat_sim.py`); the live mod keeps the original lose-timer /
> enemy-velocity semantics for those slots (`Bot.BuildNeuralInputs`).

| # | Feature | Source | Norm |
|---|---|---|---|
| 0 | `hpFrac = Health / BotHealth` | `Bot.Tick` | [0,1] |
| 1 | `enemyHpFrac` (target or 1 if none) | `FindTarget` | [0,1] |
| 2 | `dist / VisionRange` | `Vector3.Distance` | [0,1] clipped |
| 3 | `canSee` | `BotBrain.CanSee` | {0,1} |
| 4 | `loseTimer / LoseTargetTimeSec` | `_loseTargetTimer` | [0,1] |
| 5 | `weaponRange / AttackRange` | `WeaponProfile.Range` | [0,1] |
| 6 | `weaponPellets / 8` | `WeaponProfile.Pellets` | [0,1] |
| 7 | `aimAcc` | `Character.AimAccuracy` | [0,1] |
| 8 | `aimSkill` | `Character.AimSkill` | [0,1] |
| 9 | `aggression`, `selfPreservation`, `camper` | `BotCharacter` | [0,1] each but packed → 3 dims here: indices 9,10,11 |
| 12 | `enemyVelMag / 12` | `_targetVel.magnitude` | [0,1] |
| 13 | `stuckFrac = min(stuckSince / StuckTimeoutSec, 1)` | `_stuckSince` | [0,1] |
| 14 | `burstLeft / BurstMax` | `_burstLeft` | reserved, not shipped |
| 15 | `inAttackRange` | `dist <= Weapon.Range` | reserved, not shipped |

*Why 14-16 and not 100.* A dedi tick runs every 50 ms; even 16 bots × a few hundred FLOPs is noise. BLOPS budget: `16 × (14*16 + 16*5) ≈ 4k MACs/tick`.

## 3. Action heads (5 out)

> Status (2026-08-21, R10/R12): outputs stay advisory but their live use moved.
> `retreatLogit`, `fireGate` and `aimBiasYaw` work as tabled below
> (`Bot.cs` retreat gate / `TryShootBurst` / aim window). `campLogit` no longer
> replaces the idle `WantsToCamp` roll (idle camping stays heuristic); it damps
> forward movement to 15% when healthy and far in `AttackInRange`. `strafeDir`
> survives as the sign head, but since R10 the continuous strafe/retreat logits
> also compose the bot's 2D velocity (`BotBrain.MoveDir`, Q3 fallback kept) —
> see REPORT-2026-08-21-R10 and `05-integration.md` §2.

| # | Output | Interpretation | Host clamp |
|---|---|---|---|
| 0 | `campLogit` | Sigmoid → `wantCamp` (replaces `WantsToCamp` roll) | Threshold 0.5; still needs `DecideGoal==Camp` |
| 1 | `retreatLogit` | Sigmoid → `wantRetreat` (replaces hp check) | Distance gate `dist<22 \|\| !hasBetterWeapon` stays |
| 2 | `aimBiasYaw` | Tanh → ±0.45*(1-acc) rad (replaces `_aimBiasYaw`) | Clamped to same interval as heuristic |
| 3 | `fireGate` | Sigmoid → shoot? | Still gated by `ReactionTime`, `BurstPause`, LOS, range |
| 4 | `strafeDir` | Sigmoid → left/right (replaces `Rng01()<0.5`) | Host picks direction, not speed |

All outputs are *advisory*; the authoritative gates in `TryShootBurst` and `CanSee` stay.

## 4. Genome encoding

### 4.1 Fixed-topology MLP (phase 1)

```
14 → Dense 16 tanh → Dense 5 (mixed: sigmoid/tanh)
Weights: (14*16 + 16) + (16*5 + 5) = 325 floats ≈ 1.3 KiB
Genome: float[W] + optional small ΔCharacter tweak (Camper/Aggression/etc)
```

- Flat `float[]` stored as JSON `number[]` or binary blob.
- Initialization: He-style `U(-√6/fan_in, √6/fan_in)` then scaled so initial policy roughly matches heuristic (behavioral cloning warm-start — see §6).
- Encoding is framework-free: no ONNX, no `System.Numerics.Tensors` needed on the mod. Forward pass is handwritten loops (see `05-integration.md`).

### 4.2 NEAT / evolving topology (phase 2)

When `W` stops improving for `G_plat` generations:

- Add-node / add-connection mutations (Stanley & Miikkulainen 2002), still histogram-capped at ~600 weights so dedi inference stays cheap.
- Innovation numbers tracked in `evolved/innovations.json` (global, not per genome).
- Speciation by compatibility distance (δ = c1·E/N + c2·D/N + c3·W̄) to protect new topologies.

### 4.3 ES / CMA-ES (optional phase 3)

If we want *gradient-free but smoother* than GA, OpenAI-ES (Salimans et al. 2017) or CMA-ES on the same flat vector is a drop-in replacement for selection/crossover — no new encoding.

## 5. Determinism contract

- Net forward pass is pure math (no RNG). RNG only touches **genetic operators** and falls back to the existing per-bot LCG (`Bot.RngNext`) if we ever randomize inside the brain.
- Evaluation harness seeds every match from `generation × genomeIdx × matchIdx` via the same LCG tap (`2654435761 / 1103515245`). Same genome → same record → same fitness. This is what makes evolution stable and debuggable.

## 6. Warm-start (behavioral cloning from heuristic)

> Status (2026-08-24): not implemented as described. `ga.clone_heuristic`
> is a stub: generation 0 = He init + σ=0.02 jitter (the R1-R12 runs all
> started from random weights and still beat the heuristic). Trace-based
> fitting below remains the design target.

Random weights in generation 0 waste evaluations. Instead:

1. Log `(observation → heuristic decision)` traces from current bots (a few minutes, deterministic replay).
2. Solve a tiny regression so the net's initial outputs mimic heuristic on those traces (closed-form or a few hundred offline SGD steps in Python).
3. Generation 0 = cloned net + small noise (σ=0.02) across P-1 siblings + one exact clone as champion.

Result: generation 0 already behaves like today's bots; evolution only needs to *beat* them.

## 7. What we are NOT doing yet

- Recurrent memory (GRU/LSTM). `_lastKnownTargetPos` + `_targetVel` are already explicit features; a recurrent net is a later ablation.
- Convolution over voxels. LOS is a binary gate; full voxel sight would need a map embedding.
- Multi-agent communication. Each bot's net sees only its own obs; self-play diversity comes from the GA population, not a shared policy.

## 8. Size and cost (for the dedi)

| Item | Value |
|---|---|
| Weights per genome | ~325 floats (1.3 KiB), up to ~600 with NEAT |
| Forward FLOPs | ~500 MACs/bot/tick |
| 16 bots @ 20 Hz | ~160k MACS/s — negligible vs physics |
| JSON `best.json` | ~5 KiB, loaded once at `GameStartDone` |
| No native deps | Pure C# loops; no `DllImport`, no ONNX Runtime |

## 9. Decision matrix

| Choice | Pick | Rationale |
|---|---|---|
| Fixed 16 vs 32 hidden | 16 | Dedi-friendly; expand only if ablations show headroom |
| Tanh vs ReLU hidden | Tanh | Bounded, no dead units under mutation noise |
| NEAT from day 1 | No | Fixed is debuggable; NEAT when plateau detected |
| ES vs GA | GA first | Simpler to implement in C# + Python harness; ES later |

## 10. Open questions (tracked, not blocking)

1. Should `WeaponProfile.Range` be baked into the genome's range head or left as a hard clamp? (Leaving as clamp is safer for now.)
2. Does the net need a `timeSinceLastShot` feature? Cheap to add; measure ablation.
3. Innovation log for NEAT — keep in `evolved/` or in the trainer's DB? (File is fine < 100 KiB.)
