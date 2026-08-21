# R13 — Static (Q3/D3-style) Bot Parity Audit: clanker heuristic vs zdtd_bot guest (2026-08-21)

*The clanker static/heuristic bot AI (`BotBrain.cs`, `Bot.cs` non-neural paths,
`BotConfig.cs`) is cross-pollinated with zdtd's bot brain guest
(`zdtd/mods/zdtd_bot/zdtd_bot.c`, a Wasm plugin per zdtd ADR 0026; the host
`bot.zig` is a servant). zdtd's `docs/BOTS_SPEC.md` and `docs/BOTS_PRD.md` cite
clanker (and `docs/q3-inspiration-notes.md`) as the Q3/Doom 3 behavioral
reference; this report is the parity ledger for that relationship. This audit
compares the two side by side, with the game's
`items.xml` as ground truth for weapon data. Status: `verified` (static reading +
game data); the alignment itself needs direction (see section 5).*

## 1. What is in sync (already matches)

- Target scoring structure: nearest with kind multipliers (clanker `player ×0.82,
  bot ×0.9` on linear distance; zdtd `player ×0.67 = 0.82^2, bot ×0.81 = 0.9^2`
  on squared distance — the same weights, squared) and a wounded-target bonus.
- FOV cone with a close-spot radius (clanker `dist<12` wider cone, `<7 m` always
  spotted; zdtd `CLOSE_SPOT_RANGE 7.0`, `<7 m` always spotted).
- Grudge/retaliation: remember attacker ~15 s, bias target score, halve reaction
  when hit (clanker `preferredScale 0.6`; zdtd `(0.85-0.35*venge)` mid 0.67).
- Reaction-halve-on-hit, per-engagement aim error re-roll, burst+pause firing
  rhythm, ammo/reload pacing (holds fire while reloading, keeps moving), low-HP
  retreat with cover-seeking, lost-sight memory (~4.5-5 s), phased dodge
  (backpedal then strafe, heavy-hit stagger), camp-hold with slow yaw sweep,
  stuck juke with perpendicular offset, keep-range backpedal for long weapons.
- Weapon damage/range table (host): pistol 16/40, shotgun 14/22/8 pellets,
  auto 9/22/6 pellets, AK 16/55, sniper 42/90, SMG 9/35. Identical.
- Deterministic LCG RNG only; no wall-clock noise in decisions.

## 2. Tunable divergences (same behavior, different numbers)

| Behavior | clanker | zdtd guest | Note |
|---|---|---|---|
| Reaction time (skill/diff 0..4) | 0.42..0.06 s | 0.60..0.16 s (`max(0.6-0.11s, 0.08)`) | different curves |
| Headshot chance | d0-1 cap 0.04, d3 >=0.16, d4 >=0.18 | `0.05+0.05*s` (5-25%) | different |
| Wounded-target bonus | `+(hp/100)*6` on linear dist | `+0.02*hp` on d2 | clanker ~3x stronger |
| Lead fire | `t = dist/55`, scale `0.25+0.18*diff` (+0.15 range>40), vel clamp 12 | `t = dist/40`, weapon lead scale | different bullet speed + scaling |
| Hit model | angular spray (spread deg, skill-scaled, min 0.2 deg) | probability roll `0.34+0.15*s` clamp [0.28,0.95] x dscale | structurally different (see 4) |
| Vision range | 70 (d0-2), 110/120 (d3/4) | 25..57 blocks (`25+8*s`) | different scales |
| FOV | flat 190 deg config, never scaled | skill-scaled 90..170 deg (`1.57+0.35*s`) | clanker has no FOV scaling |
| Strafe-orbit blend | toward 0.22 + perp 0.78 | toward 0.3 + perp 1.0 | different |
| Backpedal blend | -toward 0.55 + perp 0.45 | -toward 1.7 + perp 1.1 | different |
| Keep-range threshold | `max(6, effRange*0.35)` | `range > 40 && dist < range*0.5` | different |
| Retreat gate | `hp<0.35 && selfpres>0.55 && aggr<0.75`, finish-kill override | `hp<0.20 || (hp < 0.20+0.25*selfpres && aggr<0.7)` | different |
| Retreat firing | fires while retreating (re-enters Attack) | fire suppressed while retreating | behavioral divergence |
| Burst pause | `BurstPause*(0.85+rng*0.3)` per weapon | `0.25/0.45` by `skill%2` (sniper/auto fixed) | zdtd quirk: odd skills slower |
| Chase speed | engine `MoveTo` | 4.2 (s>=2) / 3.2 blocks/s | platform-dependent |

## 3. Weapon magazine sizes (game ground truth vs both)

| Gun | game `items.xml` | clanker | zdtd guest |
|---|---|---|---|
| pistol (T1) | 15 | 12 | 12 |
| double-barrel | 2 | 6 | 6 |
| AK47 | 30 | 30 | 30 |
| sniper | 12 | 5 | 5 |
| auto-shotgun | 16 | 6 | 40 |
| SMG5 | 30 | 32 | 32 |

Neither implementation matches the game; clanker and zdtd disagree on the
auto-shotgun (6 vs 40). Note the clanker headless sim `combat_sim.py` weapon
table shares these values, so any change affects the trained champion.

## 4. Structural differences (cannot be "synced" directly)

- Hit resolution: clanker is Unity raycast with angular spray; zdtd is a
  probability roll (`rng < hit_chance`) because the Wasm guest cannot raycast
  into voxels (the host gates LOS instead). Same intent, different mechanics.
- Movement physics: clanker uses engine `MoveEntityHeaded` + a 1.6 m/s manual
  fallback for trader bodies; zdtd integrates `speed*dt` with wall-slide in the
  host. Speeds are not directly comparable.
- Platform: C# Unity mod vs Wasm guest + Zig host.

## 5. Findings and recommendation (needs direction)

The static bots are behaviorally equivalent in structure but diverge in most
tunables, and neither matches the game's magazine data. "In sync" is not a
binary state here; the following need a decision:

1. **Canonical direction**: "in-sync with zdtd" reads as clanker -> zdtd, but
   some zdtd values are quirky (auto mag 40 vs game 16; `skill%2` burst throttle;
   the near-zero wounded bonus). Recommend: align to game data where it exists
   (mag sizes), and to zdtd's skill-table shape for the rest, fixing zdtd's
   quirks in the same pass.
2. **Retreat firing**: zdtd suppresses fire while retreating; clanker's heuristic
   does not. Recommend: align clanker to zdtd (suppress) for the heuristic path
   only — the neural path keeps its policy-driven behavior (R11).
3. **Hit model**: keep the two mechanics (raycast vs probability) but align the
   skill-scaled accuracy curves so the effective hit rates match.
4. **Weapon mags**: aligning to the game changes the headless sim's ammo economy
   and the champion's behavior; do it deliberately with a re-eval, not as a
   silent edit.
5. Any zdtd-side change goes through zdtd's own process (provenance rows,
   `make check` green, Wasm rebuild).

## 6. Executed alignment (mags, 2026-08-21)

Decision item 4 executed deliberately: magazine sizes aligned to the game's
`items.xml` MagazineSize base_set on all three sides, with re-eval:

| Gun | game | old (clanker/zdtd) | new |
|---|---|---|---|
| pistol | 15 | 12 / 12 | 15 |
| double-barrel | 2 | 6 / 6 | 2 |
| AK47 | 30 | 30 / 30 | 30 |
| sniper | 12 | 5 / 5 | 12 |
| auto-shotgun | 16 | 6 / 40 | 16 |
| SMG5 | 30 | 32 / 32 | 30 |

- `tools/ga/combat_sim.py` WEAPON_MAG, `Source/BotMod/Config/BotConfig.cs`
  WeaponProfile MagSize (auto vs double-barrel split 16/2), and the zdtd guest
  `weapon_mag` (wasm rebuilt) updated.
- Re-eval on the canonical gate with the new mags: **champion held 13.04 avg**
  (12.696/13.612/12.815), margins +8.044/+8.862/+8.223, GOAL MET. The champion
  IMPROVED (11.91 -> 13.04): larger mags mean less reload downtime for the
  kiter. No re-evolution needed.
- BotMod.dll rebuilt and installed (live bots get the game-accurate mags on the
  next server restart).

Still open (needs direction): decision items 1, 2, 3 — retreat-firing behavior,
the skill-table curves, and the hit-model curve alignment.
