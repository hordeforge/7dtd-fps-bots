# Threat Model - BotMod

Systemic view of what this mod exposes to attack, what it costs if broken, and
what stands in the way. Derived from code and deployment artifacts at release
0.7.1 (last reviewed 2026-09-28, commit e238b2b; every `WebApi.cs` line
reference re-anchored the same day against the 0.7.1 file). Every claim carries
a file reference so the next review can re-verify it.

Owner and review cadence are organizational decisions; none is defined in this
repository yet. Individual vulnerabilities and their fixes belong to sec-review;
this document records them as threats with locations.

## Scope

BotMod is a server-side 7 Days to Die mod (C#, Harmony-patched) that spawns FPS
bots and exposes an admin REST endpoint plus console commands. In scope: the
mod's own code, its shipped web bundle, its config/training artifacts, and the
boundaries it touches in the dedicated server process. Out of scope: the game
engine and stock webserver internals (trusted third-party code, referenced but
not reviewed here), the host OS, and the dev-side GA training tools
(`tools/ga/`, operator-run, not part of the deployed attack surface).

## Risk-ranked summary

| # | Risk | Boundary | State |
|---|------|----------|-------|
| G1 | Single control carries all `/api/bot` authority: authentication and authorization are delegated entirely to the stock webserver (permission level 0 declared, never re-checked in mod code). Stolen/replayed admin webtoken or a webpermissions misconfiguration yields full bot control with no second gate. | TB2 | Named gap |
| G2 | Opt-in auth bypass (`AllowSyntheticAuthBypass=true`) lets anyone who can reach the server port join with a predictable synthetic Steam id, without owning the game. Controls: default off (`Source/BotMod/Config/BotConfig.cs:17`) and the `AuthBypass=` startup log line (`Source/BotMod/ModApi.cs:34`). | TB1 | Documented residual (README Install) |
| G3 | ~~Response bodies echoed into the audit line unsanitized.~~ Closed in code: the `ok in <ms> <body>` audit line now runs the response through `LogSanitizer.Clean` (`Source/BotMod/Web/WebApi.cs:384`), so request-supplied player names and idents no longer carry C0/DEL/C1, bidi, or zero-width characters into the log. Re-verify before trusting it: the fix is one call site. | TB6 | Closed (verified 2026-09-28) |
| G4 | No mod-layer rate limit or quota on `/api/bot`: each call is clamped, but aggregate calls (mutations and the every-5s-per-session status poll, `Source/BotMod/Web/WebApi.cs:496`) are unbounded. | TB2 | Named gap |
| G5 | Operator-trusted files (`botmod.json`, neural weights, `characters.json`) are parsed without integrity verification; weights get structural validation only (`Source/BotMod/AI/BotNeuralBrain.cs:211-240`). | TB3/TB4 | Accepted risk (operator boundary) |
| G6 | Idempotency ledger eviction (oldest-first, capacity 256) can drop an active claim under key churn, allowing a late duplicate to execute twice. Admin-only trigger. Eviction now warns on the server log (`Source/BotMod/Web/WebApi.cs:66-67`, `Source/BotMod/Web/IdempotencyLedger.cs:134-152`), so the churn is attributable. | TB2 | Named gap, low |

## Assets and impact

- **A1 Dedicated server availability** - the game main thread is the chokepoint;
  the code records that touching Unity/world state off it segfaulted the server
  (`Source/BotMod/Web/WebApi.cs:184-186`). Loss: whole server down.
- **A2 Game-world fairness** - bots shoot players; admins can retarget them at a
  specific player (`spawnNear`, `vs player`). Loss: griefing at scale, PvP
  balance destroyed.
- **A3 Operator config integrity** - `Mods/BotMod/Config/botmod.json` holds every
  persisted admin decision; a torn write resets state to defaults
  (`docs/recovery.md`). Protected by atomic write + `.bak`
  (`Source/BotMod/Config/AtomicTextFile.cs:42,76`, fallback in `BotConfig.Load`,
  `Source/BotMod/Config/BotConfig.cs:192-250`).
- **A4 Audit trail integrity** - one log line per executed/replayed/rejected
  mutation is the investigation record (`Source/BotMod/Web/WebApi.cs:104-131`).
  Loss: repudiation, hidden actions.
- **A5 Player identity data** - online player names + entity ids served by
  `GET /api/bot` (`BuildStatus`, `Source/BotMod/Web/WebApi.cs:447-534`).
  Exposure limited to permission-0 holders.
- **A6 Admin browser session** - the dashboard runs in the admin's browser
  against the stock webserver; server-controlled strings (player and bot names)
  are rendered through hyperscript `h(...)` (`Source/BotMod/WebMod/bundle.ts:293,337`),
  which assigns text nodes; no `dangerouslySetInnerHTML`, `innerHTML`, or
  `document.write` sink exists anywhere in the bundle.
- **Secrets** - the mod holds none. Admin web tokens and telnet credentials live
  in the game's own configuration, outside this repository.

## Trust boundaries

- **TB1 Player/game client <-> dedicated server (network).** The mod widens
  vanilla Steam auth exactly once: `Patch_SteamAuthServer_SyntheticBypass`
  auto-passes ids 76561199000000000..76561199000010000 when
  `AllowSyntheticAuthBypass=true` (`Source/BotMod/Patches/BotPatches.cs:11-44`).
  Deployments running code mods have EAC disabled anyway (README, Install).
- **TB2 Admin browser <-> stock webserver <-> BotMod REST API.**
  `GET/POST /api/bot` (`Source/BotMod/Web/WebApi.cs:75,97`) is discovered by the
  game's webserver as an `AbsRestApi` subclass; the mod declares permission
  level 0 for all methods (`WebApi.cs:404`) and performs **no** authentication,
  authorization, or rate limiting of its own. Enforcement point is entirely in
  game-owned code/config.
- **TB3 Operator filesystem <-> mod.** Config (`BotConfig.Load`,
  `Source/BotMod/Config/BotConfig.cs:192`), characters (`BotCharacterDB.Load`,
  `Source/BotMod/ModApi.cs:32`) and world `spawnpoints.xml` (read at spawn,
  `Source/BotMod/Core/BotSpawner.cs:272-287`) are trusted as
  operator-controlled and parsed at load/reload. `config/entityclasses.xml` is
  copied into the install by `scripts/build.sh:58` and consumed by the game's
  own entity loader, not by mod code.
- **TB4 Build/GA artifacts <-> runtime.** `evolved/best.json` is promoted via
  git commit (`evolved/README.md`, see `docs/recovery.md`) and loaded at
  startup, reload, or `neural on` (`Source/BotMod/AI/BotNeuralBrain.TryLoad`).
  Build-to-runtime trust: whatever lands in the installed mod dir is executed
  as data driving combat decisions.
- **TB5 Web thread pool <-> game main thread.** Every world-touching action is
  marshaled via `RunOnMain` -> `MainThreadDispatch.Execute` with a 15 s timeout
  (`Source/BotMod/Web/WebApi.cs:396-402`, `Source/BotMod/Web/MainThreadDispatch.cs:38`).
  This is a privilege transition: queued work still runs after a dispatch
  timeout, which the error path handles by keeping the idempotency claim
  (`WebApi.cs:353-371`) and reporting the late outcome through
  `MainThreadDispatch.Abandoned` (`WebApi.cs:63-65`, `MainThreadDispatch.cs:58-59`).
- **TB6 Mod <-> server log.** Request-derived strings are scrubbed before
  logging (`LogSanitizer.Clean`, `Source/BotMod/Config/LogSanitizer.cs:24-47`,
  applied at `WebApi.cs:110-111` for the routing fields and at `WebApi.cs:384`
  for the response body); see G3 for the history of the second call site.
- **TB7 Server data <-> admin browser.** Status JSON renders player/bot names in
  the dashboard through text nodes only, with no raw-HTML sink available
  (asset A6).

## Entry points

| Entry point | Untrusted input | Reference |
|---|---|---|
| `GET /api/bot` | none (read-only status) | `Source/BotMod/Web/WebApi.cs:72-92` |
| `POST /api/bot` | `action`, `requestId`, `count`, `player`, `weapon`, `entityId`, `level`, `target`, `name`, `team`, `on` fields | `Source/BotMod/Web/WebApi.cs:94-387` |
| Console command `bot` (console/telnet) | subcommand args incl. player name/id lookups, weapon ids, team ids | `Source/BotMod/Commands/BotConsoleCommands.cs:43-47,62-80` |
| `Config/botmod.json` (+ `.bak`) | full config object; unknown keys warned | `Source/BotMod/Config/BotConfig.cs:192-250` |
| `evolved/best.json` weights | version/inputs/outputs validated, length- and NaN/Inf-checked | `Source/BotMod/AI/BotNeuralBrain.cs:179-263`; fuzz: `tests/BotMod.Web.Tests/BotNeuralBrainFuzzTests.cs` |
| `characters.json` | game data, parsed into the bot character DB | `Source/BotMod/ModApi.cs:32` |
| `spawnpoints.xml` (world) | XML positions, parsed with entity resolution disabled | `Source/BotMod/Core/BotSpawner.cs:272-313` |
| `Config/entityclasses.xml` | shipped by `scripts/build.sh:58`, read by the game's entity loader, not by mod code | `scripts/build.sh:58-59` |
| Harmony hooks | game-call surfaces: `AuthenticateUser` (`:11`), `listplayers` (`:46`), `OnEntityDeath` (`:82`), `DamageEntity` (`:106`) | `Source/BotMod/Patches/BotPatches.cs` |
| Mod events | `GameStartDone`, `GameUpdate`, `WorldShuttingDown` | `Source/BotMod/ModApi.cs:45-51,62-89` |

Nothing listed here lacks a named validation point except where noted (G4: no
aggregate quota). Inputs treated as trusted from outside a boundary: config,
character, spawnpoint and weight files (operator boundary, TB3/TB4) and world
player names echoed into responses (G3, now sanitized).

## Threats per boundary (STRIDE, tied to code)

**TB1 (client <-> server)**
- *Spoofing/EoP:* forged synthetic Steam id joins without owning the game when
  the bypass flag is on; range is fixed and documented
  (`BotPatches.cs:25-26`). Each grant is logged with id and peer IP
  (`BotPatches.cs:27`). See G2.
- *Tampering/repudiation:* vanilla cheating - owned by the game/EAC layer, out
  of scope.
- *DoS:* connection floods - game-owned; the mod adds entity load only after an
  authorized spawn.

**TB2 (admin -> API)**
- *Spoofing/EoP (SPOF, G1):* one control - webserver token authn + level-0
  declaration (`WebApi.cs:389`). No second gate in mod code.
- *Tampering:* concurrent persists interleaving - mitigated: every mutation
  body runs on the main thread and the file write is serialized by `PersistGate`
  (`WebApi.cs:152`, `Source/BotMod/ModApi.cs:196-200`); torn writes recovered
  from `.bak` (`BotConfig.cs:192-250`).
- *Repudiation:* every executed/replayed/rejected mutation logs one sanitized
  line (`WebApi.cs:114,124,131,375,384`); GET polling deliberately unlogged
  (`WebApi.cs:104-105`) - acceptable volume tradeoff, noted for investigators.
- *Information disclosure:* exception type/message/stack suppressed from
  responses; generic 500 envelope only (`WebApi.cs:366-369`), detail to log.
- *DoS:* body-size limits are game-owned; per-call clamps everywhere
  (spawn count 1..16 `WebApi.cs:168,413-420`; team 0..BotTeamCount, itself
  0..8 `:311`, `BotConfig.cs:178-182`; skill 0..4 `:269`, `BotConfig.cs:165-170`;
  global bot ceiling via `Normalize` `BotConfig.cs:252`); ledger capped at
  256 keys, 128-char keys, 10 min retention (`IdempotencyLedger.cs:27-31`);
  dispatch timeout 15 s (`WebApi.cs:396-402`). Aggregate rate: unbounded (G4).

**TB3/TB4 (files/artifacts -> runtime)**
- *Tampering/EoP:* hand-edited or substituted config/weights change bot behavior
  (damage filters, target selection). Mitigations: unknown-key warnings and
  full value clamping (`Normalize`, `BotConfig.cs:192-250,252`), weights
  rejected on version/input/output/length mismatch or NaN/Inf
  (`BotNeuralBrain.cs:211-240`) plus fuzz suites. Residual: no signature check
  (G5) - accepted because the source is the operator.
- *Tampering/DoS:* a crafted world `spawnpoints.xml` is semi-trusted shared
  content, so it is parsed with `DtdProcessing.Ignore` and a null
  `XmlResolver` (no external entity expansion, no entity-expansion DoS) and any
  parse failure falls back to the default spawn ring instead of aborting a
  spawn (`Source/BotMod/Core/BotSpawner.cs:284-313`).

**TB5 (web thread -> main thread)**
- *DoS/crash:* wrong-thread world access historically segfaulted the dedi
  (`WebApi.cs:184-186`); mitigated by mandatory `RunOnMain` marshaling and the
  ambiguous-timeout rule preventing double execution
  (`WebApi.cs:353-371`, `MainThreadDispatch.cs:38-60`). A dispatch that ran
  after its caller gave up is now logged through the `Abandoned` sink
  (`WebApi.cs:63-65`), so a lost response is distinguishable from a no-op.

**TB6 (output -> audit log)**
- *Repudiation/tampering:* log forging via CR/LF, terminal escapes, bidi/zero-
  width controls - mitigated for the routing fields (`WebApi.cs:110-111`) and,
  since the fix verified on 2026-09-28, for the response body as well
  (`WebApi.cs:384`, G3).

## Abuse cases

- **Hostile-but-authenticated admin (griefing at scale).** An authenticated
  permission-0 user can aim bots at a chosen player (`POST /api/bot`
  `action=spawnNear`, `WebApi.cs:197-233`) and keep them there across respawns,
  or flip `vs player on`. Bounded only by MaxBots <= 64. This is the tool's
  intended power; the mitigation is webserver credential hygiene (game-owned),
  not mod code.
- **Network peer joins without owning the game.** With
  `AllowSyntheticAuthBypass=true`, any reachable peer presents an id in
  76561199000000000..10000 and authenticates (`BotPatches.cs:24-27`). Enabling
  scenario is documented in README (Install). Residual risk accepted by config.
- **Dedup exhaustion.** An authenticated caller submits many distinct
  `requestId`s; at 256 live entries the oldest claims are evicted
  (`IdempotencyLedger.cs:134-152`), so a retried spawn inside the window may
  execute twice. The eviction count is warned to the server log
  (`WebApi.cs:66-67`), so a churning or abusive caller is visible. Impact:
  duplicate bots up to the hard cap; no crash path.
- **Client-side enforcement:** none relied upon. Every dashboard-controllable
  value is re-clamped server-side (clamps cited above); the bundle is display +
  submit only.

## Mitigation-to-threat map (existing controls)

| Control | Covers | Reference |
|---|---|---|
| Webserver authn + permission level 0 declaration | all TB2 spoofing/EoP (sole gate, G1) | `Source/BotMod/Web/WebApi.cs:404` |
| Deny-side matrix tests: web API method levels and console default level pinned to 0 | TB2 gate regression (widened declaration fails `make test`) | `tests/BotMod.Web.Tests/WebApiAuthzTests.cs` |
| Bypass flag default-off + startup visibility | TB1 spoofing blast radius (G2) | `BotConfig.cs:17`, `ModApi.cs:34` |
| Per-join bypass logging (id + peer IP) | TB1 attribution | `BotPatches.cs:27` |
| Sanitized audit fields, including the response body | TB6 log forging (closes G3) | `WebApi.cs:110-111,384`, `LogSanitizer.cs:24-47`; fuzz: `tests/BotMod.Web.Tests/LogSanitizerFuzzTests.cs` |
| Generic 500 envelope, exception detail to log only | TB2 information disclosure | `WebApi.cs:366-369` |
| Serialized atomic persists + `.bak` recovery | A3 tampering/durability | `ModApi.cs:196-200`, `AtomicTextFile.cs:42,76`, `BotConfig.cs:192-250` |
| Locked team-map access | race between web writes and damage-event reads | `BotConfig.cs:58-125` |
| Input clamps (all POST fields, config `Normalize`) | TB2 DoS/value abuse | `WebApi.cs:154-349,413-420`, `BotConfig.cs:252` |
| Bounded idempotency ledger + eviction warning | retry storms, unbounded memory, silent dedup loss (G6) | `IdempotencyLedger.cs:27-31,134-152`, `WebApi.cs:66-67` |
| Main-thread dispatch + 15 s timeout + claim-on-timeout + abandoned-dispatch warning | TB5 crash/double-exec/lost response | `WebApi.cs:63-65,353-371,396-402`, `MainThreadDispatch.cs:38-60` |
| Weight structural validation (version, shape, length, NaN/Inf) + fuzz suites | TB4 malformed artifacts | `BotNeuralBrain.cs:211-240`, `tests/BotMod.Web.Tests/BotNeuralBrainFuzzTests.cs` |
| `spawnpoints.xml` parsed with DTD processing off and a null resolver | TB3 XXE / entity-expansion DoS from a crafted world file | `Source/BotMod/Core/BotSpawner.cs:284-290` |
| Text-node rendering, no raw HTML sink in the bundle | TB7 XSS into the admin session | `Source/BotMod/WebMod/bundle.ts:293,337` |

Documentation claims checked against code this pass: README's "authenticated
GET/POST /api/bot ... permission level 0" matches `WebApi.cs:389`; the
auth-bypass description matches `BotPatches.cs:25`. Two claims the code
contradicted were corrected rather than kept: `SECURITY.md` named 0.4.0 as the
supported release (the shipped version is 0.7.1 in `BotModVersion.cs` and
`ModInfo.xml`) and cited `WebApi.cs:298` for the permission-level declaration
(it is `WebApi.cs:389`). No remaining contradicted claim found.

## Response readiness (note only)

Mutation audit lines cover success, replay, rejection, and failure outcomes
with durations, giving investigators a per-action trail; status polling is
unlogged by design. Two silent failure modes of the web plumbing are also
surfaced to the server log: a main-thread dispatch that ran after its caller
timed out (`WebApi.cs:63-65`) and idempotency-ledger capacity evictions
(`WebApi.cs:66-67`). What is still untraced: the game's own auth decisions for
non-synthetic joins, and `bot` console/telnet commands, which are covered by
the game's console permission level rather than a mod audit line. There is no
documented vulnerability-report-to-fix path: `SECURITY.md` states that absence
explicitly; defining the process is an organizational decision.
