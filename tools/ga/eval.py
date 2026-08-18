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
    ap.add_argument("--held-out", action="store_true", help="held-out opponent/map pool flag (stub)")
    ap.add_argument("--matches", type=int, default=30)
    args = ap.parse_args()

    w, meta = load_best(Path(args.best))
    if w.size != ga.W:
        raise SystemExit(f"weights size {w.size} != want {ga.W}")

    # Use a different run_seed than training so it's held-out-ish
    seed = 999
    scores = [harness.synthetic_fitness(w, seed + m) for m in range(args.matches)]
    print(f"best gen {meta.get('generation')} fitness {meta.get('fitness'):+.4f}")
    print(f"re-eval {len(scores)} matches  mean {np.mean(scores):+.4f}  stdev {np.std(scores):.4f}")

    # Report the heuristic stub as a placeholder (real heuristic would be a JSON too)
    print("(held-out pool: synthetic; wire to real opponent pool when harness is live)")


if __name__ == "__main__":
    main()
