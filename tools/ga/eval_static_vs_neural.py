#!/usr/bin/env python3
"""eval_static_vs_neural.py — the measuring stick for "GA beats our static bots".

Reports, per held seed, the held-out fitness of:
  - the evolved champion (evolved/best.json)
  - the static "no-brain" baseline (all-zero weights: every gate at sigmoid(0)=0.5,
    no aim bias, neutral strafe — i.e. a bot that just always fires)

Both are measured on the SAME harness (the currently-imported evaluator), so the
margin is apples-to-apples. The goal's finish line is
    champion_held - static_held >= +0.5   (every seed)
plus the static baseline dropping ~1.0 below 11.28 on the reworked eval.

Usage:
  python tools/ga/eval_static_vs_neural.py [--seeds 999 1234 4242] [--matches 40]
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np

TOOLS = Path(__file__).resolve().parent          # tools/ga
REPO = TOOLS.parent.parent                        # repo root
sys.path.insert(0, str(TOOLS))
import harness  # noqa: E402 -- sibling module resolves only after the sys.path bootstrap above

DEFAULT_SEEDS = [999, 1234, 4242]


def held(w: np.ndarray, seed: int, matches: int) -> tuple[float, float]:
    harness.FIT_ELO = 0.55
    harness.FIT_ECON = 0.25
    harness.FIT_SURV = 0.15
    harness.FIT_STUCK = 0.05
    harness.ACTIVATION = 0
    harness.CURRICULUM = "mixed"
    harness.DRAWS_PER_CONFIG = 1  # pin the measuring stick to the canonical F=18 sample
    sc = [harness.evaluate(w, 999, m, seed) for m in range(matches)]
    return float(np.mean(sc)), float(np.std(sc))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", nargs="*", type=int, default=DEFAULT_SEEDS, help="held seeds")
    ap.add_argument("--matches", type=int, default=40, help="held matches per seed")
    ap.add_argument("--best", default=str(REPO / "evolved/best.json"))
    args = ap.parse_args()

    best_path = Path(args.best)
    if not best_path.is_file():
        raise SystemExit(f"--best not found: {best_path} (e.g. evolved/best.json)")
    w_champ = np.array(json.loads(best_path.read_text(encoding="utf-8"))["weights"], dtype=float)
    w_static = np.zeros(w_champ.shape[0], dtype=np.float32)

    rows = []
    for seed in args.seeds:
        c_mean, c_std = held(w_champ, seed, args.matches)
        s_mean, s_std = held(w_static, seed, args.matches)
        margin = c_mean - s_mean
        rows.append((seed, c_mean, s_mean, margin, c_std, s_std))

    print(f"{'seed':>6} {'champion':>10} {'static':>10} {'margin':>8}")
    for seed, c, s, m, cst, sst in rows:
        print(f"{seed:>6} {c:>10.3f} {s:>10.3f} {m:>+8.3f}   (champ stdev {cst:.2f}, static stdev {sst:.2f})")
    ok = all(m >= 0.5 for _, _, _, m, _, _ in rows)
    print(f"\nGOAL MET (champion - static >= +0.5 on every seed): {ok}")
    if not ok:
        print("NOT met. The rework must push the static baseline down ~1.0 "
              "and evolve a champion that out-generates it by >= +0.5 per seed.")


if __name__ == "__main__":
    main()
