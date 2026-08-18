#!/usr/bin/env python3
"""Behavioural-cloning warm-start stub (docs/research/04 §4, 05-integration §6).

For R0 this is a dry-run that validates the weight contract without needing
heuristic traces. The real clone (gradient descent on heuristic `obs→label`
pairs) will replace the body when traces exist; the contract (flat weight
order, best.json shape) stays.
"""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np

import ga

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--heuristic-traces", dest="traces", default=None)
    ap.add_argument("--out", default="evolved/clone.json")
    args = ap.parse_args()
    rng = np.random.default_rng(0)
    w = ga.he_init(rng)
    config = {"heuristic_traces": args.traces, "steps": 0, "lr": 0.01}
    ga.save_best(Path(args.out), w, generation=0, fitness=0.0, config=config)
    print(f"clone -> {args.out}  ({ga.W} weights, synthetic warm-start)")
