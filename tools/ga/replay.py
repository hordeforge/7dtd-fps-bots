#!/usr/bin/env python3
"""replay.py — deterministic arena match recorder + top-down HTML replay renderer.

The production fitness uses the numba `combat_sim.simulate_match` which discards
per-tick state. This module is a pure-Python recorder of the pre-R10 sim rules
(heuristic movement, retreat fire-suppression, old mag table), kept for
visualization; it stopped tracking combat_sim 1:1 when R10-R13 reworked
movement/fire/ammo. Traces are still deterministic per `(w, seed, ...)` but no
longer reproduce the exact match a numba eval scored.

`record_match` exposes the per-tick frames; `render_html`
writes a self-contained HTML canvas replay (no external JS).

Usage:
  python tools/ga/replay.py --best evolved/best.json --seed 42 --n-bots 4 \
      --n-zombies 3 --out docs/ga-replay.html
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import numpy as np

INPUTS = 14
WEAPON_DAMAGE = [16, 14, 16, 42, 9, 9]
WEAPON_RANGE = [40, 22, 55, 90, 22, 35]
WEAPON_PELLETS = [1, 8, 1, 1, 6, 1]
WEAPON_MAG = [12, 6, 30, 5, 6, 32]
WEAPON_RELOAD = [1.2, 2.6, 2.0, 2.5, 2.6, 1.8]
WEAPON_BURST_MIN = [1, 1, 3, 1, 1, 5]
WEAPON_BURST_MAX = [3, 1, 6, 1, 1, 9]

# Arena walls (env index 0..4), mirroring combat_sim.
WALLS = [
    [[20, 20, 20, 60], [20, 40, 60, 40], [50, 10, 50, 30]],
    [[40, 10, 40, 70], [10, 40, 70, 40], [15, 15, 65, 65], [65, 15, 15, 65]],
    [],
    [[40, 0, 40, 30], [40, 50, 40, 80], [0, 40, 30, 40], [50, 40, 80, 40]],
    [[30, 20, 50, 20], [50, 20, 50, 45], [30, 55, 55, 55], [20, 35, 20, 60], [60, 35, 60, 65], [45, 60, 68, 60]],
]


def _lcg(state):
    return (state * 1103515245 + 12345) & 0xFFFFFFFF


def _lcg01(state):
    state = _lcg(state)
    return ((state >> 8) & 0x00FFFFFF) / 16777216.0, state


def _orient(px, py, qx, qy, rx, ry):
    v = (qy - py) * (rx - qx) - (qx - px) * (ry - qy)
    if abs(v) < 1e-6:
        return 0
    return 1 if v > 0 else 2


def _on_seg(px, py, qx, qy, rx, ry):
    return (min(px, rx) - 1e-6 <= qx <= max(px, rx) + 1e-6 and
            min(py, ry) - 1e-6 <= qy <= max(py, ry) + 1e-6)


def _seg_intersect(ax, ay, bx, by, cx, cy, dx, dy):
    o1 = _orient(ax, ay, bx, by, cx, cy)
    o2 = _orient(ax, ay, bx, by, dx, dy)
    o3 = _orient(cx, cy, dx, dy, ax, ay)
    o4 = _orient(cx, cy, dx, dy, bx, by)
    if o1 != o2 and o3 != o4:
        return True
    if o1 == 0 and _on_seg(ax, ay, cx, cy, bx, by):
        return True
    if o2 == 0 and _on_seg(ax, ay, dx, dy, bx, by):
        return True
    if o3 == 0 and _on_seg(cx, cy, ax, ay, dx, dy):
        return True
    if o4 == 0 and _on_seg(cx, cy, bx, by, dx, dy):
        return True
    return False


def _los(ax, ay, bx, by, walls):
    for w in walls:
        if _seg_intersect(ax, ay, bx, by, w[0], w[1], w[2], w[3]):
            return False
    return True


def _sigmoid(x):
    if x > 8:
        return 1.0
    if x < -8:
        return 0.0
    return 1.0 / (1.0 + math.exp(-x))


def _forward(w, x, hidden=16, outputs=5, inputs=INPUTS):
    """tanh hidden MLP, flat w order: W1 row-maj(hidden x inputs) b1 W2 b2."""
    W1 = np.array(w[: hidden * inputs]).reshape(hidden, inputs)
    b1 = np.array(w[hidden * inputs: hidden * inputs + hidden])
    off = hidden * inputs + hidden
    W2 = np.array(w[off: off + outputs * hidden]).reshape(outputs, hidden)
    b2 = np.array(w[off + outputs * hidden: off + outputs * hidden + outputs])
    h = np.tanh(W1 @ np.array(x, dtype=float) + b1)
    return W2 @ h + b2


def _trait_jitter(net_id):
    h = (net_id * 2654435761) & 0xFFFFFFFF
    h = _lcg(h)
    return ((((h >> 8) & 0x00FFFFFF) / 16777216.0) - 0.5) * 0.06


def _skill_hit_chance(skill, dist, jitter):
    base = 0.34 + 0.15 * skill + jitter
    base = min(0.95, max(0.28, base))
    dscale = max(0.2, 1.0 - dist / 90.0)
    return base * dscale


def record_match(w, seed, n_bots=4, n_zombies=3, max_ticks=1200, bot_skill=3, bot_weapon=0,
                 env_fixed=None):
    """Deterministic match that returns (summary, frames).

    frames: list of dicts, one per recorded tick, with:
      t, env, bots:[{id,x,y,hp,alive,tx,ty,strafe,fire,hit}], zombies:[{id,x,y,hp,alive}],
      events:["shot b0->z2","hit","kill b0@z2 b5",...]
    A frame is recorded once every RECORD_STRIDE ticks so replay stays small.
    """
    rng = seed & 0xFFFFFFFF
    bx = []; by = []; bhp = []; bweapon = []; balive = []; bskill = []
    for i in range(n_bots):
        v, rng = _lcg01(rng); ang = v * 6.283185307179586
        v2, rng = _lcg01(rng); rad = 8.0 + v2 * 18.0
        bx.append(40.0 + math.cos(ang) * rad); by.append(40.0 + math.sin(ang) * rad)
        bhp.append(100.0); bweapon.append((rng >> 8) % 6); bskill.append(float(bot_skill)); balive.append(True)
    zx = []; zy = []; zhp = []; zalive = []
    for i in range(n_zombies):
        v, rng = _lcg01(rng); ang = v * 6.283185307179586
        v2, rng = _lcg01(rng); rad = 12.0 + v2 * 14.0
        zx.append(40.0 + math.cos(ang) * rad); zy.append(40.0 + math.sin(ang) * rad)
        zhp.append(80.0); zalive.append(True)
    kills = 0; deaths = 0; damage_dealt = 0.0; damage_taken = 0.0; shots = 0; hits = 0; total_ticks = 0
    burst_left = [WEAPON_BURST_MIN[bot_weapon]] * n_bots
    burst_cd = [0.0] * n_bots; reaction_cd = [0.0] * n_bots; strafe_dir = [1] * n_bots
    ammo = [WEAPON_MAG[bot_weapon]] * n_bots; reload_cd = [0.0] * n_bots
    last_x = list(bx); last_y = list(by)
    stuck = [0] * n_bots
    env = (seed % 5) if env_fixed is None else env_fixed
    walls = WALLS[env]
    dt = 0.05
    frames = []
    RECORD_STRIDE = 4  # ~12.5 fps at dt=0.05

    # Flat-genome -> layer split hoisted out of the tick loop: _forward
    # re-copied W1/b1/W2/b2 (~325 floats) on every call, once per live bot
    # per tick (~n_bots * max_ticks array rebuilds per match). Same ops and
    # dtypes as _forward (hidden 16, outputs 5), so traces stay bit-identical.
    hid, outs = 16, 5
    W1 = np.array(w[: hid * INPUTS]).reshape(hid, INPUTS)
    b1 = np.array(w[hid * INPUTS: hid * INPUTS + hid])
    _off = hid * INPUTS + hid
    W2 = np.array(w[_off: _off + outs * hid]).reshape(outs, hid)
    b2 = np.array(w[_off + outs * hid: _off + outs * hid + outs])

    def _fwd(x_obs):
        h = np.tanh(W1 @ np.array(x_obs, dtype=float) + b1)
        return W2 @ h + b2


    for tick in range(max_ticks):
        alive_bots = sum(balive)
        alive_z = sum(zalive)
        if alive_bots == 0 or (n_zombies > 0 and alive_z == 0 and alive_bots <= 1):
            if alive_bots == 0:
                break
        total_ticks += 1
        frame = {"t": tick, "env": env, "bots": [], "zombies": [], "events": []}
        for bi in range(n_bots):
            if not balive[bi]:
                continue
            best = -1; best_kind = 0; best_d2 = 1e18; best_d2_true = 1e18
            bx0 = bx[bi]; by0 = by[bi]
            for j in range(n_bots):
                if j == bi or not balive[j]:
                    continue
                d2 = (bx[j] - bx0) ** 2 + (by[j] - by0) ** 2
                if d2 < best_d2:
                    best_d2 = d2; best_d2_true = d2; best = j; best_kind = 0
            for j in range(n_zombies):
                if not zalive[j]:
                    continue
                d2 = (zx[j] - bx0) ** 2 + (zy[j] - by0) ** 2
                # d2_eff only biases target *selection* toward bots; hit chance,
                # range gating and obs must see the true distance (sqrt(1.05)
                # inflation here made zombies ~2.5% farther than they are).
                # Mirrors combat_sim.simulate_match.
                d2_eff = d2 * 1.05
                if d2_eff < best_d2:
                    best_d2 = d2_eff; best_d2_true = d2; best = j; best_kind = 1
            if best < 0:
                v, rng = _lcg01(rng); ang = v * 6.283185307179586
                bx[bi] += math.cos(ang) * 0.4; by[bi] += math.sin(ang) * 0.4
                if tick % RECORD_STRIDE == 0:
                    frame["bots"].append({"id": bi, "x": bx0, "y": by0, "hp": bhp[bi], "alive": True, "tx": 0, "ty": 0, "strafe": 0, "fire": False, "w": bweapon[bi]})
                continue
            if best_kind == 0:
                tx = bx[best]; ty = by[best]; thp = bhp[best]
            else:
                tx = zx[best]; ty = zy[best]; thp = zhp[best]
            dist = math.sqrt(best_d2_true)
            can_see = _los(bx0, by0, tx, ty, walls)
            x_obs = [
                bhp[bi] / 100.0, thp / 100.0, min(1.0, dist / 70.0), 1.0 if can_see else 0.0,
                0.0, WEAPON_RANGE[bweapon[bi]] / 45.0, float(WEAPON_PELLETS[bweapon[bi]]) / 8.0,
                0.55 + bskill[bi] * 0.10, 0.55 + bskill[bi] * 0.10, 0.6, 0.5, 0.2, 0.0,
                min(1.0, float(stuck[bi]) / 40.0),
            ]
            y = _fwd(x_obs)
            camp = _sigmoid(y[0]); retreat = _sigmoid(y[1]); aim_raw = math.tanh(y[2])
            fire_gate = _sigmoid(y[3]); strafe_sig = _sigmoid(y[4])
            sdir = 1 if strafe_sig > 0.5 else -1
            is_retreating = (retreat > 0.5 and bhp[bi] < 42.0) or (bhp[bi] < 20.0)
            if camp > 0.5 and bhp[bi] > 55 and dist > 18:
                bx[bi] += (1 if strafe_dir[bi] > 0 else -1) * 0.15
            elif is_retreating:
                dx = bx0 - tx; dy = by0 - ty; d = max(0.001, math.sqrt(dx * dx + dy * dy))
                bx[bi] += (dx / d) * 1.1 + (-dy / d) * sdir * 0.6
                by[bi] += (dy / d) * 1.1 + (dx / d) * sdir * 0.6
                strafe_dir[bi] = sdir
            else:
                if dist < 6.0:
                    dx = tx - bx0; dy = ty - by0; d = max(0.001, math.sqrt(dx * dx + dy * dy))
                    px = -dy / d; py = dx / d
                    bx[bi] += (-dx / d) * 0.45 + px * sdir * 0.55
                    by[bi] += (-dy / d) * 0.45 + py * sdir * 0.55
                elif dist < WEAPON_RANGE[bweapon[bi]] and can_see:
                    dx = tx - bx0; dy = ty - by0; d = max(0.001, math.sqrt(dx * dx + dy * dy))
                    px = -dy / d; py = dx / d
                    bx[bi] += dx / d * 0.22 + px * sdir * 0.78
                    by[bi] += dy / d * 0.22 + py * sdir * 0.78
                else:
                    dx = tx - bx0; dy = ty - by0; d = max(0.001, math.sqrt(dx * dx + dy * dy))
                    bx[bi] += dx / d * 1.2
                    by[bi] += dy / d * 1.2
                strafe_dir[bi] = sdir
            bx[bi] = min(78, max(2, bx[bi])); by[bi] = min(78, max(2, by[bi]))
            if abs(bx[bi] - last_x[bi]) < 0.18 and abs(by[bi] - last_y[bi]) < 0.18:
                stuck[bi] += 1
            else:
                last_x[bi] = bx[bi]; last_y[bi] = by[bi]
            if reaction_cd[bi] > 0:
                reaction_cd[bi] -= dt
            if burst_cd[bi] > 0:
                burst_cd[bi] -= dt
            if reload_cd[bi] > 0:
                reload_cd[bi] -= dt
            fired = False; hit_tgt = None; killed_tgt = None
            if reload_cd[bi] > 0:
                pass
            elif ammo[bi] <= 0:
                ammo[bi] = WEAPON_MAG[bweapon[bi]]; reload_cd[bi] = WEAPON_RELOAD[bweapon[bi]]
            elif not can_see:
                pass
            elif dist > WEAPON_RANGE[bweapon[bi]] + 2.0:
                pass
            elif is_retreating:
                pass
            elif fire_gate < 0.5:
                pass
            elif reaction_cd[bi] > 0 or burst_cd[bi] > 0:
                pass
            else:
                shots += 1; ammo[bi] -= 1
                tj = _trait_jitter(1000 + bi)
                hc = _skill_hit_chance(bskill[bi], dist, tj)
                aim_penalty = abs(aim_raw) * (1.0 - bskill[bi] * 0.15)
                hc2 = hc * (1.0 - aim_penalty * 0.35)
                v, rng = _lcg01(rng)
                fired = True
                if v > hc2:
                    burst_left[bi] -= 1
                    if burst_left[bi] <= 0:
                        burst_left[bi] = WEAPON_BURST_MIN[bweapon[bi]]; burst_cd[bi] = 0.55
                    else:
                        reaction_cd[bi] = 0.28
                else:
                    hits += 1
                    is_head = False
                    if WEAPON_PELLETS[bweapon[bi]] == 1:
                        v2, rng = _lcg01(rng)
                        if v2 < (0.04 + bskill[bi] * 0.02):
                            is_head = True
                    dmg = WEAPON_DAMAGE[bweapon[bi]]
                    if is_head:
                        dmg *= 2.0
                    damage_dealt += dmg
                    hit_tgt = (best_kind, best)
                    if best_kind == 0:
                        bhp[best] -= dmg
                        if bhp[best] <= 0:
                            balive[best] = False; deaths += 1; kills += 1
                            killed_tgt = ("bot" + str(best))
                    else:
                        zhp[best] -= dmg
                        if zhp[best] <= 0:
                            zalive[best] = False; kills += 1
                            killed_tgt = ("zombie" + str(best))
                    if burst_left[bi] > 0:
                        burst_left[bi] -= 1
                        if burst_left[bi] <= 0:
                            burst_left[bi] = WEAPON_BURST_MIN[bweapon[bi]]; burst_cd[bi] = 0.55
                    else:
                        reaction_cd[bi] = 0.28
            if tick % RECORD_STRIDE == 0:
                frame["bots"].append({
                    "id": bi, "x": bx[bi], "y": by[bi], "hp": bhp[bi], "alive": balive[bi],
                    "tx": tx, "ty": ty, "strafe": sdir, "fire": fired, "w": bweapon[bi],
                })
            if fired and (hit_tgt is not None or killed_tgt is not None):
                kind = "zombie" if best_kind == 1 else "bot"
                frame["events"].append("shot b%d @%s%d %s" % (bi, kind, best, "KILL" if killed_tgt else ""))
            elif fired:
                frame["events"].append("shot b%d (miss)" % bi)
        for zi in range(n_zombies):
            if not zalive[zi]:
                continue
            best_b = -1; best_d2 = 1e18
            for bi in range(n_bots):
                if not balive[bi]:
                    continue
                d2 = (bx[bi] - zx[zi]) ** 2 + (by[bi] - zy[zi]) ** 2
                if d2 < best_d2:
                    best_d2 = d2; best_b = bi
            if best_b < 0:
                continue
            dx = bx[best_b] - zx[zi]; dy = by[best_b] - zy[zi]; d = math.sqrt(best_d2)
            if d > 0.01:
                zx[zi] += dx / d * 0.42; zy[zi] += dy / d * 0.42
            if d < 2.0:
                bhp[best_b] -= 10 * dt * 8
                damage_taken += 10 * dt * 8
                if bhp[best_b] <= 0:
                    balive[best_b] = False; deaths += 1
                    frame["events"].append("zombie%d ate b%d" % (zi, best_b))
            if tick % RECORD_STRIDE == 0:
                frame["zombies"].append({"id": zi, "x": zx[zi], "y": zy[zi], "hp": zhp[zi], "alive": zalive[zi]})
        if tick % RECORD_STRIDE == 0:
            frames.append(frame)

    summary = {"kills": kills, "deaths": deaths, "damage_dealt": damage_dealt,
               "damage_taken": damage_taken, "shots": shots, "hits": hits,
               "total_ticks": total_ticks,
               # Alive bots only: dead bots keep their residual hp (<= 0), and
               # balive is always non-empty so a plain truthiness guard is dead code.
               "winner_hp": max((h for h, a in zip(bhp, balive) if a), default=0)}
    return summary, frames


def _round_floats(o, nd=2):
    """Trim float precision for serialization only: world units are 0..80 on an
    8 px/unit canvas, so 2 decimals are far below one pixel. Full-precision
    repr doubles or triples the embedded JSON otherwise."""
    if isinstance(o, float):
        return round(o, nd)
    if isinstance(o, dict):
        return {k: _round_floats(v, nd) for k, v in o.items()}
    if isinstance(o, list):
        return [_round_floats(v, nd) for v in o]
    return o


def render_html(summary, frames, walls, out: Path, title="GA Arena Replay"):
    """Self-contained HTML with a <canvas> top-down replay (play/pause/scrub)."""
    world = 80  # arena 0..80 units -> pixels
    scale = 8.0
    W = int(world * scale); H = int(world * scale)
    js_frames = json.dumps(_round_floats(frames))
    js_walls = json.dumps(walls)
    # Build with token substitution (not an f-string) so the embedded JS/CSS braces are literal.
    html = """<!doctype html><html lang="en"><head><meta charset="utf-8">
<title>@TITLE@</title>
<style>
 body{font-family:ui-sans-serif,system-ui,Segoe UI,Roboto,Arial;background:#0b1220;color:#e2e8f0;margin:0}
 .wrap{max-width:980px;margin:20px auto;padding:0 16px}
 h1{font-size:20px}
 .sum{display:flex;gap:18px;flex-wrap:wrap;margin:12px 0}
 .sum div{background:#1e293b;padding:8px 14px;border-radius:8px;font-size:13px}
 .sum b{color:#38bdf8}
 canvas{background:#0f172a;border:1px solid #334155;border-radius:8px;width:100%}
 .ctl{display:flex;gap:10px;align-items:center;margin:10px 0;flex-wrap:wrap}
 button{background:#0369a1;color:#fff;border:0;border-radius:6px;padding:7px 14px;cursor:pointer;font-size:13px}
 button:hover{background:#075985}
 input[type=range]{flex:1;min-width:180px}
 .legend{display:flex;gap:16px;font-size:12px;margin:8px 0}
 .dot{display:inline-block;width:10px;height:10px;border-radius:50%;margin-right:4px}
 #log{background:#111827;padding:8px 12px;border-radius:6px;font-family:monospace;font-size:12px;max-height:80px;overflow:auto;margin-top:8px}
</style></head><body><div class="wrap">
<h1>@TITLE@</h1>
<div class="sum">
 <div>Kills <b>@KILLS@</b></div>
 <div>Deaths <b>@DEATHS@</b></div>
 <div>Shots <b>@SHOTS@</b></div>
 <div>Hits <b>@HITS@</b></div>
 <div>Ticks <b>@TICKS@</b></div>
</div>
<div class="legend">
 <span><span class="dot" style="background:#f87171" aria-hidden="true"></span>Bots (tag=weapon)</span>
 <span><span class="dot" style="background:#34d399" aria-hidden="true"></span>Zombies</span>
 <span><span class="dot" style="background:#fde047" aria-hidden="true"></span>Shots</span>
 <span style="color:#94a3b8">Weapon tags: Pistol P · Shotgun S · AK AK · Sniper Sn · AutoShotgun Au · SMG SM</span>
 <span style="color:#94a3b8">Walls block LOS (aim around them)</span>
</div>
<div class="ctl">
 <button onclick="play()">&#9654; Play</button>
 <button onclick="pause()">&#9208; Pause</button>
 <button onclick="reset()" aria-label="Reset to first frame">&#9198;</button>
 <span style="font-size:12px" id="frame">0/0</span>
 <input type="range" id="scrub" min="0" max="0" value="0" aria-label="Jump to frame" oninput="goto(this.value)">
</div>
<canvas id="c" width="@W@" height="@H@" role="img" aria-label="Top-down arena replay animation: bot circles versus zombie circles on the training map">Top-down arena replay animation (canvas unsupported).</canvas>
<div id="log"></div>
</div>
<script>
const S = @SCALE@;
const F = @FRAMES@;
const WALLS = @WALLS@;
const c = document.getElementById('c');
const ctx = c.getContext('2d');
const scrub = document.getElementById('scrub');
const frameLbl = document.getElementById('frame');
scrub.max = Math.max(0, F.length-1); scrub.value = 0;
let fi = 0, playing = true;
function draw(fr){
  ctx.clearRect(0,0,c.width,c.height);
  ctx.strokeStyle='#334155'; ctx.lineWidth=2; ctx.strokeRect(2,2,c.width-4,c.height-4);
  ctx.strokeStyle='#64748b'; ctx.lineWidth=7; ctx.lineCap='round';
  for(const w of WALLS){
    ctx.beginPath();
    ctx.moveTo(w[0]*S,(80-w[1])*S); ctx.lineTo(w[2]*S,(80-w[3])*S);
    ctx.stroke();
  }
  const WCOL=['#f87171','#c084fc','#fb923c','#38bdf8','#f472b6','#a3e635']; // pistol,shotgun,ak,sniper,auto,smg
  const WTAG=['P','S','AK','Sn','Au','SM'];
  for(const b of fr.bots){
    if(!b.alive){ continue; }
    if(b.fire){ ctx.strokeStyle='rgba(253,224,71,0.85)'; ctx.lineWidth=3; ctx.beginPath(); ctx.moveTo(b.x*S,(80-b.y)*S); ctx.lineTo(b.tx*S,(80-b.ty)*S); ctx.stroke(); }
    else if(b.tx||b.ty){ ctx.strokeStyle='rgba(56,189,248,0.22)'; ctx.lineWidth=1; ctx.beginPath(); ctx.moveTo(b.x*S,(80-b.y)*S); ctx.lineTo(b.tx*S,(80-b.ty)*S); ctx.stroke(); }
    // weapon ring: unique color per loadout, tag letter inside
    const wc=WCOL[(b.w||0)%6];
    ctx.fillStyle=wc; ctx.beginPath(); ctx.arc(b.x*S,(80-b.y)*S,8,0,7); ctx.fill();
    ctx.strokeStyle='#0f172a'; ctx.lineWidth=2; ctx.stroke();
    ctx.fillStyle='#0f172a'; ctx.font='9px monospace'; ctx.textAlign='center'; ctx.textBaseline='middle';
    ctx.fillText(WTAG[(b.w||0)%6], b.x*S, (80-b.y)*S);
    ctx.fillStyle='rgba(15,23,42,0.85)'; ctx.fillRect(b.x*S-11,(80-b.y)*S-18,22,5);
    ctx.fillStyle= b.hp>50?'#22c55e': (b.hp>25?'#eab308':'#ef4444');
    ctx.fillRect(b.x*S-11,(80-b.y)*S-18,22*Math.max(0,b.hp/100),5);
  }
  for(const z of fr.zombies){
    if(!z.alive) continue;
    ctx.fillStyle='#34d399'; ctx.beginPath(); ctx.arc(z.x*S,(80-z.y)*S,7,0,7); ctx.fill();
    ctx.strokeStyle='#166534'; ctx.stroke();
  }
  frameLbl.textContent = fi+'/'+(F.length-1);
  scrub.value = fi;
}
function step(){ if(playing && fi < F.length-1){ fi++; draw(F[fi]); const e=F[fi].events.join(' &middot; '); if(e){ document.getElementById('log').textContent='t'+F[fi].t+': '+e; } } }
function play(){ playing=true; }
function pause(){ playing=false; }
function reset(){ fi=0; draw(F[0]); document.getElementById('log').textContent=''; }
function goto(i){ fi=+i; draw(F[fi]); }
// Respect the OS reduce-motion setting by loading paused; Play still works.
if (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) { playing = false; }
setInterval(step, 80);
draw(F[0]);
</script></body></html>"""
    html = (html
            .replace("@TITLE@", str(title))
            .replace("@KILLS@", str(summary["kills"]))
            .replace("@DEATHS@", str(summary["deaths"]))
            .replace("@SHOTS@", str(summary["shots"]))
            .replace("@HITS@", str(summary["hits"]))
            .replace("@TICKS@", str(summary["total_ticks"]))
            .replace("@SCALE@", str(scale))
            .replace("@W@", str(W))
            .replace("@H@", str(H))
            .replace("@FRAMES@", js_frames)
            .replace("@WALLS@", js_walls))
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(html, encoding="utf-8")
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--best", default="evolved/best.json")
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--n-bots", type=int, default=4)
    ap.add_argument("--n-zombies", type=int, default=3)
    ap.add_argument("--max-ticks", type=int, default=1200)
    ap.add_argument("--skill", type=int, default=3)
    ap.add_argument("--weapon", type=int, default=0)
    ap.add_argument("--env", type=int, default=None)
    ap.add_argument("--out", default="docs/ga-replay.html")
    ap.add_argument("--verify", action="store_true", help="compare summary to numba eval on same seed")
    args = ap.parse_args()
    best_path = Path(args.best)
    if not best_path.is_file():
        raise SystemExit(f"--best not found: {best_path} (e.g. evolved/best.json)")
    w = np.array(json.loads(best_path.read_text(encoding="utf-8"))["weights"], dtype=float)
    summary, frames = record_match(w, args.seed, args.n_bots, args.n_zombies, args.max_ticks,
                                   args.skill, args.weapon, args.env)
    walls = WALLS[args.env if args.env is not None else args.seed % 5]
    out = render_html(summary, frames, walls, Path(args.out), f"GA Arena Replay — seed {args.seed}")
    print(f"replay -> {out}  ({len(frames)} frames, kills={summary['kills']}, shots={summary['shots']})")
    if args.verify:
        import harness
        harness.ACTIVATION = 0
        fit = harness.evaluate(w, 999, args.seed % 1000, args.seed)  # scalar check
        print(f"numba harness fitness on seed {args.seed}: {fit:.3f} (replay mirror summary above)")


if __name__ == "__main__":
    main()
