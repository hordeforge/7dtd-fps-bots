#!/usr/bin/env python3
"""dashboard.py: one polished, self-contained training dashboard for the GA evolutions.

Assembles, into a single HTML file:
  1. Evolution curves: every run overlaid (best/mean) with IQR band + current champion
     highlighted, plus a champion-is-best callout.
  2. Held-out stability strip: per-run final held (seed 999) ranked.
  3. Neural-net controller viz (reuse viz.py's diagram, embedded as a PNG).
  4. Arena replays: top-down canvas matches of the champion on multiple seeds/arenas
     (reuse replay.py; embedded as inline HTML frames, each mounted into its
     iframe when the card scrolls into view).
  5. Per-run summary table (pop/gen/curriculum/islands/held/verdict).

Usage:
  python tools/ga/dashboard.py [--runs run1 run2 ... | --all] --out docs/ga-dashboard.html
"""

from __future__ import annotations

import argparse
import base64
import csv
import html
import json
import math
import tempfile
from pathlib import Path

import numpy as np

# matplotlib is optional if no PNGs are wanted, but the dashboard is much richer with it.
try:
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    HAS_MPL = True
except Exception:  # pragma: no cover
    HAS_MPL = False

TOOLS = Path(__file__).resolve().parent          # repo/tools/ga
import sys as _sys  # noqa: E402 -- sibling modules resolve only after the sys.path bootstrap above
_sys.path.insert(0, str(TOOLS))
from replay import WALLS, record_match, render_html  # noqa: E402 -- same bootstrap
from viz import draw as draw_net  # noqa: E402 -- same bootstrap
import ga  # noqa: E402 -- same bootstrap
import report as _report  # noqa: E402 -- same bootstrap
import theme  # noqa: E402 -- same bootstrap

REPO = TOOLS.parent.parent                         # repo root (TOOLS is already repo/tools/ga)
RUNS_DIR = REPO / "evolved"                       # repo/evolved


def fig_b64(fig) -> str:
    """Palette-optimized PNG as base64 (quantizer lives in report.py: charts
    are flat-color figures, so an adaptive 256-color palette keeps them
    visually identical at ~3-4x smaller, which matters because the embedded
    charts are nearly all of this file's weight)."""
    return base64.b64encode(_report.optimized_png_bytes(fig)).decode()


def png_dimensions(data_b64: str) -> tuple[int, int]:
    """Intrinsic pixel size from the base64 PNG's IHDR chunk (no image lib).
    The IHDR ends at byte 24, i.e. base64 char 32."""
    import struct
    raw = base64.b64decode(data_b64[:32])
    w, h = struct.unpack(">II", raw[16:24])
    return w, h


def chart_card(data_b64: str, alt: str) -> str:
    w, h = png_dimensions(data_b64)
    return (f'<figure class="fig"><img alt="{alt}" src="data:image/png;base64,{data_b64}"'
            f' width="{w}" height="{h}" loading="lazy" decoding="async"></figure>')


def load_run_csv(run: Path):
    # Bounded open: this helper runs three times per run per build (curves,
    # held strip, run table), so each read must release its own descriptor.
    # Unparseable rows are skipped with one stderr note (same contract as
    # report.load_csv): a torn row in one old run's fitness.csv must not kill
    # the whole dashboard build; every section degrades independently.
    with (run / "fitness.csv").open(encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        return [], [], [], [], [], []
    gens, best, mean, q25, q75 = [], [], [], [], []
    skipped = 0
    for r in rows:
        try:
            g = int(r["gen"])
            b = float(r["best"])
            m = float(r["mean"])
            lo = float(r.get("q25") or r["mean"])
            hi = float(r.get("q75") or r["mean"])
            # Same guard as report.load_csv: float("nan") parses fine but
            # poisons every max/mean and delta downstream (max() over a list
            # holding NaN returns NaN), so the chart silently drops the
            # series. evolve.py writes a bare "nan" held column by design, and
            # a torn or hand-edited row can carry one into best/mean.
            if not all(map(math.isfinite, (b, m, lo, hi))):
                raise ValueError("non-finite fitness value")
        except (KeyError, TypeError, ValueError):
            skipped += 1
            continue
        gens.append(g); best.append(b); mean.append(m); q25.append(lo); q75.append(hi)
    if skipped:
        print(f"{run / 'fitness.csv'}: skipped {skipped} unparseable row(s)", file=_sys.stderr)
    held = []
    for r in rows:
        hv = r.get("held")
        if hv is None or hv == "" or hv == "nan":
            held.append(float("nan"))
        else:
            try:
                held.append(float(hv))
            except ValueError:
                held.append(float("nan"))
    return gens, best, mean, q25, q75, held


def run_cfg(run: Path) -> dict:
    """config.json of a run, or {} when unreadable. One corrupt or hand-edited
    config in an old run must not kill the whole dashboard build (every section
    degrades independently); the placeholders get a stderr reason instead."""
    try:
        return json.loads((run / "config.json").read_text(encoding="utf-8"))
    except Exception as ex:
        print(f"{run / 'config.json'}: unreadable ({ex.__class__.__name__}: {ex}); "
              f"table fields shown as None", file=_sys.stderr)
        return {}


def curves_b64(runs, best_run_name: str | None):
    fig, ax = plt.subplots(figsize=(12, 5.2))
    for run in runs:
        gens, best, mean, q25, q75, _ = load_run_csv(run)
        if not gens:
            continue
        label = run.name
        if run.name == best_run_name:
            ax.plot(gens, best, color=theme.ACCENT, lw=2.2, label=f"{label} (BEST)")
            ax.fill_between(gens, q25, q75, color=theme.ACCENT, alpha=0.10)
        else:
            ax.plot(gens, best, color=theme.SERIES_DIM, lw=1.0, alpha=0.85, label=label)
    ax.set_xlabel("generation", fontsize=10)
    ax.set_ylabel("fitness (scalar)", fontsize=10)
    ax.set_title("Evolution: best fitness per generation (all runs)", fontsize=13)
    ax.grid(True, alpha=0.15)
    ax.legend(fontsize=7, ncols=3, frameon=False, loc="lower right")
    fig.tight_layout()
    return fig_b64(fig)


def held_strip_b64(runs):
    # final held per run (seed 999, last non-nan entry)
    fig, ax = plt.subplots(figsize=(12, 2.8))
    labels, helds = [], []
    for run in runs:
        _, _, _, _, _, held = load_run_csv(run)
        vals = [v for v in held if v == v]  # drop nan
        if not vals:
            continue
        labels.append(run.name)
        helds.append(vals[-1])
    if not helds:
        # fig_b64 closes the figure on the success path (report.py closes it
        # before encoding); this early exit has to do the same or pyplot's
        # global figure registry keeps the FigureManager, its axes and its
        # canvas alive for the rest of the process.
        plt.close(fig)
        return ""
    order = np.argsort(helds)[::-1]
    labels = [labels[i] for i in order]
    helds = [helds[i] for i in order]
    cols = [theme.ACCENT] + [theme.SERIES_DIM] * (len(helds) - 1)
    ax.bar(range(len(helds)), helds, color=cols, alpha=0.9)
    ax.set_xticks(range(len(helds)))
    ax.set_xticklabels(labels, rotation=45, ha="right", fontsize=7)
    ax.set_ylabel("held (seed999)")
    ax.set_title("Held-out stability: final held per run (champion on the left)")
    ax.grid(True, axis="y", alpha=0.2)
    fig.tight_layout()
    return fig_b64(fig)


def best_net_b64():
    w, best = ga.load_best(RUNS_DIR / "best.json")
    hidden = int(best.get("hidden", 16))
    png = RUNS_DIR / "sweeps" / "viz_champion_dashboard.png"
    png.parent.mkdir(parents=True, exist_ok=True)
    # render to a matplotlib figure via viz.draw (saves PNG); embed that PNG as b64.
    draw_net(w, hidden, 14, title="Champion controller", out=png)
    # The diagram is the largest single blob in the page and viz.draw writes a
    # plain RGBA PNG; run it through the same palette quantizer the charts use
    # (report.quantized_png_bytes) instead of embedding it raw.
    return base64.b64encode(_report.quantized_png_bytes(png.read_bytes())).decode()


def champion() -> tuple[dict, object]:
    """(best.json's own record, the run seed that produced it or None).

    best.json is the champion and the only authoritative read of it; the
    generation, fitness and config hash shown in the header all come from that
    file. best.meta.json is a second file ga.save_best writes right after it,
    and it exists only to carry the run seed (the other three fields are
    already in best.json). The pair is written as two atomic replacements, so
    a crash between them can leave the meta describing the previous champion;
    its configHash is what says which of the two files is older, and a meta
    that does not match best.json contributes nothing but a wrong highlight."""
    _, obj = ga.load_best(RUNS_DIR / "best.json")
    seed = None
    try:
        meta = json.loads((RUNS_DIR / "best.meta.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        meta = None
    if meta is not None and meta.get("configHash") == obj.get("configHash"):
        seed = meta.get("seed")
    return obj, seed


def build(runs, out: Path, replays):
    best, best_seed = champion()
    best_run_name = None
    # best.run is the run hash; we mark whichever run we think produced best.json
    for run in runs:
        cfg = run_cfg(run)
        if best_seed is not None and cfg.get("seed") == best_seed:
            best_run_name = run.name

    chunks = []
    # Appended after the last section, not in place: the replay payloads are
    # the bulk of this file, and a script in the middle of the body holds the
    # parser at that byte offset, so everything after it waits.
    tail = []
    chunks.append(f"""<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Clanker: bot evolution</title>
<style>{theme.DARK_STYLE}</style></head><body><div class="wrap">
<header class="head">
<div>
<h1>Clanker: bot evolution</h1>
<p class="lede">Champion of the headless combat sim, 14&rarr;16&rarr;5 MLP, genetic algorithm, held-gated promotion.</p>
</div>
<dl class="meta">
<dt>generation</dt><dd>""")
    # best.json travels via git (whitelisted in evolved/.gitignore), so its
    # values are untrusted text from the dashboard's perspective: escape
    # before they land in the page.
    chunks.append(html.escape(str(best.get("generation", "?"))))
    chunks.append("""</dd>
<dt>train fitness</dt><dd>""")
    chunks.append(f"{best.get('fitness',0):.1f}")
    chunks.append("""</dd>
<dt>config hash</dt><dd>""")
    chunks.append(html.escape(str(best.get("configHash", "?"))[:8]))
    chunks.append("""</dd>
</dl>
</header>
""")

    if HAS_MPL:
        cd = curves_b64(runs, best_run_name)
        hs = held_strip_b64(runs)
        net = best_net_b64()
        # Each chart degrades independently: held_strip_b64 returns "" when no
        # run recorded held-out scores, and feeding "" through chart_card would
        # crash the whole dashboard on the PNG header parse.
        if cd:
            chunks.append(f"""<section class="sec"><h2>1 · Evolution curves</h2>{chart_card(cd, f'Line chart of the best fitness per generation across {len(runs)} runs; the champion run is highlighted')}</section>""")
        if hs:
            chunks.append(f"""<section class="sec"><h2>2 · Held-out stability</h2>{chart_card(hs, 'Bar chart of the final held-out score per run, champion run on the left')}</section>""")
        if net:
            chunks.append(f"""<section class="sec"><h2>3 · Champion controller (14&rarr;16&rarr;5)</h2>{chart_card(net, 'Diagram of the champion controller neural network: 14 inputs, 16 hidden units, 5 outputs')}</section>""")

    # Arena replays
    if replays:
        chunks.append('<section class="sec"><h2>4 · Arena replays</h2>')
        chunks.append('<p class="lede">Top-down matches of the champion in the sim, same seed as the run that promoted it.</p>')
        chunks.append('<div class="grid">')
        # Ids come from the frame's position, not from its label: str hashing is
        # salted per process, so the same runs produced different element ids on
        # every build, and two labels that collided on the same id left one
        # iframe without a srcdoc (a permanently blank card).
        frames = list(replays.items())
        for i, (label, _) in enumerate(frames):
            safe = html.escape(str(label), quote=True)
            chunks.append(f'<figure class="fig"><figcaption>{safe}</figcaption>'
                          f'<iframe id="replay-{i}" title="Arena replay {safe}" loading="lazy"></iframe></figure>')
        chunks.append('</div></section>')
        # Payloads stay base64 in the document (the output is one shareable
        # file), but each is decoded and handed to its iframe only when that
        # card approaches the viewport. Decoding all of them in one loop at
        # parse time blocked the host page on ~90 KB of atob per replay, and
        # mounted four canvas animations nobody had scrolled to. The fallback
        # is the pre-IntersectionObserver path: mount everything at once.
        tail.append("<script>")
        tail.append("const fr = {};")
        for i, (_, payload) in enumerate(frames):
            tail.append(f'fr["replay-{i}"] = "{base64.b64encode(payload.encode()).decode()}";')
        tail.append("""function deb64(s){ const bin=atob(s); const u8=new Uint8Array(bin.length); for(let i=0;i<bin.length;i++) u8[i]=bin.charCodeAt(i); return new TextDecoder().decode(u8); }
function mount(el){ el.srcdoc=deb64(fr[el.id]||""); }
if (typeof IntersectionObserver === "function") {
  const io = new IntersectionObserver((entries) => {
    for (const e of entries) { if (e.isIntersecting) { io.unobserve(e.target); mount(e.target); } }
  }, {rootMargin:"200px"});
  for (const k in fr) { const el=document.getElementById(k); if (el) io.observe(el); }
} else {
  for (const k in fr) { const el=document.getElementById(k); if (el) mount(el); }
}
</script>""")

    # Run table
    rows = []
    for run in runs:
        cfg = run_cfg(run)
        _, _, _, _, _, held = load_run_csv(run)
        heldv = [v for v in held if v == v]
        # A key the run config never wrote reads as "n/a", never as a value:
        # `None` in a results table is indistinguishable from a measured zero.
        rows.append((run.name,
                     *("n/a" if cfg.get(k) is None else cfg.get(k)
                       for k in ("pop", "gens", "curriculum", "islands")),
                     f"{heldv[-1]:.2f}" if heldv else "n/a"))
    rows.sort(key=lambda r: float(r[5]) if r[5] != "n/a" else 0, reverse=True)
    if rows:
        chunks.append("""<section class="sec"><h2>5 · Runs</h2><table><caption>Population, generations, curriculum, islands and final held-out score (seed 999) per run, best held first.</caption><thead><tr><th scope="col">run</th><th scope="col">pop</th><th scope="col">gens</th><th scope="col">curriculum</th><th scope="col">islands</th><th scope="col">held</th></tr></thead><tbody>""")
        # Every cell is filesystem/config text (run dir names, hand-editable
        # config.json values), so it is HTML-escaped before it lands in the page:
        # a crafted run name must not execute in the browser of whoever opens the
        # generated dashboard.
        for r in rows:
            cells = "".join(f"<td>{html.escape(str(c))}</td>" for c in r)
            chunks.append(f"<tr>{cells}</tr>")
        chunks.append("</tbody></table></section>")
    else:
        # An empty table reads as a measured zero; name what is missing instead.
        chunks.append('<section class="sec"><h2>5 · Runs</h2><p class="lede">'
                      'No run has recorded a fitness.csv yet. Run tools/ga/evolve.py, '
                      'then rebuild this dashboard.</p></section>')
    chunks.append(f"""<p class="foot">Built from {len(runs)} run(s) in evolved/runs. Replays are deterministic: the same seed replays the same match. Replay frames follow the pre-R10 sim rules, not the live game.</p></div>""")
    chunks.extend(tail)
    chunks.append("</body></html>")

    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text("".join(chunks), encoding="utf-8")
    return out


def main():
    ap = argparse.ArgumentParser(
        description="Assemble every GA run into one self-contained HTML dashboard.",
        epilog="""examples:
  %(prog)s                          # every run in evolved/runs
  %(prog)s --runs runs/<ts> --out /tmp/dash.html
  %(prog)s --replays                # embed arena replays (deterministic, ~seconds)

--runs paths are relative to evolved/. Paths given to --out are relative to
the current directory.

exit status:
  0  the dashboard was written
  1  a required run or evolved/best.json is missing
  2  bad command line""",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--runs", nargs="*", default=None, metavar="DIR",
                    help="run dirs relative to evolved/ (default: every run in evolved/runs)")
    ap.add_argument("--all", action="store_true",
                    help="use every run in evolved/runs (the default when --runs is omitted)")
    ap.add_argument("--out", default="docs/ga-dashboard.html", help="output HTML path (default: %(default)s)")
    ap.add_argument("--replays", action="store_true", help="include arena replays (deterministic, ~seconds)")
    args = ap.parse_args()
    if args.all and args.runs:
        # --all wins silently otherwise, so a typo'd --runs builds a dashboard
        # over every run and the caller never learns their selection was dropped.
        ap.error("--all and --runs are mutually exclusive; pass one or the other")

    if args.all or not args.runs:
        # Only directories are runs; a stray sweep PNG or a report.html sitting
        # beside them must not abort the whole dashboard.
        runs = sorted(p for p in RUNS_DIR.glob("runs/*") if p.is_dir())
    else:
        runs = [RUNS_DIR / r for r in args.runs]
    missing = [str(r) for r in runs if not r.is_dir()]
    if missing:
        raise SystemExit(f"--runs dir not found: {missing[0]} (paths are relative to {RUNS_DIR}, e.g. runs/<ts>)")
    for req in ("best.json", "best.meta.json"):
        if not (RUNS_DIR / req).is_file():
            raise SystemExit(f"{RUNS_DIR / req} not found (run tools/ga/evolve.py first)")

    replays = {}
    if args.replays:
        w, _ = ga.load_best(RUNS_DIR / "best.json")
        # Scratch replay HTML lives only long enough to be read back into the
        # dashboard string; the temp dir is removed on success and on failure.
        with tempfile.TemporaryDirectory(prefix="ga-dashboard-replay-") as tmp:
            for label, (seed, nb, nz, envf) in {
                "seed 1 · cross": (1, 4, 3, 1),
                "seed 777 · maze": (777, 5, 2, 4),
                "seed 1234 · maze": (1234, 4, 3, 4),
                "seed 42 · corridor": (42, 4, 3, 3),
            }.items():
                summary, frames = record_match(w, seed, nb, nz, 1200, 3, -1, envf)
                walls = WALLS[envf]
                html_path = Path(tmp) / f"replay_seed{seed}.html"
                render_html(summary, frames, walls, html_path, label)
                replays[label] = html_path.read_text(encoding="utf-8")

    out = build(runs, Path(args.out), replays)
    print(f"dashboard -> {out}  ({len(runs)} runs, {len(replays)} replays)")


if __name__ == "__main__":
    main()
