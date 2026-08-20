# R11 — Residual Sweep: Duel Re-Test, Champion Plateau, Live-Verification Limit (2026-08-21)

*Follow-up to R10: re-test duel discrimination under policy-driven movement, push
the champion past 11.0 held, and verify the neural movement live. Status:
`verified` for the numbers, `blocked`/`residual` for the two items that could not
be closed headlessly.*

## 1. Commit

The full R10 change set (sim movement rework, C# port, champion, report, viz) is
committed as `b5b1da1`; working tree clean after the sweep.

## 2. Duel re-test (movement is now policy-driven)

Champion 1v1 vs the static no-brain, 24 seeds, canonical sim:

```
4W / 14L / 6T  =  17% win rate
```

Below the 70% threshold, so the arena mix stays unchanged (`verified`).

Root cause, tested: the sim applies **no accuracy penalty to moving targets**.
Hit chance depends only on (skill, distance, aim bias, spread), so kiting or
orbiting gives the shooter zero defensive benefit. A target-movement accuracy
penalty (MOVE_HIT_PENALTY 0.45 then 0.25) was implemented and measured in both
njit variants: it makes open-env duels winnable for the kiter but does NOT move
the overall duel win rate (12-17%: maze/cross env LOS windows stall fights into
trades regardless of the accuracy model) and it regresses the aggregate by
slowing bot-vs-bot combat inside horde/FFA (champion held 10.93 -> 9.57 at 0.45,
10.48 at 0.25). The change was reverted; the env LOS structure, not the accuracy
model, is the binding constraint on duel discrimination.

## 3. Champion push (F=36, two fresh runs)

| run | train peak | held avg (canonical gate) |
|-----|-----------|---------------------------|
| warm-start from champion, seed 42 | +14.17 | 10.934 (tie) |
| warm-start from champion, seed 777 | +14.17 | 10.448 (regressed) |

Both failed to reach 11.0. Per the stop rule, the best measured champion
(10.934, margins +6.06/+6.01/+5.85, GOAL MET) is kept; the run-777 genome was not
promoted. Conclusion: the champion is at a hard plateau ~10.9 for this
architecture; the train/held gap (train +14.17 vs held 10.93) persists.

## 4. Live kite verification (isolated dedi instance)

Attempted twice via telnet on an isolated Navezgane instance (new DLL loaded,
champion loaded, bots fight, zero mod exceptions). A controlled bot-vs-zombie
engagement could not be produced headlessly: `spawnzombie` has no anchor without
a connected player, ambient zombie encounters are sporadic, and bot-vs-bot chases
dominate the spawn area (one pair was LOS-stalled in Chase for over a minute).
The kite/orbit pattern in live 3D therefore remains a `residual`: it requires a
real client or the 7dtd-playtest suite (sibling repo, out of scope), which was
not available in this session. The code path itself runs without exceptions.

## 5. Final state

- Shipped champion: gen 299, held 10.934, margins +6.06/+6.01/+5.85, GOAL MET.
- Committed at `b5b1da1`; working tree clean.
- Duel discrimination: 17% (below threshold, arena unchanged, root cause noted).
- Live kite pattern: unverified headlessly, residual documented.
- Next steps: duel discrimination requires a change to the duel ENV structure
  (open-field duels with fixed loadouts), not an accuracy model; the playtest
  client suite for live kite verification.
