# Held-Out Repro Check — pop60×80 s999 (2026-08-19)

*Sanity after the pop40×80 g25 promotion.*

## Check

| Genome | Held 40 (seed 999) | vs random (+0.08) | Verdict |
|---|---|---|---|
| g25 pop40×80 g25 (current `best.json`) | **+11.82±1.03** | `+11.74` | keeps crown |
| pop60×80 s999 g64 (fresh, same harness) | `+11.64±1.02` | `+11.56` | does not beat `g25` on held-out |

So the promoted `g25` holds. The sweep already showed the harness can separate
layouts (H16 `+11.82`, H08 `+11.64` tie under noise) — no shape change needed.
The next bigger run should vary the *arena* (harder Horde / closer walls), not
just `pop`.

## Repro

```bash
python3 tools/ga/evolve.py --pop 60 --gens 80 --seed 999
python3 tools/ga/eval.py evolved/best.json --matches 40
python3 tools/ga/viz.py --best evolved/best.json --out /tmp/net.png
```

## Artifacts

- Kept `evolved/best.json` is still `pop40×80 seed42 g25` (`hash 885a3e9b`).
- Fresh run lives at `evolved/runs/2026-08-19_113140_pop60_g80_s999/` (archived, not promoted).
