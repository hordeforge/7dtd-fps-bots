"""theme.py: one visual language for the generated GA HTML (report, dashboard,
replay) and the matplotlib charts inside them.

Values come from the HordeForge brand tokens (hordeforge/.github
brand/tokens.css). The GA pages are data tools, so they use the brand's
terminal palette: one dark ground, one signal green for whatever the reader
should look at first, red for bad, amber for keys and warnings. The report
keeps a paper face from the same tokens, and every chart renders on the
terminal plate whichever page it sits on, so a chart reads the same in both.
Sans for sentences, mono for ids and numbers.

This module is the only place a color or a size is set: the generators and
viz.py read the constants below, never a hex of their own.
"""

from __future__ import annotations

# Terminal face (brand --term-*).
BG = "#101418"
SURFACE = "#1a2129"
LINE = "#2a333d"
FG = "#d8e2dc"
MUTED = "#7f8b94"      # 5.3:1 on BG, 4.7:1 on SURFACE
ACCENT = "#5fd894"     # the signal; 10.3:1 on BG, so it also carries text
BAD = "#ff7364"
KEY = "#ffd8a0"

# Paper face (brand --background, --card, --foreground, --muted-foreground,
# --border, --secondary, --primary), used by report.py.
PAPER = "#f7f5f0"
PAPER_CARD = "#fffdf8"
PAPER_FG = "#1a1d21"
PAPER_MUTED = "#4d545c"  # 7.0:1 on PAPER
PAPER_LINE = "#e2ddd0"
PAPER_CODE = "#efece4"
PAPER_ACCENT = "#0f5c37"  # 7.4:1 on PAPER

FONT_SANS = '-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif'
FONT_MONO = 'ui-monospace, SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace'

# Chart series. The champion line is the signal, everything else is muted, so
# the reader never hunts for which run is which. SERIES_BEST is the brightest
# ink, for the report's best-so-far line.
SERIES = ACCENT
SERIES_DIM = MUTED
SERIES_BEST = FG

# Render resolution for every chart these tools emit. The figures are 8.5-13.5
# in wide and land in a max-width 1080 px container; 120 dpi renders a 1080 px
# figure at 1:1 and leaves a 1.5x margin on a 2x display. The charts are
# base64-embedded, so their bytes are the page's first-paint cost.
CHART_DPI = 120

# Chart plate and ink: every figure renders on SURFACE, one step above the
# page ground, with FG / MUTED text (12.2:1 and 4.7:1 on SURFACE).
CHART_BG = SURFACE
CHART_INK = FG
CHART_INK_MUTED = MUTED
CHART_GRID = LINE
CHART_RING = MUTED
# Layer fills step down one green ramp so input -> hidden -> output reads as
# one progression. The same three steps are the ordered scale for the
# activation traces (healthy, wounded, camping), which are themselves ordered.
# All clear 3:1 on CHART_BG as non-text marks.
CHART_LAYERS = ("#a8ecc4", ACCENT, "#3aa56c")
# Weight sign is data, so it keeps a diverging pair: the mid-strength red and
# blue of the RdBu map the W1/W2 matrices are painted with, so an edge and the
# cell it weights never disagree about sign. 4.4:1 and 4.8:1 on CHART_BG.
WEIGHT_NEG = "#d6604d"
WEIGHT_POS = "#4393c3"

# The arena replay is a canvas, so it cannot read CSS custom properties; these
# are handed to replay.py's draw loop as JSON (@ARENA@). The arena is the
# raised surface inside the dark page.
ARENA_BG = SURFACE
ARENA_EDGE = LINE
ARENA_WALL = MUTED
# Weapon is the one categorical scale in the family: six loadouts have to be
# told apart at a glance in a replay that plays at 80 ms a frame. All clear
# 4.7:1 on ARENA_BG, and the tag letter cut out of each (TAG_INK) clears the
# same against its ring.
WEAPON_RING = ("#e0644a", "#d9a441", "#c9c05a", "#5fa88f", "#7f9fd6", "#c98fc0")
TAG_INK = ARENA_BG
# Muzzle flash is the key amber; the held aim line is the signal at a quarter
# alpha. Zombies take a desaturated teal so they do not read as the signal.
SHOT = KEY
AIM = ACCENT
ZOMBIE = "#4f9d78"
ZOMBIE_EDGE = "#3f7d61"
HP_TRACK = LINE
HP_OK = ACCENT
HP_WARN = KEY
HP_BAD = BAD

# Clanker product tile (hordeforge/.github brand/tiles/clanker.svg) as the
# page icon: guard-violet ground, paper glyph (Lucide "bot", ISC license).
FAVICON = (
    '<link rel="icon" href="data:image/svg+xml,%3Csvg%20xmlns=%27http://www.w3.org/2000/svg%27'
    '%20viewBox=%270%200%2032%2032%27%3E%3Crect%20width=%2732%27%20height=%2732%27%20rx=%277%27%20fill=%27%234b2f8a%27/%3E'
    '%3Cg%20transform=%27translate(4%204)%27%20fill=%27none%27%20stroke=%27%23f7f5f0%27%20stroke-width=%272%27'
    '%20stroke-linecap=%27round%27%20stroke-linejoin=%27round%27%3E%3Cpath%20d=%27M12%208V4H8M2%2014h2M20%2014h2M15%2013v2M9%2013v2%27/%3E'
    '%3Crect%20width=%2716%27%20height=%2712%27%20x=%274%27%20y=%278%27%20rx=%272%27/%3E%3C/g%3E%3C/svg%3E">'
)


def chart_rc() -> dict[str, object]:
    """matplotlib rcParams for every GA chart: the terminal plate and ink.
    Callers apply it with plt.rcParams.update(theme.chart_rc()) after import."""
    return {
        "figure.facecolor": CHART_BG,
        "savefig.facecolor": CHART_BG,
        "axes.facecolor": CHART_BG,
        "axes.edgecolor": CHART_GRID,
        "axes.labelcolor": CHART_INK_MUTED,
        "axes.titlecolor": CHART_INK,
        "text.color": CHART_INK,
        "xtick.color": CHART_INK_MUTED,
        "ytick.color": CHART_INK_MUTED,
        "grid.color": CHART_GRID,
        "grid.alpha": 1.0,
        "legend.labelcolor": CHART_INK,
        "axes.axisbelow": True,
        "axes.spines.top": False,
        "axes.spines.right": False,
    }


_BASE = """
*, *::before, *::after { box-sizing: border-box; }
body { margin: 0; padding: 0 16px 56px; font-family: var(--sans); background: var(--bg); color: var(--fg); }
.wrap { max-width: 1080px; margin: 0 auto; }
:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }

/* Type scale: 22 / 16 / 14 / 12, sans for sentences, mono for ids. */
h1 { font-size: 22px; line-height: 1.25; font-weight: 700; margin: 0; }
h2 { font-size: 16px; line-height: 1.3; margin: 0 0 12px; font-weight: 600; }
p { font-size: 14px; line-height: 1.6; max-width: 68ch; }
.lede { margin: 6px 0 0; color: var(--muted); }
.meta { font-family: var(--mono); font-size: 12px; font-variant-numeric: tabular-nums; }

/* The header is the page's one anchor: title and champion readout on the
   same baseline, so the eye lands on the champion numbers first. */
.head { display: flex; align-items: flex-end; justify-content: space-between; gap: 16px 32px;
  flex-wrap: wrap; padding: 28px 0 4px; }
.head dl { display: grid; grid-template-columns: auto auto; gap: 3px 14px; margin: 0; text-align: end; }
.head dt { color: var(--muted); }
.head dd { margin: 0; color: var(--accent); }
code, .num { font-family: var(--mono); }
code { background: var(--code); padding: 1px 5px; border-radius: 4px; font-size: 12px; overflow-wrap: anywhere; }
.foot { font-size: 12px; margin: 40px 0 0; color: var(--muted); }

/* Sections are separated by a rule, not by a box. */
.sec { margin-top: 36px; padding-top: 18px; border-top: 1px solid var(--line); }
.sec .lede { margin-bottom: 18px; }
.sec .grid { margin-top: 18px; }
figure { margin: 0; }
.fig { border: 1px solid var(--line); border-radius: 8px; overflow: hidden; background: """ + CHART_BG + """; }
.fig img { display: block; width: 100%; height: auto; }
.fig iframe { display: block; width: 100%; height: 480px; border: 0; }
.fig figcaption { font-family: var(--mono); font-size: 12px; color: """ + FG + """;
  padding: 7px 10px; border-bottom: 1px solid """ + LINE + """; }
.grid { display: grid; grid-template-columns: 1fr 1fr; gap: 24px; }

/* Wide tables scroll inside their own box on a narrow screen instead of
   running off the page. */
.tablescroll { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; font-variant-numeric: tabular-nums; }
caption { text-align: start; font-size: 12px; margin-bottom: 8px; color: var(--muted); }
th, td { text-align: start; padding: 7px 16px 7px 0; font-size: 13px; white-space: nowrap; }
thead th { font-weight: 600; font-size: 12px; color: var(--muted); border-bottom: 1px solid var(--line); }
tbody td { font-family: var(--mono); border-top: 1px solid var(--line); }
tbody td:first-child { font-family: inherit; }
td.n, th.n { text-align: end; }

@media (max-width: 820px) {
  .grid { grid-template-columns: 1fr; }
  .head dl { text-align: start; }
}
"""

DARK_STYLE = f"""
:root {{ --bg: {BG}; --surface: {SURFACE}; --line: {LINE}; --fg: {FG}; --muted: {MUTED};
  --accent: {ACCENT}; --code: {SURFACE}; --arena: {ARENA_BG};
  --sans: {FONT_SANS};
  --mono: {FONT_MONO}; color-scheme: dark; }}
{_BASE}"""

LIGHT_STYLE = f"""
:root {{ --bg: {PAPER}; --surface: {PAPER_CARD}; --line: {PAPER_LINE}; --fg: {PAPER_FG}; --muted: {PAPER_MUTED};
  --accent: {PAPER_ACCENT}; --code: {PAPER_CODE};
  --sans: {FONT_SANS};
  --mono: {FONT_MONO}; color-scheme: light; }}
{_BASE}"""

# The canvas cannot read the custom properties above, so replay.py is handed
# this dict as JSON and the draw loop reads A.<token> like the CSS reads
# var(--token). A legend dot and the ring it labels are the same value.
ARENA = {
    "BG": ARENA_BG,
    "EDGE": ARENA_EDGE,
    "WALL": ARENA_WALL,
    "WEAPON_RING": list(WEAPON_RING),
    "TAG_INK": TAG_INK,
    "SHOT": SHOT,
    "AIM": AIM,
    "ZOMBIE": ZOMBIE,
    "ZOMBIE_EDGE": ZOMBIE_EDGE,
    "HP_TRACK": HP_TRACK,
    "HP_OK": HP_OK,
    "HP_WARN": HP_WARN,
    "HP_BAD": HP_BAD,
}
