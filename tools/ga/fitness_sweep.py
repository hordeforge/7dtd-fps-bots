#!/usr/bin/env python3
"""fitness_sweep.py — proper weight sweep now that harness does scalarization."""
import sys, json
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import numpy as np, harness, ga, random
import combat_sim as cs

HOLD_SEED = 999
POP, GENS, SEED = 24, 22, 42

MIXES = [
    {"elo":0.55,"econ":0.25,"surv":0.15,"stuck":0.05, "tag":"canon_55-25-15"},
    {"elo":0.65,"econ":0.20,"surv":0.10,"stuck":0.05, "tag":"elo65_econ20_surv10"},
    {"elo":0.60,"econ":0.25,"surv":0.10,"stuck":0.05, "tag":"elo60_surv10"},
    {"elo":0.50,"econ":0.30,"surv":0.15,"stuck":0.05, "tag":"elo50_econ30"},
    {"elo":0.55,"econ":0.20,"surv":0.15,"stuck":0.10, "tag":"stuck10_econ20"},
    {"elo":0.45,"econ":0.25,"surv":0.25,"stuck":0.05, "tag":"surv25_elo45"},
    {"elo":0.70,"econ":0.15,"surv":0.10,"stuck":0.05, "tag":"elo70_econ15"},
]

def one_mix(mix):
    harness.FIT_ELO=mix["elo"]; harness.FIT_ECON=mix["econ"]; harness.FIT_SURV=mix["surv"]; harness.FIT_STUCK=mix["stuck"]
    harness.ACTIVATION=0; harness.CURRICULUM="mixed"
    rng=np.random.default_rng(SEED); random.seed(SEED); np.random.seed(SEED)
    pop_w=ga.clone_heuristic(rng, P=POP, sigma=0.02)
    best_f=float("-inf"); best_w=None
    for g in range(GENS):
        fit=harness.evaluate_population(pop_w, g, SEED)
        order=np.argsort(fit); f=float(np.max(fit))
        if f>best_f:
            best_f=f; best_w=pop_w[int(order[-1])].copy()
        if g==GENS-1: break
        ranked=np.empty(len(fit),dtype=float)
        ranked[order]=np.arange(len(fit))/max(1,len(fit)-1)
        pop_w=ga.next_generation(pop_w, ranked, order, rng, generation=g, total_gens=GENS)
    # held scoring always on canonical weights (apples-to-apples)
    harness.FIT_ELO=0.55; harness.FIT_ECON=0.25; harness.FIT_SURV=0.15; harness.FIT_STUCK=0.05
    scores=[harness.evaluate(best_w, 999, m, HOLD_SEED) for m in range(60)]
    held=float(np.mean(scores)); std=float(np.std(scores))
    return best_f, held, std, mix["tag"]

if __name__=="__main__":
    print(f"fitness sweep {len(MIXES)} mixes pop{POP}x{GENS} seed{SEED} (train on mix, held on canon)")
    rows=[]
    for mix in MIXES:
        bf, held, std, tag = one_mix(mix)
        rows.append((tag, bf, held, std, mix))
        print(f"  {tag:22s} train {bf:+.3f}  held60(canon) {held:+.3f} +- {std:.3f}")
    rows.sort(key=lambda r: r[2], reverse=True)
    print(f"\nwinner (held60 canon): {rows[0][0]}  held {rows[0][2]:+.3f}  train {rows[0][1]:+.3f}")
    # Artifacts land beside the other sweep outputs (evolved/runs/, same
    # convention as sweep.py), not a fixed /tmp path: /tmp is RAM-backed and
    # wiped on reboot, and the name is not run-parameterized.
    out_dir = Path("evolved/runs"); out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "fitness_sweep_R7.json").write_text(json.dumps([{"tag":t,"train":bf,"held":h,"held_std":s,"mix":m} for t,bf,h,s,m in rows], indent=2), encoding="utf-8")
    print(f"json -> {out_dir}/fitness_sweep_R7.json")
    # plot
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        fig,ax=plt.subplots(figsize=(8.5,3.2))
        tags=[r[0] for r in rows]; helds=[r[2] for r in rows]
        ax.bar(range(len(tags)), helds, color="#0369a1", alpha=0.9)
        ax.set_xticks(range(len(tags))); ax.set_xticklabels(tags, rotation=18, ha="right", fontsize=7)
        ax.set_ylabel("held60 (canon)"); ax.set_title("Fitness mix sweep — held60 on canonical (train on mix) pop24×22 seed42")
        ax.grid(True, axis="y", alpha=0.15)
        for i,v in enumerate(helds): ax.text(i, v+0.02, f"{v:.2f}", ha="center", fontsize=6)
        fig.tight_layout(); fig.savefig(out_dir / "fitness_sweep_R7.png", dpi=150); plt.close(fig); print(f"plot -> {out_dir}/fitness_sweep_R7.png")
    except Exception as ex: print(f"plot skipped: {ex}")
