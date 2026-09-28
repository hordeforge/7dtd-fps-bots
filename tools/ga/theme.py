"""theme.py: one visual language for the generated GA HTML (report + dashboard).

The GA pages are read by the mod's own maintainers while a training run is
going on: an instrumentation console, not a product page. So the sheet is
built from three decisions and nothing else:

  - one warm near-black ramp (a warm ramp on a cold one is the difference you
    can see without naming it) with a single rust signal hue for anything the
    reader should look at first,
  - two faces (console dark, report light) from the same tokens, so a report
    and a dashboard side by side read as one family,
  - mono for every number, sans for every sentence, and no other ideas.

Matplotlib is a consumer of the same hues (report.py, dashboard.py), so a
chart and the page it sits on cannot drift apart.
"""

from __future__ import annotations

# Console dark face.
BG = "#0f0e0c"
SURFACE = "#171512"
LINE = "#2b2724"
FG = "#e8e2d9"
MUTED = "#a29a8f"

# Rust signal hue. ACCENT draws lines, rules and dots; ACCENT_TEXT carries
# small text (it is a step lighter so 12px labels keep 8:1 on BG).
ACCENT = "#e2762f"
ACCENT_TEXT = "#f2954a"

# Report face: the same ramp inverted onto warm paper.
PAPER = "#f6f3ee"
PAPER_FG = "#1a1714"
PAPER_MUTED = "#5d564d"
PAPER_LINE = "#ddd6ca"

# Chart series. The champion line is the accent, everything else is muted, so
# the reader never hunts for which run is which.
SERIES = ACCENT
SERIES_DIM = "#8c8378"
SERIES_BEST = "#3a332c"

# Render resolution for every chart these tools emit. The figures are 8.5-13.5
# in wide and land in a max-width 1040 px container, so the old 150-165 dpi
# spent up to 2.1x more pixels than any display shows, and the charts are
# base64-embedded in a committed HTML page where their bytes are the document's
# first-paint cost. 120 dpi still renders a 1080 px figure at 1:1 there and
# leaves a 1.5x margin on a 2x display, and cuts the champion diagram from 67 KB
# to 45 KB.
CHART_DPI = 120

# Chart ink, tuned against the white plate the PNGs sit on rather than against
# BG: matplotlib always renders on white, and the dark dashboard puts the figure
# on a white .fig for the same reason. viz.py reads these, so the largest
# figure on the page cannot carry a palette the page does not have.
CHART_INK = PAPER_FG        # 17.9:1 on white
CHART_INK_MUTED = PAPER_MUTED  # 7.2:1
CHART_RING = SERIES_DIM     # 3.7:1, a non-text ring so it only has to clear 3:1
# Layer fills step down the warm ramp so input -> hidden -> output reads as one
# progression instead of three unrelated dots. All clear 4.7:1 on white. The
# same three steps are the ordered scale for the activation traces, which are
# themselves ordered (healthy, wounded, camping), so an ordered ramp is the
# honest encoding there too and a second hue buys nothing.
CHART_LAYERS = ("#a8622c", "#7d3a1a", "#3a332c")
# Weight sign is data, not decoration, so it keeps a diverging pair instead of
# joining the one-hue ramp: a warm red negative against a blue from the same
# RdBu family the W1/W2 matrices are painted with, so an edge and the cell it
# weights never disagree about sign. 6.0:1 and 6.9:1 on white.
WEIGHT_NEG = "#b03a2e"
WEIGHT_POS = "#2f5d8a"

# The arena replay is a canvas, so it cannot read the CSS custom properties the
# page above it uses; these are the same tokens inlined into the script (see
# replay.py's @ARENA@). The ground is a step under BG so the arena reads as a
# lit surface inside the dark page.
ARENA_BG = "#14110e"
ARENA_EDGE = "#4a433c"
ARENA_WALL = SERIES_DIM
# Weapon is the one categorical scale in the family: six loadouts have to be
# told apart at a glance in a replay that plays at 80ms a frame, so it is the
# one place six hues are allowed. They sit in a mid-chroma band rather than the
# pastel row a default palette hands you, and the pistol step is the accent
# because it is the most common. All clear 5.4:1 on ARENA_BG.
WEAPON_RING = ("#e0644a", "#d9a441", "#c9c05a", "#5fa88f", "#7f9fd6", "#c98fc0")
# The bot's tag letter is cut out of its own ring, so the ink is the ground.
TAG_INK = ARENA_BG
# Muzzle flash and the held aim line are the same two steps theme.py already
# uses for "look at this": ACCENT_TEXT at full strength for the shot, a quarter
# of it for the aim the bot is holding. 9.9:1 on ARENA_BG.
SHOT = "#e8b53c"
AIM = ACCENT_TEXT
# Zombie green, then the bot's own health ramp. HP_WARN is a warmer yellow than
# SHOT on purpose: one is a bullet leaving the barrel, the other is a bar that
# is about to change state, and they are never drawn over each other.
ZOMBIE = "#4f9d78"
ZOMBIE_EDGE = "#3f7d61"
HP_TRACK = SURFACE
HP_OK = "#5fa860"
HP_WARN = "#c9962f"
HP_BAD = "#cc5148"

_BASE = """
*, *::before, *::after { box-sizing: border-box; }
body { margin: 0; padding: 0 20px 56px; }
.wrap { max-width: 1080px; margin: 0 auto; }

/* Type scale: 20 / 14 / 13 / 11.5, with mono for every number. */
h1 { font-size: 20px; line-height: 1.25; font-weight: 600; margin: 0; letter-spacing: -.01em; }
h2 { font-size: 13px; margin: 0 0 12px; font-weight: 600; }
p { font-size: 14px; line-height: 1.6; max-width: 68ch; }
.lede { margin: 6px 0 0; }
.meta { font-family: var(--mono); font-size: 12px; }

/* The header is the page's one anchor: title and champion readout on the
   same baseline, so the eye lands on the champion numbers first. */
.head { display: flex; align-items: flex-end; justify-content: space-between; gap: 32px;
  flex-wrap: wrap; padding: 30px 0 20px; border-bottom: 1px solid var(--line); }
.head dl { display: grid; grid-template-columns: auto auto; gap: 3px 14px; margin: 0; text-align: end; }
.head dt { color: var(--muted); }
.head dd { margin: 0; color: var(--accent-text); }
code, .num { font-family: var(--mono); }
.foot { font-size: 11.5px; margin: 40px 0 0; }

/* Sections are separated by a rule, not by a box: a chart, a replay and a
   table are different objects and one card radius around all three says
   nothing about any of them. */
.sec { margin-top: 40px; padding-top: 18px; border-top: 1px solid var(--line); }
.sec .lede { margin-bottom: 18px; }
.sec .grid { margin-top: 18px; }
figure { margin: 0; }
.fig { border: 1px solid var(--line); border-radius: 3px; overflow: hidden; }
.fig img { display: block; width: 100%; height: auto; }
.fig iframe { display: block; width: 100%; height: 430px; border: 0; }
.fig figcaption { font-family: var(--mono); font-size: 12px; color: var(--accent-text);
  padding: 7px 10px; border-bottom: 1px solid var(--line); }
.grid { display: grid; grid-template-columns: 1fr 1fr; gap: 24px; }
@media (max-width: 820px) { .grid { grid-template-columns: 1fr; } }

table { width: 100%; border-collapse: collapse; }
caption { text-align: start; font-size: 11.5px; margin-bottom: 8px; }
th, td { text-align: start; padding: 6px 10px 6px 0; font-size: 13px; }
thead th { font-weight: 600; font-size: 11.5px; letter-spacing: .04em; }
tbody td { font-family: var(--mono); border-top: 1px solid var(--line); }
tbody td:first-child { font-family: inherit; }
"""

DARK_STYLE = f"""
:root {{ --bg: {BG}; --surface: {SURFACE}; --line: {LINE}; --fg: {FG}; --muted: {MUTED};
  --accent: {ACCENT}; --accent-text: {ACCENT_TEXT}; --arena: {ARENA_BG};
  --sans: ui-sans-serif, system-ui, "Segoe UI", Roboto, sans-serif;
  --mono: ui-monospace, SFMono-Regular, "Cascadia Mono", Menlo, Consolas, monospace; }}
body {{ background: {BG}; color: {FG}; font-family: var(--sans); }}
h1 {{ color: {FG}; }}
h2 {{ color: var(--accent-text); font-family: var(--mono); letter-spacing: .06em; }}
.lede, .foot, caption {{ color: var(--muted); }}
{_BASE}
thead th {{ color: var(--muted); border-bottom: 1px solid var(--line); }}
code {{ background: {SURFACE}; color: {FG}; padding: 1px 5px; border-radius: 3px; font-size: 12px; }}
/* Charts are rendered on white by matplotlib; the plate keeps them from
   floating on the dark page with nothing under them. */
.fig {{ background: #fff; }}
"""

LIGHT_STYLE = f"""
:root {{ --bg: {PAPER}; --surface: #fff; --line: {PAPER_LINE}; --fg: {PAPER_FG}; --muted: {PAPER_MUTED};
  --accent: #b4551a; --accent-text: #9c4a15;
  --sans: ui-sans-serif, system-ui, "Segoe UI", Roboto, sans-serif;
  --mono: ui-monospace, SFMono-Regular, "Cascadia Mono", Menlo, Consolas, monospace; }}
body {{ background: {PAPER}; color: {PAPER_FG}; font-family: var(--sans); }}
h1 {{ color: {PAPER_FG}; }}
h2 {{ color: var(--accent-text); font-family: var(--mono); letter-spacing: .06em; }}
.lede, .foot, caption {{ color: var(--muted); }}
{_BASE}
thead th {{ color: var(--muted); border-bottom: 1px solid var(--line); }}
code {{ background: #ece7de; color: {PAPER_FG}; padding: 1px 5px; border-radius: 3px; font-size: 12px; }}
"""

# The canvas cannot read the custom properties above, so replay.py is handed
# this dict as JSON and the draw loop reads A.<token> like the CSS reads
# var(--token). One source for both surfaces is the point: a legend dot and the
# ring it labels are the same value, so they cannot disagree.
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
