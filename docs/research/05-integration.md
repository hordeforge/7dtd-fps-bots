# Integration — `BotNeuralBrain` Inside `Bot.cs`

## 1. Design rule

**The neural net is advisory, not authoritative.** Every gate the heuristic already has (`CanSee`, `VisionRange`, `Weapon.Range`, `ReactionTime`, `BurstPause`, `LOS`, move caps) stays. The net only decides *which* admissible move/intent to queue; the host may ignore it. This is the same authority model zdtd uses (`wasm_host` → `BotManager`) and it keeps the mod dedi-legal: a broken net cannot no-clip, godmode, or wallhack.

## 2. Placement in the call graph

```
Bot.Tick(dt, world)
  ├─ target scan   ──► BotBrain.FindTarget            (still heuristic)
  ├─ retreat gate  ──► TryEval → wantRetreat          (RetreatToCover; hp/trait check is the fallback)
  ├─ aim bias      ──► TryEval → aimBiasYaw           (AttackInRange; scales the per-engagement window)
  ├─ fire gate     ──► TryEval → shouldFire           (inside TryShootBurst;
  │                    still ANDed with _reactionUntil, _burstLeft, _burstPauseUntil, LOS, range)
  ├─ move intent   ──► TryEval → retreat/strafe logits compose a 2D velocity via BotBrain.MoveDir
  │                    (Q3-style MoveTo/Strafe/Backpedal is the fallback; clamps inside)
  └─ idle camp     ──► WantsToCamp / WantsIdleCamp    (stays heuristic on purpose: zombies must
                       pull idle bots out of cover, so no neural gate sits on this branch)
```

Every site funnels through one cached forward pass per tick (`Bot.TryNeuralOnce`
wrapping `BotNeuralBrain.TryEval`; no model → heuristic fallback, no throw), so
a thrown `NullRef` / `IndexOutOfRange` from a malformed `best.json` never
crashes the tick — it logs once and falls back.

## 3. File and API

```
Source/BotMod/AI/BotNeuralBrain.cs
```

| Symbol | Purpose |
|---|---|
| `static bool TryLoad(string path, out string reason)` | Reads one weights JSON (`evolved/best.json` by default; `best.meta.json` is trainer-side bookkeeping the mod never reads), validates version/inputs/shape/finite weights, keeps `LoadedHash` from the file's own `configHash`. Runs on world start when `UseNeuralBrain=true`, plus `bot neural on|reload [path]` and the web `neural` action. |
| `static bool Loaded` | Whether a valid model is in memory |
| `static bool TryEval(in NeuralInputs obs, out NeuralOutputs outs)` | 325-float forward pass, handwritten loops, no allocs on tick; returns false (caller falls back) when not loaded or on any internal error |
| `struct NeuralInputs` | 14 floats matching `01` §2, normalized already |
| `struct NeuralOutputs` | 5 advisory floats (§`01` §3) |

### 3.1 Forward pass (no alloc, no framework)

```csharp
// hidden = tanh(W1*x + b1), out = mixed(W2*hidden + b2)
// W1: 14*16, b1: 16, W2: 16*5, b2: 5
public static void Forward(in float[] w, in float[] x, ref float[] hidden, ref float[] y) {
    for (int h=0; h<16; h++) { float s=b1[h]; for (int i=0;i<14;i++) s += W1[h*14+i]*x[i]; hidden[h] = (float)Math.Tanh(s); }
    for (int o=0;o<5;o++)  { float s=b2[o]; for (int h=0;h<16;h++) s += W2[o*16+h]*hidden[h]; y[o]=s; }
    // y[0],y[1],y[3],y[4] sigmoid; y[2] tanh scaled
}
```

- Arrays are `static readonly` buffers sized at startup; the tick reuses them (no `new`).
- Weights order is canonical: `W1 row-major (16×14) | b1 | W2 row-major (5×16) | b2`. Documented in `evolved/README.md` and in the file header so Python and C# never drift.
- Clamp outputs: `sigmoid(x)=1/(1+exp(-x))` with `x` clamped to `[-8,8]` so `exp` never under/overflows on Mono.
- Micro-opt: run `forward` every *other* scan period, not every tick, if profiling ever shows cost (we have ~19 µs/bot budget before it matters — see §6).

### 3.2 Flat JSON contract

```json
{
  "version": 1,
  "hidden": 16,
  "inputs": 14,
  "outputs": 5,
  "weights": [ -0.017, 0.203, ... ],
  "configHash": "sha256_of_hp_table",
  "fitness": 0.81,
  "generation": 47
}
```

JSON is the only format `TryLoad` accepts today (it parses the file as JSON text and validates `weights.length == W`). A binary `Float32LE` blob (`best.bin`) remains an option if weight-file IO ever matters; it would go through the same validator.

## 4. Fallback and feature flags

| Config flag | Location | Effect |
|---|---|---|
| `UseNeuralBrain` (bool, default `false` in `BotConfig.cs`) | `BotConfig` → `botmod.json` | When false, `BotNeuralBrain` is never called — heuristic only. The deployed `config/botmod.json` ships it **true** since R13: the validation gates were met (R12/R13 GOAL MET) and the champion held 13.04 avg after the magazine alignment. |
| `BotNeuralWeightPath` (string, default `evolved/best.json`) | `BotConfig` | Where to load the model from |
| `bot neural reload` | console command | Re-reads `best.json` without restarting the server |
| `bot neural off/on` | admin | Toggles flag live; useful for blind tests |

If `TryLoad` fails (missing file, short array, weight count or NaN/Inf, unknown
`version`) the mod does:

```csharp
ModApi.Warn("BotNeuralBrain not loaded (" + reason + "), using heuristic.");
_loaded = false; // tick sees Loaded==false
```

No exception propagates to `Bot.Tick`.

> The file's `configHash` never affects loadability (`BotNeuralBrainFuzzTests`
> pins this): it is recorded into `LoadedHash` and shown by `bot neural status`,
> but a mismatch with the current hyperparam table does not reject the file.

## 5. Per-bot own trick: no cross-bot state

`BotNeuralBrain` holds no per-bot mutable state (weights are shared). Any per-bot scratch (e.g., hidden-state for a future recurrent net) lives on the `Bot` instance, not statical — otherwise two bots would alias each other's memory. Phase 1's fixed MLP needs no scratch at all.

## 6. Performance

- Forward pass: ~500 MACs → ~2 KiB memory loads → ~19 µs/bot on Mono (measured on `net48`, not guessed).
- 16 bots × 20 Hz → ~0.3 ms/s — invisibly small vs the 45 ms physics budget.
- No native calls, no `DllImport`, no `System.Numerics.Vectors` dependency (Mono ships without it on some distros).

## 7. Testing

- `tests/BotMod.Web.Tests/BotNeuralBrainFuzzTests.cs` (**shipped**): compiles `BotNeuralBrain.cs` against a `ModApi.ModPath` stub and fuzzes `TryLoad` (byte-level + structure-aware JSON mutants of the golden `best.json`) plus `TryEval` (sane/extreme observations must stay finite, bounded, internally consistent; non-14 input counts rejected). Run via `bash scripts/test-idempotency.sh` (`make test`; skipped when the game's Newtonsoft.Json.dll is absent).
- Harness comparison: run the same deterministic match twice with `UseNeuralBrain=false` vs `true` and diff the replay traces — they must differ only via net decisions, not physics.

## 8. Migration path

1. Ship heuristic as always. `UseNeuralBrain=false` in `config/botmod.json`.
2. Train offline (`04-training-pipeline.md`), promote `evolved/best.json`.
3. Operator flips `UseNeuralBrain true` + `bot neural reload`, watches fitness on the live map, flips back instantly if it regresses.
4. Eventually default the flag to true once multi-day validation holds.

No wire change, no client mod, no EAC interaction. A server that never pulls `best.json` never sees the net.

## 9. What we defer

- Recurrent hidden state (would need per-bot scratch and a sequence eval).
- On-dedi mutation (background GA thread). Phase 1 is offline-only; the dedi is not a trainer.
- Guard AI trace logging (already available via `ModApi.Log`; no new pipe needed).
