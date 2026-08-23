#!/usr/bin/env python3
"""Re-evaluate a single best.json on the held-out pool (docs/research/04 §8)."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

import harness
import ga


def load_best(path: Path):
    obj = json.loads(path.read_text())
    w = np.array(obj["weights"], dtype=np.float32)
    return w, obj


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("best", type=str, help="evolved/best.json")
    ap.add_argument("--matches", type=int, default=30)
    args = ap.parse_args()

    best_path = Path(args.best)
    if not best_path.is_file():
        raise SystemExit(f"best.json not found: {best_path} (e.g. evolved/best.json)")
    w, meta = load_best(best_path)
    if w.size != ga.W:
        raise SystemExit(f"weights size {w.size} != want {ga.W}")

    # Use combat harness with held-out seed so it's not the training roll
    scores = [harness.evaluate(w, 999, m, run_seed=999) for m in range(args.matches)]
    print(f"best gen {meta.get('generation')} fitness {meta.get('fitness'):+.4f}")
    print(f"held-out re-eval {len(scores)} matches  mean {np.mean(scores):+.4f}  stdev {np.std(scores):.4f}")

    # Also show a random baseline for the same seed so combat sim is discriminative
    rng = np.random.default_rng(123)
    w_rand = ga.he_init(rng)
    rand_scores = [harness.evaluate(w_rand, 999, m, run_seed=999) for m in range(args.matches)]
    print(f"random baseline  mean {np.mean(rand_scores):+.4f}  stdev {np.std(rand_scores):.4f}  (delta {np.mean(scores)-np.mean(rand_scores):+.2f})")


if __name__ == "__main__":
    main()
