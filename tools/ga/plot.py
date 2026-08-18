#!/usr/bin/env python3
"""Plot fitness.csv + Pareto scatter."""

from __future__ import annotations

import argparse
import csv
from pathlib import Path

try:
    import matplotlib.pyplot as plt  # type: ignore
    HAS_MPL = True
except ImportError:
    HAS_MPL = False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("csv", help="evolved/runs/<ts>/fitness.csv")
    ap.add_argument("--out", default=None)
    args = ap.parse_args()
    p = Path(args.csv)
    gens, best, mean = [], [], []
    with open(p) as f:
        r = csv.DictReader(f)
        for row in r:
            gens.append(int(row["gen"]))
            best.append(float(row["best"]))
            mean.append(float(row["mean"]))
    if not HAS_MPL:
        print(f"gens {len(gens)}  best last {best[-1] if best else 'n/a'}")
        return
    out = Path(args.out) if args.out else p.parent / "plot.png"
    plt.figure()
    plt.plot(gens, best, label="best")
    plt.plot(gens, mean, label="mean")
    plt.xlabel("generation"); plt.ylabel("fitness (synthetic)")
    plt.legend(); plt.tight_layout()
    plt.savefig(out)
    print(f"saved {out}")

if __name__ == "__main__":
    main()
