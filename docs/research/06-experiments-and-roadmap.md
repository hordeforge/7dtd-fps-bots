# Experiments & Roadmap — What to Prove First

## 1. The question sequence

We are *not* trying to ship a self-improving bot in one go. The sequence answers one question at a time, each with a falsifiable check. If a check fails, we stop and reconsider instead of stacking complexity.

| Phase | Question | Experiment | Pass = |
|---|---|---|---|
| **0** | Does our current heuristic deserve to be the warm-start? | Log(`obs → heuristicOutputs`) for 1000 ticks, train the 14→16→5 net to imitate it, replay cloned net vs heuristic in 30 duels. | Clone is within 5% mean fitness of heuristic (`fitness_norm` drop < 0.05). If not, fix obs norm / hidden size before touching GA. |
| **1** | Does GA actually beat the heuristic? | Full `01` fixed MLP, `P=32, G=40, F=9`, curriculum A→B (§`04` §6), one run. Validate on held-out pool (`Hunter/Wrack` + fresh map patch, 30 matches, same seeds). | Validation mean fitness > heuristic + 0.10 norm units *and* damage efficiency not worse. If not, tune fitness weights before adding topology. |
| **2** | Does NEAT help when fixed plateaus? | Take phase-1 champion, enter NEAT (add-node/connection 0.15), `G+=40`. | Champion adds at least one structural mutation that survives 5+ gens. If flat, GA is sufficient — skip NEAT. |
| **3** | Does online background promotion help between offline runs? | Offline champ vs a low-priority background evolver that mutates 1 clone per 100 spare ticks. Long-running dedi, 24 h. | Background champ rate ≤ offline batch rate; it doesn't degrade or drift. If it drifts, keep training offline-only. |
| **4** | Can we close the sim-to-real gap? | Headless-champ replays on the live dedi (same seeds, same harness but real chunk IO). | Headless fitness and dedi fitness correlate r > 0.8 over 30 matches. If not, the headless map sampling mis-models real LOS. |

Each phase produces one `runs/<ts>/report.md` + `fitness.csv` plot. No phase starts until the previous one logged its report.

## 2. Ablations (answer with one knob per run)

| Ablation | Knob | Hypothesis |
|---|---|---|
| No cloning | Gen 0 = random weights | Clone halves generations to parity (§`01` §6) — measure the delta, don't assume |
| No rank-norm | Raw fitness for selection | Rank stabilizes evolution; prove it isn't cosmetic |
| No stuck penalty | Remove `stuckFrac` term | The penalty actually prevents wall-hugging, not just number-goes-down |
| No trait ΔCharacter tail | `W` only, traits fixed | Whether personality nudge matters or the net already learns it |
| Single arena vs all 3 | DM only vs 1v1+FFA+Horde | Three arenas prevent single-mode overfit |

Run each ablation as one knob overridden per run against the same seed so only the knob changes — in practice a dedicated sweep script that pins `harness.py` constants (`tools/ga/fitness_sweep.py` for fitness mixes, `tools/ga/sweep.py --hidden/--activations` for topology); `evolve.py` itself has no `--ablate` flag.

## 3. Metrics (what we watch while training)

Per generation:

- `best.fit` / `mean.fit` / `median.fit` / `q25/q75` (rank-normed).
- `diversity_pairwise` (mean L2 weight distance top quartile).
- Pareto cloud `(elo, econ)` for scatter, not scalar.

Per `best.json` promotion candidate (30-match validation):

- `Δfit vs heuristic`, `ΔdamageEff`, `headshotRate`, `campFrac`, `stuckTicks`.
- Human note: one-line operator impression after a live DM watch (the only subjective signal).

Dashboard: `tools/ga/plot runs/<ts>/fitness.csv --out runs/<ts>/plot.png` — best/mean envelope + diversity sparkline. Commit the PNG alongside the report.

## 4. Roadmap (calendar-agnostic)

| Milestone | Ships | Gate |
|---|---|---|
| **R0 — trace dump + clone** | `evolved/clone.json` + `docs/research/00..06` (this set) + headless replay script | Phase 0 passes |
| **R1 — GA champ** | `evolved/best.json` + `best.meta.json` + `runs/<first>` report | Phase 1 validation passes; blind test feels better |
| **R2 — harness hardening** | Curriculum + Hall-of-Fame + zstd'd match logs | R1 stable on a second seed |
| **R3 — NEAT branch** | `innovations.json` + growing topology tail | Only if R1 plateaus (guarded) |
| **R4 — mod integration** | `BotNeuralBrain.cs` wired into `Bot.Tick` behind `UseNeuralBrain` | Live flag flip is demo-able, revert is one command |
| **R5 — online evolver** | Background thread with feature flag + `bot neural off` | Only after R4 ships and stays green |

No date is on R3/R5 — they are conditional branches, not deadlines.

> Status (2026-08-21): R0-R1 shipped (`evolved/best.json` gen 199 champion,
> GOAL MET on the canonical gate), R4 shipped via R10/R12 —
> `UseNeuralBrain` defaults true in `config/botmod.json` since R13. Of R2,
> Hall-of-Fame and the curriculum flag ship; zstd'd match logs do not.
> NEAT (R3) and the online evolver (R5) remain unstarted. See `INDEX.md`
> for the authoritative current state.

## 5. Risks and open questions

| Risk | Mitigation | When to decide |
|---|---|---|
| Overfit to headless LOS (wall check is cheap, not exact) | Correlate headless vs dedi before promoting (phase 4) | Before R1 promotion |
| Ops distrust "neural bot" on live servers | Flag stays `false` until validation; revert is `bot neural off`; heuristic is never deleted | At R4 |
| Curse of tuning (too many GA knobs) | Freeze table `03` §4; sweep at most one knob per ablation | Ongoing |
| Recurrent nets blow up latency | They are deferred; fixed MLP stays until ablations ask for memory | Post-R1 |
| NEAT bloat without gain | Growth cap 600 weights; scale `add-connection` down if bloat persists | At R3 |

## 6. What "self-improving" will look like when it's real

An operator runs `make ga-evolve` overnight, gets a `best.json` in the morning with a Slack/Discord note:

```
gen 47 champ:  fit 0.83 (+0.11 vs heu), kd 1.4×, camp 0.18
promote?  [y/N]   dry-run --diff plots in evolved/runs/<ts>/
```

They run `bot neural reload` on the dedi; the next DM feels harder without feeling unfair (`BotVsBot/Player` gates unchanged). They can always `bot neural off` and nothing is lost. That is the only user-visible surface.

## 7. How to kill the project cleanly

If two consecutive phase-1 runs with doubled `F` and warm-start both fail to beat heuristic on held-out maps, the heuristic *is* the answer for this game and this harness. In that case:

1. Document the negative result in `docs/research/06` with plots and seeds.
2. Keep `best.json` unpromoted, leave `UseNeuralBrain=false`.
3. The bots stay good, useful, and shipped — no wasted work, just an honest ceiling.
