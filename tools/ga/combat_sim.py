"""combat_sim.py — realistic PvP + zombie combat simulator (numba, headless).

No 7DTD binary needed. Re-uses weapon profiles, LOS, traits, and the
BotNeuralBrain 14→16→5 contract verbatim so evolved weights drop straight
into the live mod. Deterministic LCG seeds; every match replays.

Matches docs/research/02 (arenas) and 01 (obs vector).
"""

from __future__ import annotations

import math
import numpy as np
import numba

# Weapon table mirrors Source/BotMod/Config/BotConfig.cs WeaponProfile.ForGun
# idx: 0 pistol, 1 shotgun, 2 AK, 3 sniper, 4 auto-shotgun, 5 SMG
WEAPON_DAMAGE = np.array([16, 14, 16, 42, 9, 9], dtype=np.float32)
WEAPON_RANGE = np.array([40, 22, 55, 90, 22, 35], dtype=np.float32)
WEAPON_SPREAD = np.array([1.6, 9.0, 1.4, 0.35, 6.0, 2.2], dtype=np.float32)
WEAPON_PELLETS = np.array([1, 8, 1, 1, 6, 1], dtype=np.int32)
WEAPON_FIRE_RATE = np.array([0.28, 0.55, 0.11, 0.90, 0.22, 0.09], dtype=np.float32)
WEAPON_BURST_MIN = np.array([1, 1, 3, 1, 1, 5], dtype=np.int32)
WEAPON_BURST_MAX = np.array([3, 1, 6, 1, 1, 9], dtype=np.int32)
WEAPON_MAG = np.array([12, 6, 30, 5, 6, 32], dtype=np.int32)
WEAPON_RELOAD = np.array([1.2, 2.6, 2.0, 2.5, 2.6, 1.8], dtype=np.float32)

# Fire-cost tuning (task shape: finite ammo + sustained-fire spread so a
# no-brain that always fires burns rounds and bleeds accuracy; pacing wins).
AMMO_RESERVE_MULT = 1                # spare-round pool = mag x this; reload consumes one mag from it
SPREAD_ADD_PER_SHOT = 0.25           # spread added per shot fired
SPREAD_DECAY_PER_SEC = 1.0           # spread recovered per second of pause
SPREAD_HIT_PENALTY = 0.75            # hit-chance loss at full spread (1.0)

# Flat params — HIDDEN is the canonical clanker size (16). For sweeps we keep
# these as *runtime* values via _HIDDEN/_W1_LEN etc, but the default layout
# that ships in BotNeuralBrain.cs is still 14->16->5 (W=325).
INPUTS = 14
HIDDEN = 16
OUTPUTS = 5
W1_LEN = HIDDEN * INPUTS
B1_LEN = HIDDEN
W2_LEN = OUTPUTS * HIDDEN
W_ALL = W1_LEN + B1_LEN + W2_LEN + 5
# dispatch table for activation (set by harness/sweep). Keep canonical tanh.
# We keep _ACTIVATION at 0 but harness picks the njit via explicit call.
_WALL_SET = 0
_LAYOUT_TAG = "H16-tanh"


@numba.njit
def _lcg(s: int) -> int:
    return (s * 1103515245 + 12345) & 0xFFFFFFFF


@numba.njit
def _lcg01(s: int):
    s = _lcg(s)
    v = ((s >> 8) & 0x00FFFFFF) / 16777216.0
    return v, s


@numba.njit
def _forward_hidden(w, x, h):
    # w flat, x[14], h[16] out
    off = 0
    for hi in range(HIDDEN):
        s = w[off + HIDDEN * INPUTS + hi]  # b1[hi] at offset W1_LEN+hi  (careful: w is flat so b1 starts at W1_LEN)
        # actually recompute: layout is W1[0:W1_LEN], b1[W1_LEN:W1_LEN+B1_LEN], W2, b2
        # so do explicit
        s = w[W1_LEN + hi]
        base = hi * INPUTS
        for i in range(INPUTS):
            s += w[base + i] * x[i]
        # tanh
        # numba lacks math.tanh for float32? use np.tanh
        h[hi] = math.tanh(s)
    # we ignore second layer here; caller does it


@numba.njit
def forward_numba(w, x, y):
    # canonical tanh — what ships (BotNeuralBrain.cs). Sweep relu uses forward_numba_relu.
    h = np.empty(HIDDEN, dtype=numba.float32)
    for hi in range(HIDDEN):
        s = w[W1_LEN + hi]
        base = hi * INPUTS
        for i in range(INPUTS):
            s += w[base + i] * x[i]
        h[hi] = math.tanh(s)
    base2 = W1_LEN + B1_LEN
    for o in range(OUTPUTS):
        s = w[base2 + W2_LEN + o]
        row = base2 + o * HIDDEN
        for hi in range(HIDDEN):
            s += w[row + hi] * h[hi]
        y[o] = s


@numba.njit
def forward_numba_relu(w, x, y):
    h = np.empty(HIDDEN, dtype=numba.float32)
    for hi in range(HIDDEN):
        s = w[W1_LEN + hi]
        base = hi * INPUTS
        for i in range(INPUTS):
            s += w[base + i] * x[i]
        h[hi] = s if s > 0 else 0.0
    base2 = W1_LEN + B1_LEN
    for o in range(OUTPUTS):
        s = w[base2 + W2_LEN + o]
        row = base2 + o * HIDDEN
        for hi in range(HIDDEN):
            s += w[row + hi] * h[hi]
        y[o] = s


@numba.njit
def sigmoid(x):
    if x > 8.0:
        return 1.0
    if x < -8.0:
        return 0.0
    return 1.0 / (1.0 + math.exp(-x))


@numba.njit
def skill_hit_chance(skill, dist, trait_jitter):
    base = 0.34 + 0.15 * skill + trait_jitter
    if base > 0.95:
        base = 0.95
    if base < 0.28:
        base = 0.28
    dscale = 1.0 - dist / 90.0
    if dscale < 0.2:
        dscale = 0.2
    return base * dscale


# World geometry — env variants (picked per-match by harness for diversity).
# 0: L + block (default), 1: cross (4 walls), 2: open (no walls), 3: corridor.
WALLS = np.array([
    [20.0, 20.0, 20.0, 60.0],
    [20.0, 40.0, 60.0, 40.0],
    [50.0, 10.0, 50.0, 30.0],
], dtype=np.float32)
WALLS_CROSS = np.array([
    [40.0, 10.0, 40.0, 70.0],
    [10.0, 40.0, 70.0, 40.0],
    [15.0, 15.0, 65.0, 65.0],
    [65.0, 15.0, 15.0, 65.0],
], dtype=np.float32)
WALLS_OPEN = np.zeros((0, 4), dtype=np.float32)
WALLS_CORRIDOR = np.array([
    [40.0, 0.0, 40.0, 30.0],
    [40.0, 50.0, 40.0, 80.0],
    [0.0, 40.0, 30.0, 40.0],
    [50.0, 40.0, 80.0, 40.0],
], dtype=np.float32)
# Dense choke maze — 5 shorter walls split the field into corridors so bots must
# turn to keep LOS / not stand in an open field. Hardest layout (env index 4).
WALLS_MAZE = np.array([
    [30.0, 20.0, 50.0, 20.0],
    [50.0, 20.0, 50.0, 45.0],
    [30.0, 55.0, 55.0, 55.0],
    [20.0, 35.0, 20.0, 60.0],
    [60.0, 35.0, 60.0, 65.0],
    [45.0, 60.0, 68.0, 60.0],
], dtype=np.float32)
_WALL_TABLE = [WALLS, WALLS_CROSS, WALLS_OPEN, WALLS_CORRIDOR, WALLS_MAZE]


@numba.njit
def seg_intersect(ax, ay, bx, by, cx, cy, dx, dy):
    # lines AB and CD intersect? orientation test
    def orient(px, py, qx, qy, rx, ry):
        v = (qy - py) * (rx - qx) - (qx - px) * (ry - qy)
        if abs(v) < 1e-6:
            return 0
        return 1 if v > 0 else 2
    def on_seg(px, py, qx, qy, rx, ry):
        return (min(px, rx) - 1e-6 <= qx <= max(px, rx) + 1e-6 and
                min(py, ry) - 1e-6 <= qy <= max(py, ry) + 1e-6)
    o1 = orient(ax, ay, bx, by, cx, cy)
    o2 = orient(ax, ay, bx, by, dx, dy)
    o3 = orient(cx, cy, dx, dy, ax, ay)
    o4 = orient(cx, cy, dx, dy, bx, by)
    if o1 != o2 and o3 != o4:
        return True
    if o1 == 0 and on_seg(ax, ay, cx, cy, bx, by):
        return True
    if o2 == 0 and on_seg(ax, ay, dx, dy, bx, by):
        return True
    if o3 == 0 and on_seg(cx, cy, ax, ay, dx, dy):
        return True
    if o4 == 0 and on_seg(cx, cy, bx, by, dx, dy):
        return True
    return False


@numba.njit
def los_clear(ax, ay, bx, by, walls, n_walls):
    for i in range(n_walls):
        if seg_intersect(ax, ay, bx, by, walls[i, 0], walls[i, 1], walls[i, 2], walls[i, 3]):
            return False
    return True


@numba.njit
def trait_jitter(net_id):
    h = (net_id * 2654435761) & 0xFFFFFFFF
    h = _lcg(h)
    v = ((h >> 8) & 0x00FFFFFF) / 16777216.0
    return (v - 0.5) * 0.06


@numba.njit
def simulate_match_relu(w, seed, n_bots, n_zombies, max_ticks, bot_skill, bot_weapon, w_opp=None, n_evolved=-1):
    # relu variant of the full tick loop (9792 bytes duplicated to keep njit simple)
    bx = np.empty(16, dtype=numba.float32)
    by = np.empty(16, dtype=numba.float32)
    bhp = np.empty(16, dtype=numba.float32)
    bweapon = np.empty(16, dtype=numba.int64)
    bskill = np.empty(16, dtype=numba.float32)
    balive = np.empty(16, dtype=numba.boolean)
    bvelx = np.empty(16, dtype=numba.float32)
    bvely = np.empty(16, dtype=numba.float32)
    zx = np.empty(16, dtype=numba.float32)
    zy = np.empty(16, dtype=numba.float32)
    zhp = np.empty(16, dtype=numba.float32)
    zalive = np.empty(16, dtype=numba.boolean)
    rng = seed & 0xFFFFFFFF
    for i in range(n_bots):
        v, rng = _lcg01(rng)
        ang = v * 6.283185307179586
        v2, rng = _lcg01(rng)
        rad = 8.0 + v2 * 18.0
        bx[i] = 40.0 + math.cos(ang) * rad
        by[i] = 40.0 + math.sin(ang) * rad
        bhp[i] = 100.0; bweapon[i] = (rng >> 8) % 6; bskill[i] = float(bot_skill); balive[i] = True; bvelx[i] = 0.0; bvely[i] = 0.0
    for i in range(n_zombies):
        v, rng = _lcg01(rng)
        ang = v * 6.283185307179586
        v2, rng = _lcg01(rng)
        rad = 12.0 + v2 * 14.0
        zx[i] = 40.0 + math.cos(ang) * rad
        zy[i] = 40.0 + math.sin(ang) * rad
        zhp[i] = 80.0; zalive[i] = True
    kills = 0; deaths = 0; damage_dealt = 0.0; damage_taken = 0.0; shots = 0; hits = 0; stuck_ticks = 0; camp_ticks = 0; total_ticks = 0
    burst_left = np.empty(16, dtype=numba.int64); burst_cd = np.empty(16, dtype=numba.float32); reaction_cd = np.empty(16, dtype=numba.float32); strafe_dir = np.empty(16, dtype=numba.int64)
    ammo = np.empty(16, dtype=numba.int64); reload_cd = np.empty(16, dtype=numba.float32)
    reserve = np.empty(16, dtype=numba.int64)  # finite per-match rounds; reload drains it (spams run dry)
    spread = np.empty(16, dtype=numba.float32)
    for i in range(n_bots):
        burst_left[i] = WEAPON_BURST_MIN[bweapon[i]]; burst_cd[i] = 0.0; reaction_cd[i] = 0.0
        v, rng = _lcg01(rng); strafe_dir[i] = 1 if v > 0.5 else -1
        ammo[i] = WEAPON_MAG[bweapon[i]]; reserve[i] = WEAPON_MAG[bweapon[i]] * AMMO_RESERVE_MULT; spread[i] = 0.0; reload_cd[i] = 0.0
    last_x = np.empty(16, dtype=numba.float32); last_y = np.empty(16, dtype=numba.float32)
    for i in range(n_bots):
        last_x[i] = bx[i]; last_y[i] = by[i]
    stuck = np.zeros(16, dtype=numba.int64)
    dt = 0.05
    env = seed % 5
    y_raw = np.empty(5, dtype=numba.float32); x_obs = np.empty(INPUTS, dtype=numba.float32)
    use_opp = w_opp is not None
    if use_opp and n_evolved < 0:
        n_evolved = n_bots
    kills_ev = 0; deaths_ev = 0; damage_dealt_ev = 0.0; damage_taken_ev = 0.0; shots_ev = 0; hits_ev = 0
    for tick in range(max_ticks):
        alive_bots = 0
        for i in range(n_bots):
            if balive[i]: alive_bots += 1
        alive_z = 0
        for i in range(n_zombies):
            if zalive[i]: alive_z += 1
        if alive_bots == 0 or (n_zombies > 0 and alive_z == 0 and alive_bots <= 1):
            if alive_bots == 0: break
        total_ticks += 1
        for bi in range(n_bots):
            if not balive[bi]: continue
            best = -1; best_kind = 0; best_d2 = 1e9; bx0 = bx[bi]; by0 = by[bi]
            for j in range(n_bots):
                if j == bi or not balive[j]: continue
                d2 = (bx[j] - bx0) ** 2 + (by[j] - by0) ** 2
                if d2 < best_d2: best_d2 = d2; best = j; best_kind = 0
            for j in range(n_zombies):
                if not zalive[j]: continue
                d2 = (zx[j] - bx0) ** 2 + (zy[j] - by0) ** 2
                d2_eff = d2 * 1.05
                if d2_eff < best_d2: best_d2 = d2_eff; best = j; best_kind = 1
            if best < 0:
                v, rng = _lcg01(rng); ang = v * 6.283185307179586
                bx[bi] += math.cos(ang) * 0.4; by[bi] += math.sin(ang) * 0.4
                continue
            if best_kind == 0: tx = bx[best]; ty = by[best]; thp = bhp[best]
            else: tx = zx[best]; ty = zy[best]; thp = zhp[best]
            dist = math.sqrt(best_d2)
            if env == 0: can_see = los_clear(bx0, by0, tx, ty, WALLS, 3)
            elif env == 1: can_see = los_clear(bx0, by0, tx, ty, WALLS_CROSS, 4)
            elif env == 2: can_see = True
            elif env == 3: can_see = los_clear(bx0, by0, tx, ty, WALLS_CORRIDOR, 4)
            else: can_see = los_clear(bx0, by0, tx, ty, WALLS_MAZE, 6)
            x_obs[0] = bhp[bi] / 100.0; x_obs[1] = thp / 100.0; x_obs[2] = min(1.0, dist / 70.0); x_obs[3] = 1.0 if can_see else 0.0; x_obs[4] = spread[bi]; x_obs[5] = WEAPON_RANGE[bweapon[bi]] / 45.0; x_obs[6] = float(WEAPON_PELLETS[bweapon[bi]]) / 8.0; x_obs[7] = 0.55 + bskill[bi] * 0.10; x_obs[8] = 0.55 + bskill[bi] * 0.10; x_obs[9] = 0.6; x_obs[10] = 0.5; x_obs[11] = 0.2; x_obs[12] = min(1.0, (ammo[bi] + reserve[bi]) / (WEAPON_MAG[bweapon[bi]] * (1.0 + AMMO_RESERVE_MULT))); x_obs[13] = min(1.0, float(stuck[bi]) / 40.0)
            # forward
            if use_opp and bi >= n_evolved:
                forward_numba_relu(w_opp, x_obs, y_raw)
            else:
                forward_numba_relu(w, x_obs, y_raw)
            camp = sigmoid(y_raw[0]); retreat = sigmoid(y_raw[1]); aim_raw = math.tanh(y_raw[2]); fire_gate = sigmoid(y_raw[3]); strafe_sig = sigmoid(y_raw[4]); sdir = 1 if strafe_sig > 0.5 else -1
            # movement — policy-driven (deeper sim rework): net controls a 2D
            # velocity via retreat (forward), strafe_sig (lateral), camp (hold)
            fwd = 1.2 * (1.0 - 2.0 * retreat)
            lat = (strafe_sig - 0.5) * 2.4
            if camp > 0.5 and bhp[bi] > 55 and dist > 18:
                camp_ticks += 1
                fwd *= 0.15
            dx = tx - bx0; dy = ty - by0; d = max(0.001, math.sqrt(dx*dx + dy*dy)); px = -dy / d; py = dx / d
            vx = fwd * (dx / d) + lat * px; vy = fwd * (dy / d) + lat * py
            vmag = math.sqrt(vx*vx + vy*vy)
            if vmag > 1.3:
                vx *= 1.3 / vmag; vy *= 1.3 / vmag
            bx[bi] += vx; by[bi] += vy
            strafe_dir[bi] = 1 if strafe_sig > 0.5 else -1
            is_retreating = retreat > 0.5 and bhp[bi] < 42.0
            if bx[bi] < 2: bx[bi] = 2
            if bx[bi] > 78: bx[bi] = 78
            if by[bi] < 2: by[bi] = 2
            if by[bi] > 78: by[bi] = 78
            if abs(bx[bi] - last_x[bi]) < 0.18 and abs(by[bi] - last_y[bi]) < 0.18:
                stuck[bi] += 1
                if stuck[bi] > 0: stuck_ticks += 1
            else: stuck[bi] = 0; last_x[bi] = bx[bi]; last_y[bi] = by[bi]
            if spread[bi] > 0.0: spread[bi] = max(0.0, spread[bi] - SPREAD_DECAY_PER_SEC * dt)
            if reaction_cd[bi] > 0: reaction_cd[bi] -= dt
            if burst_cd[bi] > 0: burst_cd[bi] -= dt
            if reload_cd[bi] > 0: reload_cd[bi] -= dt
            if reload_cd[bi] > 0: continue
            if ammo[bi] <= 0:
                if reserve[bi] > 0:
                    reserve[bi] -= WEAPON_MAG[bweapon[bi]]
                    ammo[bi] = WEAPON_MAG[bweapon[bi]]; reload_cd[bi] = WEAPON_RELOAD[bweapon[bi]]
                continue  # reserve dry: out of rounds for the rest of the match
            if not can_see: continue
            if dist > WEAPON_RANGE[bweapon[bi]] + 2.0: continue
            if is_retreating: continue
            if fire_gate < 0.5: continue
            if reaction_cd[bi] > 0 or burst_cd[bi] > 0: continue
            shots += 1
            if not use_opp or bi < n_evolved: shots_ev += 1
            ammo[bi] -= 1; spread[bi] = min(1.0, spread[bi] + SPREAD_ADD_PER_SHOT); tj = trait_jitter(1000 + bi); hc = skill_hit_chance(bskill[bi], dist, tj); aim_penalty = abs(aim_raw) * (1.0 - bskill[bi] * 0.15); hc2 = hc * (1.0 - aim_penalty * 0.35) * (1.0 - spread[bi] * SPREAD_HIT_PENALTY)
            v, rng = _lcg01(rng)
            if v > hc2:
                if burst_left[bi] > 0:
                    burst_left[bi] -= 1
                    if burst_left[bi] <= 0: burst_left[bi] = WEAPON_BURST_MIN[bweapon[bi]]; burst_cd[bi] = 0.55
                else: reaction_cd[bi] = 0.28
                continue
            hits += 1
            if not use_opp or bi < n_evolved: hits_ev += 1
            is_head = False
            if WEAPON_PELLETS[bweapon[bi]] == 1:
                v2, rng = _lcg01(rng)
                if v2 < (0.04 + bskill[bi] * 0.02): is_head = True
            dmg = WEAPON_DAMAGE[bweapon[bi]]
            if is_head: dmg = dmg * 2.0
            damage_dealt += dmg
            if not use_opp or bi < n_evolved:
                damage_dealt_ev += dmg
            if best_kind == 0:
                bhp[best] -= dmg
                if not use_opp or best < n_evolved:
                    damage_taken_ev += dmg
                if bhp[best] <= 0:
                    balive[best] = False; deaths += 1; kills += 1
                    if not use_opp or bi < n_evolved:
                        kills_ev += 1
                    if not use_opp or best < n_evolved:
                        deaths_ev += 1
            else:
                zhp[best] -= dmg
                if zhp[best] <= 0:
                    zalive[best] = False; kills += 1
                    if not use_opp or bi < n_evolved:
                        kills_ev += 1
            if burst_left[bi] > 0:
                burst_left[bi] -= 1
                if burst_left[bi] <= 0: burst_left[bi] = WEAPON_BURST_MIN[bweapon[bi]]; burst_cd[bi] = 0.55
            else: reaction_cd[bi] = 0.28
        for zi in range(n_zombies):
            if not zalive[zi]: continue
            best_b = -1; best_d2 = 1e9
            for bi in range(n_bots):
                if not balive[bi]: continue
                d2 = (bx[bi] - zx[zi]) ** 2 + (by[bi] - zy[zi]) ** 2
                if d2 < best_d2: best_d2 = d2; best_b = bi
            if best_b < 0: continue
            dx = bx[best_b] - zx[zi]; dy = by[best_b] - zy[zi]; d = math.sqrt(best_d2)
            if d > 0.01: zx[zi] += dx / d * 0.42; zy[zi] += dy / d * 0.42
            if d < 2.0:
                bhp[best_b] -= 10 * dt * 8; damage_taken += 10 * dt * 8
                if not use_opp or best_b < n_evolved:
                    damage_taken_ev += 10 * dt * 8
                if bhp[best_b] <= 0:
                    balive[best_b] = False; deaths += 1
                    if not use_opp or best_b < n_evolved:
                        deaths_ev += 1
            if zx[zi] < 2: zx[zi] = 2
            if zx[zi] > 78: zx[zi] = 78
            if zy[zi] < 2: zy[zi] = 2
            if zy[zi] > 78: zy[zi] = 78
        # second pass for zombie melee kills (kept inline above got folded; do proper)
        # (rely on already decremented bhp)
    elo = float(kills) - float(deaths)
    econ = 0.0
    if damage_taken > 1e-6: econ = damage_dealt / damage_taken
    else: econ = damage_dealt / 10.0
    if shots > 0: econ -= 0.05 * shots / (hits + 1)
    survival = float(total_ticks) / float(max_ticks)
    stuck_frac = float(stuck_ticks) / max(1.0, float((total_ticks * n_bots)))
    camp_pen = 1.6 if (camp_ticks > total_ticks * n_bots * 0.6 and kills == 0) else 0.0
    return elo, econ, survival, stuck_frac, camp_pen, kills, deaths, damage_dealt, damage_taken, shots, hits, kills_ev, deaths_ev, damage_dealt_ev, damage_taken_ev, shots_ev, hits_ev


@numba.njit
def simulate_match(w, seed, n_bots, n_zombies, max_ticks, bot_skill, bot_weapon, w_opp=None, n_evolved=-1):
    """One headless match. Returns a struct of stats for fitness.
    w is flat. We simulate n_bots evolved bots vs each other + zombies.
    For PvP zombie tests the harness calls this with different (n_bots, n_zombies)
    compositions; fitness aggregates across arenas in harness.py.
    """
    # state arrays (stack allocated)
    bx = np.empty(16, dtype=numba.float32)
    by = np.empty(16, dtype=numba.float32)
    bhp = np.empty(16, dtype=numba.float32)
    bweapon = np.empty(16, dtype=numba.int64)
    bskill = np.empty(16, dtype=numba.float32)
    balive = np.empty(16, dtype=numba.boolean)
    bvelx = np.empty(16, dtype=numba.float32)
    bvely = np.empty(16, dtype=numba.float32)
    # zombies
    zx = np.empty(16, dtype=numba.float32)
    zy = np.empty(16, dtype=numba.float32)
    zhp = np.empty(16, dtype=numba.float32)
    zalive = np.empty(16, dtype=numba.boolean)

    rng = seed & 0xFFFFFFFF
    # init bots at random ring positions around 40,40
    for i in range(n_bots):
        v, rng = _lcg01(rng)
        ang = v * 6.283185307179586
        v2, rng = _lcg01(rng)
        rad = 8.0 + v2 * 18.0
        bx[i] = 40.0 + math.cos(ang) * rad
        by[i] = 40.0 + math.sin(ang) * rad
        bhp[i] = 100.0
        bweapon[i] = (rng >> 8) % 6  # per-bot mixed weapon (matches live random loadout)
        bskill[i] = float(bot_skill)
        balive[i] = True
        bvelx[i] = 0.0
        bvely[i] = 0.0
    for i in range(n_zombies):
        v, rng = _lcg01(rng)
        ang = v * 6.283185307179586
        v2, rng = _lcg01(rng)
        rad = 12.0 + v2 * 14.0
        zx[i] = 40.0 + math.cos(ang) * rad
        zy[i] = 40.0 + math.sin(ang) * rad
        zhp[i] = 80.0
        zalive[i] = True

    kills = 0
    deaths = 0
    damage_dealt = 0.0
    damage_taken = 0.0
    shots = 0
    hits = 0
    stuck_ticks = 0
    camp_ticks = 0
    total_ticks = 0
    # per-bot reaction / burst / ammo state (ammo parity with Bot.cs)
    burst_left = np.empty(16, dtype=numba.int64)
    burst_cd = np.empty(16, dtype=numba.float32)
    reaction_cd = np.empty(16, dtype=numba.float32)
    strafe_dir = np.empty(16, dtype=numba.int64)
    ammo2 = np.empty(16, dtype=numba.int64)
    reload_cd2 = np.empty(16, dtype=numba.float32)
    reserve2 = np.empty(16, dtype=numba.int64)
    spread2 = np.empty(16, dtype=numba.float32)
    for i in range(n_bots):
        burst_left[i] = WEAPON_BURST_MIN[bweapon[i]]
        burst_cd[i] = 0.0
        reaction_cd[i] = 0.0
        v, rng = _lcg01(rng)
        strafe_dir[i] = 1 if v > 0.5 else -1
        ammo2[i] = WEAPON_MAG[bweapon[i]]
        reserve2[i] = WEAPON_MAG[bweapon[i]] * AMMO_RESERVE_MULT
        spread2[i] = 0.0
        reload_cd2[i] = 0.0
    last_x = np.empty(16, dtype=numba.float32)
    last_y = np.empty(16, dtype=numba.float32)
    for i in range(n_bots):
        last_x[i] = bx[i]
        last_y[i] = by[i]
    stuck = np.zeros(16, dtype=numba.int64)

    dt = 0.05
    env = seed % 5
    y_raw = np.empty(5, dtype=numba.float32)
    x_obs = np.empty(INPUTS, dtype=numba.float32)

    use_opp = w_opp is not None
    if use_opp and n_evolved < 0:
        n_evolved = n_bots
    kills_ev = 0; deaths_ev = 0; damage_dealt_ev = 0.0; damage_taken_ev = 0.0; shots_ev = 0; hits_ev = 0

    for tick in range(max_ticks):
        # check early termination: one side wiped
        alive_bots = 0
        for i in range(n_bots):
            if balive[i]:
                alive_bots += 1
        alive_z = 0
        for i in range(n_zombies):
            if zalive[i]:
                alive_z += 1
        if alive_bots == 0 or (n_zombies > 0 and alive_z == 0 and alive_bots <= 1):
            # keep going for FFAs; for now stop when bots dead
            if alive_bots == 0:
                break
        total_ticks += 1
        for bi in range(n_bots):
            if not balive[bi]:
                continue
            # pick nearest enemy (bot or zombie depending on mode)
            best = -1
            best_kind = 0  # 0 bot, 1 zombie
            best_d2 = 1e9
            bx0 = bx[bi]; by0 = by[bi]
            for j in range(n_bots):
                if j == bi or not balive[j]:
                    continue
                d2 = (bx[j] - bx0) ** 2 + (by[j] - by0) ** 2
                if d2 < best_d2:
                    best_d2 = d2; best = j; best_kind = 0
            for j in range(n_zombies):
                if not zalive[j]:
                    continue
                d2 = (zx[j] - bx0) ** 2 + (zy[j] - by0) ** 2
                # zombies are preferred at equal distance? slightly less than bots (mirrors clanker 0.9/0.82)
                d2_eff = d2 * 1.05
                if d2_eff < best_d2:
                    best_d2 = d2_eff; best = j; best_kind = 1
            if best < 0:
                # wander
                v, rng = _lcg01(rng)
                ang = v * 6.283185307179586
                bx[bi] += math.cos(ang) * 0.4
                by[bi] += math.sin(ang) * 0.4
                continue
            if best_kind == 0:
                tx = bx[best]; ty = by[best]; thp = bhp[best]
            else:
                tx = zx[best]; ty = zy[best]; thp = zhp[best]
            dist = math.sqrt(best_d2)
            if env == 0: can_see = los_clear(bx0, by0, tx, ty, WALLS, 3)
            elif env == 1: can_see = los_clear(bx0, by0, tx, ty, WALLS_CROSS, 4)
            elif env == 2: can_see = True
            elif env == 3: can_see = los_clear(bx0, by0, tx, ty, WALLS_CORRIDOR, 4)
            else: can_see = los_clear(bx0, by0, tx, ty, WALLS_MAZE, 6)
            # build obs (14) — normalized
            # mirrors docs/research/01 §2: keep order frozen
            # 0 hpFrac
            x_obs[0] = bhp[bi] / 100.0
            x_obs[1] = thp / 100.0
            x_obs[2] = min(1.0, dist / 70.0)
            x_obs[3] = 1.0 if can_see else 0.0
            x_obs[4] = spread2[bi]  # fire spread 0..1 (was loseTimer placeholder)
            x_obs[5] = WEAPON_RANGE[bweapon[bi]] / 45.0
            x_obs[6] = float(WEAPON_PELLETS[bweapon[bi]]) / 8.0
            # aim acc/skill derived from weapon + skill
            x_obs[7] = 0.55 + bskill[bi] * 0.10  # 0.55..0.95
            x_obs[8] = 0.55 + bskill[bi] * 0.10
            x_obs[9] = 0.6   # aggr
            x_obs[10] = 0.5  # selfPres
            x_obs[11] = 0.2  # camper
            x_obs[12] = min(1.0, (ammo2[bi] + reserve2[bi]) / (WEAPON_MAG[bweapon[bi]] * (1.0 + AMMO_RESERVE_MULT)))  # rounds-left frac (was vel placeholder)
            x_obs[13] = min(1.0, float(stuck[bi]) / 40.0)

            # forward
            if use_opp and bi >= n_evolved:
                forward_numba(w_opp, x_obs, y_raw)
            else:
                forward_numba(w, x_obs, y_raw)
            camp = sigmoid(y_raw[0])
            retreat = sigmoid(y_raw[1])
            aim_raw = math.tanh(y_raw[2])
            fire_gate = sigmoid(y_raw[3])
            strafe_sig = sigmoid(y_raw[4])
            sdir = 1 if strafe_sig > 0.5 else -1

            # movement — policy-driven (deeper sim rework): the net controls a
            # 2D velocity. retreat 0 -> full approach, 1 -> full backpedal;
            # strafe_sig -> lateral; camp -> hold position (guardrail keeps the
            # anti-camp penalty bookkeeping).
            fwd = 1.2 * (1.0 - 2.0 * retreat)
            lat = (strafe_sig - 0.5) * 2.4
            if camp > 0.5 and bhp[bi] > 55 and dist > 18:
                camp_ticks += 1
                fwd *= 0.15
            dx = tx - bx0; dy = ty - by0
            d = max(0.001, math.sqrt(dx*dx + dy*dy))
            px = -dy / d; py = dx / d
            vx = fwd * (dx / d) + lat * px
            vy = fwd * (dy / d) + lat * py
            vmag = math.sqrt(vx*vx + vy*vy)
            if vmag > 1.3:
                vx *= 1.3 / vmag; vy *= 1.3 / vmag
            bx[bi] += vx; by[bi] += vy
            strafe_dir[bi] = 1 if strafe_sig > 0.5 else -1
            is_retreating = retreat > 0.5 and bhp[bi] < 42.0
            # clamp to arena 0..80
            if bx[bi] < 2: bx[bi] = 2
            if bx[bi] > 78: bx[bi] = 78
            if by[bi] < 2: by[bi] = 2
            if by[bi] > 78: by[bi] = 78
            # stuck detection
            if abs(bx[bi] - last_x[bi]) < 0.18 and abs(by[bi] - last_y[bi]) < 0.18:
                stuck[bi] += 1
                if stuck[bi] > 0:
                    stuck_ticks += 1
            else:
                stuck[bi] = 0
                last_x[bi] = bx[bi]; last_y[bi] = by[bi]

            # shooting
            if spread2[bi] > 0.0:
                spread2[bi] = max(0.0, spread2[bi] - SPREAD_DECAY_PER_SEC * dt)
            if reaction_cd[bi] > 0:
                reaction_cd[bi] -= dt
            if burst_cd[bi] > 0:
                burst_cd[bi] -= dt
            if reload_cd2[bi] > 0:
                reload_cd2[bi] -= dt
            if reload_cd2[bi] > 0:
                continue
            if ammo2[bi] <= 0:
                if reserve2[bi] > 0:
                    reserve2[bi] -= WEAPON_MAG[bweapon[bi]]
                    ammo2[bi] = WEAPON_MAG[bweapon[bi]]
                    reload_cd2[bi] = WEAPON_RELOAD[bweapon[bi]]
                continue  # reserve dry: out of rounds for the rest of the match
            if not can_see:
                continue
            if dist > WEAPON_RANGE[bweapon[bi]] + 2.0:
                continue
            if is_retreating:
                continue
            if fire_gate < 0.5:
                continue
            if reaction_cd[bi] > 0 or burst_cd[bi] > 0:
                continue
            # fire!
            shots += 1
            if not use_opp or bi < n_evolved:
                shots_ev += 1
            ammo2[bi] -= 1
            spread2[bi] = min(1.0, spread2[bi] + SPREAD_ADD_PER_SHOT)
            # aim bias: small skill-scaled miss rotates hit chance
            # trait jitter per bot id
            tj = trait_jitter(1000 + bi)
            hc = skill_hit_chance(bskill[bi], dist, tj)
            # aim bias widens miss: scale hit down slightly if |aim_raw| large and skill low
            # crazy good nets learn to keep |aim_raw| tiny under fire
            aim_penalty = abs(aim_raw) * (1.0 - bskill[bi] * 0.15)
            # sniper gets little penalty, shotgun more forgiving
            hc2 = hc * (1.0 - aim_penalty * 0.35) * (1.0 - spread2[bi] * SPREAD_HIT_PENALTY)
            v, rng = _lcg01(rng)
            if v > hc2:
                # miss — still burns burst
                if burst_left[bi] > 0:
                    burst_left[bi] -= 1
                    if burst_left[bi] <= 0:
                        burst_left[bi] = WEAPON_BURST_MIN[bweapon[bi]]
                        burst_cd[bi] = 0.55
                else:
                    reaction_cd[bi] = 0.28
                continue
            # hit!
            hits += 1
            if not use_opp or bi < n_evolved:
                hits_ev += 1
            # headshot roll (pellets==1 only)
            is_head = False
            if WEAPON_PELLETS[bweapon[bi]] == 1:
                v2, rng = _lcg01(rng)
                if v2 < (0.04 + bskill[bi] * 0.02):
                    is_head = True
            dmg = WEAPON_DAMAGE[bweapon[bi]]
            if is_head:
                dmg = dmg * 2.0
            damage_dealt += dmg
            if not use_opp or bi < n_evolved:
                damage_dealt_ev += dmg
            # apply
            if best_kind == 0:
                bhp[best] -= dmg
                if not use_opp or best < n_evolved:
                    damage_taken_ev += dmg
                if bhp[best] <= 0:
                    balive[best] = False
                    deaths += 1
                    kills += 1
                    if not use_opp or bi < n_evolved:
                        kills_ev += 1
                    if not use_opp or best < n_evolved:
                        deaths_ev += 1
                    # respawn zombie-ish: keep FFA populated by reviving victim as fresh zombie for horde pressure?
                    # no — leave dead for K/D accounting
            else:
                zhp[best] -= dmg
                if zhp[best] <= 0:
                    zalive[best] = False
                    kills += 1
                    if not use_opp or bi < n_evolved:
                        kills_ev += 1
            # burst accounting
            if burst_left[bi] > 0:
                burst_left[bi] -= 1
                if burst_left[bi] <= 0:
                    burst_left[bi] = WEAPON_BURST_MIN[bweapon[bi]]
                    burst_cd[bi] = 0.55
            else:
                reaction_cd[bi] = 0.28

        # zombies chase nearest bot
        for zi in range(n_zombies):
            if not zalive[zi]:
                continue
            best_b = -1
            best_d2 = 1e9
            for bi in range(n_bots):
                if not balive[bi]:
                    continue
                d2 = (bx[bi] - zx[zi]) ** 2 + (by[bi] - zy[zi]) ** 2
                if d2 < best_d2:
                    best_d2 = d2; best_b = bi
            if best_b < 0:
                continue
            dx = bx[best_b] - zx[zi]; dy = by[best_b] - zy[zi]
            d = math.sqrt(best_d2)
            if d > 0.01:
                zx[zi] += dx / d * 0.42  # zombie speed (buffed for PvE pressure) 1.2-ish but tick is 0.05 → 0.06? keep 0.35 for pace
                zy[zi] += dy / d * 0.42
            if d < 2.0:
                bhp[best_b] -= 10 * dt * 8  # melee pressure (buffed)
                damage_taken += 10 * dt * 8
                if not use_opp or best_b < n_evolved:
                    damage_taken_ev += 10 * dt * 8
                if bhp[best_b] <= 0:
                    balive[best_b] = False
                    deaths += 1
                    if not use_opp or best_b < n_evolved:
                        deaths_ev += 1
            if zx[zi] < 2: zx[zi] = 2
            if zx[zi] > 78: zx[zi] = 78
            if zy[zi] < 2: zy[zi] = 2
            if zy[zi] > 78: zy[zi] = 78

    # sim returns raw components — harness does scalarization (so weights can sweep)
    elo = float(kills) - float(deaths)
    econ = 0.0
    if damage_taken > 1e-6:
        econ = damage_dealt / damage_taken
    else:
        econ = damage_dealt / 10.0
    if shots > 0:
        econ -= 0.05 * shots / (hits + 1)
    survival = float(total_ticks) / float(max_ticks)
    stuck_frac = float(stuck_ticks) / max(1.0, float((total_ticks * n_bots)))
    camp_pen = 1.6 if (camp_ticks > total_ticks * n_bots * 0.6 and kills == 0) else 0.0
    return elo, econ, survival, stuck_frac, camp_pen, kills, deaths, damage_dealt, damage_taken, shots, hits, kills_ev, deaths_ev, damage_dealt_ev, damage_taken_ev, shots_ev, hits_ev
