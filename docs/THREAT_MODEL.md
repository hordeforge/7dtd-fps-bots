# Threat Model - BotMod

Systemic view of what this mod exposes to attack, what it costs if broken, and
what stands in the way. Derived from code and deployment artifacts at release
0.7.1 (last reviewed 2026-09-28, commit 1ecc945, after the Foundation-layer
split; every line reference in this file was re-anchored against that tree).
Every claim carries a file reference so the next review can re-verify it.

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
| G1 | Single control carries all `/api/bot` authority: authentication and authorization are delegated entirely to the stock webserver (permission level 0 declared, never re-checked in mod code). A stolen/replayed admin webtoken or a webpermissions misconfiguration yields full bot control with no second gate. | TB2 | Named gap |
| G2 | Opt-in auth bypass (`AllowSyntheticAuthBypass=true`) lets anyone who can reach the server port join with a predictable synthetic Steam id, without owning the game. Controls: default off (`Source/BotMod/Config/BotConfig.cs:18`), a per-join log line (`Source/BotMod/Patches/BotPatches.cs:47`), the `AuthBypass=` startup line (`Source/BotMod/ModApi.cs:48`), and a WARN on every load and reload naming the file that asked for it (`Source/BotMod/Config/BotConfig.cs:395`). The log line names the in-world entity id only, not the id or the peer IP (`BotPatches.cs:42-47`), so attribution after the fact rests on entity id, which the client picks in part. | TB1 | Documented residual (README Install) |
| G3 | ~~Response bodies echoed into the audit line unsanitized.~~ Closed in code: the `ok in <ms> <body>` audit line runs the response through `LogSanitizer.Clean` (`Source/BotMod/Web/WebApi.cs:233`), so request-supplied player names and idents no longer carry C0/DEL/C1, bidi, or zero-width characters into the log. Re-verify before trusting it: the fix is one call site. | TB6 | Closed (verified 2026-09-28) |
| G4 | No mod-layer rate limit or quota on `/api/bot`: each call is clamped, but aggregate calls (mutations, and the dashboard's status poll on its own timer) are unbounded, and every mutation is serialized onto the game main thread (`Source/BotMod/Web/WebApi.cs:189`). | TB2/TB5 | Named gap |
| G5 | Operator-trusted files (`botmod.json`, neural weights, `characters.json`) are parsed without integrity verification; weights get structural validation only (`Source/BotMod/AI/BotNeuralBrain.cs:215-267`). | TB3/TB4 | Accepted risk (operator boundary) |
| G6 | Idempotency ledger eviction (oldest-first, capacity 256) can drop an active claim under key churn, allowing a late duplicate to execute twice. Admin-only trigger. Eviction warns on the server log (`Source/BotMod/Web/WebApi.cs:79-80`, `Source/BotMod/Web/IdempotencyLedger.cs:174-193`), so the churn is attributable. | TB2 | Named gap, low |
| G7 | The `BOTMOD_CONFIG` environment variable redirects the config path, and the same path is the persist target (`Source/BotMod/Config/BotConfig.cs:396-420`, `Source/BotMod/ModApi.cs:252-254`). Anyone who can set the server process's environment can choose which file the admin's toggles persist into and which file the next boot reads. The override is named in the `bot config` output and the config summary log, so the redirection is visible after the fact but not prevented. | TB3 | Named gap (host/config boundary) |
| G8 | The `bot` console command has no mod-side authorization or audit: `Execute` ignores `CommandSenderInfo` (`Source/BotMod/Commands/BotConsoleCommands.cs:60`), so its only gate is the game's default console permission level, and no mod log line records who ran a mutation from the console. | TB2 | Named gap |

## Assets and impact

- **A1 Dedicated server availability** - the game main thread is the chokepoint;
  the code records that touching Unity/world state off it segfaulted the server
  (`Source/BotMod/Web/WebApi.cs:206-208`). Loss: whole server down.
- **A2 Game-world fairness** - bots shoot players; admins can retarget them at a
  specific player (`spawnNear`, `vs player`). Loss: griefing at scale, PvP
  balance destroyed.
- **A3 Operator config integrity** - `Mods/BotMod/Config/botmod.json` holds every
  persisted admin decision; a torn write resets state to defaults
  (`docs/recovery.md`). Protected by atomic write + `.bak`
  (`Source/BotMod/Foundation/AtomicTextFile.cs:46,52-77`, fallback in
  `BotConfig.Load`, `Source/BotMod/Config/BotConfig.cs:197-260`). The file's
  location is operator controlled and can be redirected with `BOTMOD_CONFIG`
  (`BotConfig.ConfigPath`, `BotConfig.cs:396-420`; G7). The full effective
  config is logged at startup and reload (`BotConfig.EffectiveSummary`,
  `Source/BotMod/Config/BotConfig.cs:382`, emitted at
  `Source/BotMod/ModApi.cs:52,127`): BotConfig carries no credentials, so the
  dump adds no secret exposure, and the security-relevant switch
  `AllowSyntheticAuthBypass` is in it by name (`ModApi.cs:48`).
- **A4 Audit trail integrity** - one log line per executed/replayed/rejected
  mutation is the investigation record (`Source/BotMod/Web/WebApi.cs:150,157,168,412,421`).
  Loss: repudiation, hidden actions. Console mutations produce no such line (G8).
- **A5 Player identity data** - online player names + entity ids served by
  `GET /api/bot` (`BuildStatus`, `Source/BotMod/Web/WebApi.cs:512-...`) and
  echoed back by a `spawnNear` response (`WebApi.cs:252-254`). Exposure limited
  to permission-0 holders; the idempotency ledger also holds such a body in
  memory for up to 10 minutes (A7).
- **A6 Admin browser session** - the dashboard runs in the admin's browser
  against the stock webserver; server-controlled strings (player and bot names)
  are rendered as React children through `React.createElement`
  (`Source/BotMod/WebMod/bundle.ts:692`, used for player options at `:424` and
  bot names at `:521,593`), so values become text nodes; no `innerHTML`,
  `dangerouslySetInnerHTML`, or `document.write` sink exists in the bundle.
- **A7 Response-body cache** - the idempotency ledger retains up to 256 response
  bodies (`Source/BotMod/Web/IdempotencyLedger.cs:34,41`), some of which contain
  a player name. Memory-resident only, cleared by `PruneLocked` on age
  (`IdempotencyLedger.cs:160-173`); not written to disk.
- **Secrets** - the mod holds none. Admin web tokens and telnet credentials live
  in the game's own configuration, outside this repository.

## Trust boundaries

- **TB1 Player/game client <-> dedicated server (network).** The mod widens
  vanilla Steam auth exactly once: `Patch_SteamAuthServer_SyntheticBypass`
  auto-passes ids 76561199000000000..76561199000010000 when
  `AllowSyntheticAuthBypass=true` (`Source/BotMod/Patches/BotPatches.cs:16-59`,
  range constants at `:21-22`, gate at `:29`). A failure inside the patch falls
  through to vanilla auth and warns rather than passing silently
  (`BotPatches.cs:55-58`). Deployments running code mods have EAC disabled
  anyway (README, Install).
- **TB2 Admin browser <-> stock webserver <-> BotMod REST API.**
  `GET/POST /api/bot` (`Source/BotMod/Web/WebApi.cs:85,108`) is discovered by the
  game's webserver as an `AbsRestApi` subclass; the mod declares permission
  level 0 for every method slot and inherits a level-0 global fallback
  (`WebApi.cs:426`) and performs **no** authentication, authorization, or rate
  limiting of its own. Enforcement point is entirely in game-owned code/config.
  The console command surface (`Source/BotMod/Commands/BotConsoleCommands.cs:60`)
  is the second admin surface and is equally un-gated in mod code (G8).
- **TB3 Operator filesystem / process environment <-> mod.** Config
  (`BotConfig.Load`, `Source/BotMod/Config/BotConfig.cs:197`), characters
  (`BotCharacterDB.Load`, `Source/BotMod/ModApi.cs:47`) and world
  `spawnpoints.xml` (read at spawn, `Source/BotMod/Core/BotSpawner.cs:289-325`)
  are trusted as operator-controlled and parsed at load/reload. The
  `BOTMOD_CONFIG` environment variable is a third input on this boundary
  (`BotConfig.cs:396-420`, G7). `config/entityclasses.xml` is copied into the
  install by `scripts/build.sh:58` and consumed by the game's own entity loader,
  not by mod code.
- **TB4 Build/GA artifacts <-> runtime.** `evolved/best.json` is promoted via
  git commit (`evolved/README.md`, see `docs/recovery.md`) and loaded at
  startup, reload, or `neural on` (`Source/BotMod/AI/BotNeuralBrain.TryLoad`,
  `Source/BotMod/Web/WebApi.cs:275-277`). Build-to-runtime trust: whatever lands
  in the installed mod dir is executed as data driving combat decisions.
- **TB5 Web thread pool <-> game main thread.** Every action body, config
  mutation included, is marshaled via `RunOnMain` -> `MainThreadDispatch.Execute`
  with a 15 s timeout (`Source/BotMod/Web/WebApi.cs:189,472-478`,
  `Source/BotMod/Web/MainThreadDispatch.cs:47,98`). This is a privilege
  transition: queued work still runs after a dispatch timeout, which the error
  path handles by keeping the idempotency claim (`WebApi.cs:390-401`) and
  reporting the late outcome through `MainThreadDispatch.Abandoned`
  (`WebApi.cs:76-78`, `MainThreadDispatch.cs:82-83`). It is also the DoS
  amplifier of G4: unbounded API calls queue behind one thread.
- **TB6 Mod <-> server log.** Request-derived strings are scrubbed before
  logging (`LogSanitizer.Clean`, `Source/BotMod/Foundation/LogSanitizer.cs:25-47`,
  which covers `char.IsControl` (C0, DEL, C1) plus `BotText.IsInvisible` for
  the whole Unicode `Bidi_Control` set (U+061C, U+200E, U+200F, U+202A-U+202E,
  U+2066-U+2069), zero-width characters, line and paragraph separators, the
  word-joiner run, BOM and variation selectors; applied at `WebApi.cs:151-152`
  for the routing fields, at `:158` for the response header, and at `:233` for
  the response body); see G3 for the history of the last of those.
- **TB7 Server data <-> admin browser.** Status JSON renders player/bot names as
  React text nodes only, with no raw-HTML sink available (asset A6).

## Entry points

| Entry point | Untrusted input | Reference |
|---|---|---|
| `GET /api/bot` | none (read-only status); response marked `Cache-Control: no-store` | `Source/BotMod/Web/WebApi.cs:85-106`, `:462-465` |
| `POST /api/bot` | `action`, `requestId`, `count`, `player`, `weapon`, `entityId`, `level`, `target`, `name`, `team`, `on` fields | `Source/BotMod/Web/WebApi.cs:108-424` |
| POST body field reader (shared) | typed coercion of ints/bools/strings, invariant-culture; `FieldRead.Absent/Ok/Invalid` tri-state | `Source/BotMod/Web/RequestFields.cs:38,58,83`; fuzz: `tests/BotMod.Tests/BotAdminSettersFuzzTests.cs`, `RequestFieldsTests.cs` |
| Console command `bot` (console/telnet) | subcommand args incl. player name/id lookups, weapon ids, team ids; `CommandSenderInfo` unused | `Source/BotMod/Commands/BotConsoleCommands.cs:45-49,60` |
| Environment `BOTMOD_CONFIG` | config/persist path override | `Source/BotMod/Config/BotConfig.cs:396-420` |
| `Config/botmod.json` (+ `.bak`, `.tmp`) | full config object; unknown keys warned, all values re-clamped by `Normalize` | `Source/BotMod/Config/BotConfig.cs:197-260,266-336`; fuzz: `tests/BotMod.Tests/BotConfigFuzzTests.cs` |
| `evolved/best.json` weights | version/inputs/outputs validated, length- and NaN/Inf-checked | `Source/BotMod/AI/BotNeuralBrain.cs:184-289`; fuzz: `tests/BotMod.Tests/BotNeuralBrainFuzzTests.cs` |
| `characters.json` | game data, parsed into the bot character DB | `Source/BotMod/ModApi.cs:47,125`; fuzz: `tests/BotMod.Tests/BotCharacterFuzzTests.cs` |
| `spawnpoints.xml` (world) | XML positions, parsed with DTD and entity resolution off | `Source/BotMod/Core/BotSpawner.cs:289-325` |
| `Config/entityclasses.xml` | shipped by `scripts/build.sh:58`, read by the game's entity loader, not by mod code | `scripts/build.sh:58-59` |
| Harmony hooks | game-call surfaces: `AuthenticateUser` (`:16`), `listplayers` (`:64`), `OnEntityDeath` (`:101`), `DamageEntity` (`:130`) | `Source/BotMod/Patches/BotPatches.cs` |
| Mod events | `GameStartDone`, `GameUpdate`, `WorldShuttingDown` | `Source/BotMod/ModApi.cs:63-70,80-108` |

Nothing listed here lacks a named validation point except where noted (G4: no
aggregate quota; G7: the env override is trusted, not validated; G8: the console
surface has no mod-side gate). Inputs treated as trusted from outside a
boundary: config, character, spawnpoint and weight files and the
`BOTMOD_CONFIG` value (operator boundary, TB3/TB4) and world player names
echoed into responses (G3, now sanitized on the way to the log).

## Threats per boundary (STRIDE, tied to code)

**TB1 (client <-> server)**
- *Spoofing/EoP:* forged synthetic Steam id joins without owning the game when
  the bypass flag is on; range is fixed and documented
  (`BotPatches.cs:21-22,39-40`). The grant is logged with the in-world entity id
  (`BotPatches.cs:47`) and nothing else: the code deliberately omits the peer IP
  as personal data and the id as a fixed block that carries no diagnostic value
  (`BotPatches.cs:42-46`). Consequence for this model: attribution after the
  fact is entity-id based, not address based. See G2.
- *Repudiation:* with the bypass on, a peer that never authenticates still
  occupies an entity id, and the audit trail records that id rather than a
  network identifier. Game-side connection logging is the only address record.
- *Tampering:* vanilla cheating - owned by the game/EAC layer, out of scope.
- *DoS:* connection floods - game-owned; the mod adds entity load only after an
  authorized spawn.

**TB2 (admin -> API)**
- *Spoofing/EoP (SPOF, G1):* one control - webserver token authn + level-0
  declaration (`WebApi.cs:426`). No second gate in mod code.
- *Tampering:* concurrent persists interleaving - mitigated: every mutation body
  runs on the main thread and the file write is serialized by `PersistGate`
  (`WebApi.cs:189`, `Source/BotMod/ModApi.cs:216,252-254`); torn writes
  recovered from `.bak` (`BotConfig.cs:197-260`, `AtomicTextFile.cs:52-77`).
  Idempotency-key reuse for a different body is refused rather than replayed
  (`WebApi.cs:166-170`, `RequestFields.Fingerprint`,
  `Source/BotMod/Web/RequestFields.cs:102`).
- *Repudiation:* every executed/replayed/rejected mutation logs one sanitized
  line carrying the request tag (`WebApi.cs:150,157,168,412,421`) and echoes
  that tag in `X-BotMod-Request-Id` on every outcome (`WebApi.cs:136`), so a
  failed call ties back to one log line. GET polling deliberately unlogged
  (`WebApi.cs:119-120`) - acceptable volume tradeoff, noted for investigators.
  Console mutations are unlogged entirely (G8).
- *Information disclosure:* exception type/message/stack suppressed from
  responses; generic 500 envelope only (`WebApi.cs:402-406`), detail to log.
  Responses are `Cache-Control: no-store` on every path
  (`WebApi.cs:462-465`), so a shared cache or an admin's bfcache cannot retain a
  bot roster or config.
- *DoS:* body-size limits are game-owned; per-call clamps everywhere
  (spawn count 1..16 `WebApi.cs:498`; team 0..BotTeamCount, itself 0..8 `:348`,
  `BotConfig.cs:185-187`; skill 0..4 `:302-307`, `BotConfig.cs:170-174`;
  global bot ceiling via `Normalize` `BotConfig.cs:269`); ledger capped at
  256 keys, 128-char keys, 10 min retention
  (`IdempotencyLedger.cs:34,38,41,103-105`); dispatch timeout 15 s
  (`WebApi.cs:477`). Aggregate rate: unbounded, and each call queues on the main
  thread (G4).

**TB3/TB4 (files/artifacts -> runtime)**
- *Tampering/EoP:* hand-edited or substituted config/weights change bot behavior
  (damage filters, target selection). Mitigations: unknown-key warnings and
  full value clamping (`Normalize`, `BotConfig.cs:266-336`), weights
  rejected on version/input/output/length mismatch or NaN/Inf
  (`BotNeuralBrain.cs:215-267`) plus fuzz suites. Residual: no signature check
  (G5) - accepted because the source is the operator.
- *Spoofing:* the `BOTMOD_CONFIG` override (G7) lets a party who cannot edit the
  installed config nonetheless redirect where it is read from and written to, by
  controlling the server process environment.
- *Tampering/DoS:* a crafted world `spawnpoints.xml` is semi-trusted shared
  content, so it is parsed with `DtdProcessing.Ignore` and a null
  `XmlResolver` (no external entity expansion, no entity-expansion DoS) and any
  parse failure falls back to the default spawn ring instead of aborting a
  spawn (`Source/BotMod/Core/BotSpawner.cs:303-325`).

**TB5 (web thread -> main thread)**
- *DoS/crash:* wrong-thread world access historically segfaulted the dedi
  (`WebApi.cs:206-208`); mitigated by mandatory `RunOnMain` marshaling and the
  ambiguous-timeout rule preventing double execution
  (`WebApi.cs:390-401`, `MainThreadDispatch.cs:47-98`). A dispatch that ran
  after its caller gave up is logged through the `Abandoned` sink
  (`WebApi.cs:76-78`), so a lost response is distinguishable from a no-op.
- *DoS/amplification:* every mutation, including a pure config flip, takes a
  main-thread slot (`WebApi.cs:178-189`). An authenticated caller with no rate
  limit can therefore stall the tick loop by flooding cheap mutations (G4).

**TB6 (output -> audit log)**
- *Repudiation/tampering:* log forging via CR/LF, terminal escapes, bidi/zero-
  width controls - mitigated for the routing fields (`WebApi.cs:129-130`), the
  correlation header (`WebApi.cs:136`), and, since the fix verified on
  2026-09-28, for the response body (`WebApi.cs:421`, G3). Note the two
  sinks wired in the static ctor (`WebApi.cs:76-80`) also route their strings
  through `Clean` for the op name.

## Abuse cases

- **Hostile-but-authenticated admin (griefing at scale).** An authenticated
  permission-0 user can aim bots at a chosen player (`POST /api/bot`
  `action=spawnNear`, `WebApi.cs:219-256`) and keep them there across respawns,
  or flip `vs player on` (`WebApi.cs:321-334`). Bounded only by MaxBots <= 64
  (`BotConfig.cs:269`). This is the tool's intended power; the mitigation is
  webserver credential hygiene (game-owned), not mod code.
- **Network peer joins without owning the game.** With
  `AllowSyntheticAuthBypass=true`, any reachable peer presents an id in
  76561199000000000..10000 and authenticates (`BotPatches.cs:21-38`). Enabling
  scenario is documented in README (Install). Residual risk accepted by config;
  see G2 for what the log line does and does not record.
- **Dedup exhaustion.** An authenticated caller submits many distinct
  `requestId`s; at 256 live entries the oldest claims are evicted
  (`IdempotencyLedger.cs:174-193`), so a retried spawn inside the window may
  execute twice. The eviction count is warned to the server log
  (`WebApi.cs:79-80`), so a churning or abusive caller is visible. Impact:
  duplicate bots up to the hard cap; no crash path.
- **Keyed-request poisoning of the response cache.** A caller can pin up to 256
  response bodies in the ledger for 10 minutes, including bodies containing a
  player name, using cheap actions. Memory impact is capped by design
  (`IdempotencyLedger.cs:34,41`); the cache is not readable by any other
  principal because replay is keyed on the same secret-free request id.
- **Console-side abuse with no trail.** Anyone with console permission can run
  `bot spawn`, `bot player <name>`, or `bot reload` with no mod audit line and
  no check of `CommandSenderInfo` (`BotConsoleCommands.cs:60`). Same capability
  as the API, materially worse forensics (G8).
- **Client-side enforcement:** none relied upon. Every dashboard-controllable
  value is re-clamped or re-validated server-side (clamps cited above); the
  bundle is display + submit only.

## Mitigation-to-threat map (existing controls)

| Control | Covers | Reference |
|---|---|---|
| Webserver authn + permission level 0 declaration (all slots, plus level-0 global fallback) | all TB2 spoofing/EoP (sole gate, G1) | `Source/BotMod/Web/WebApi.cs:426` |
| Deny-side matrix tests: web API method levels and console default level pinned to 0 | TB2 gate regression (a widened declaration fails `make test`) | `tests/BotMod.Tests/WebApiAuthzTests.cs:43-61` |
| Bypass flag default-off + startup visibility | TB1 spoofing blast radius (G2) | `BotConfig.cs:18`, `ModApi.cs:48` |
| Per-join bypass logging (in-world entity id; id and peer IP deliberately omitted) | TB1 session correlation, not address attribution (G2) | `BotPatches.cs:42-47` |
| Synthetic-bypass check failure warns and falls through to vanilla auth | silent loss of the bypass mid-session | `BotPatches.cs:55-58` |
| Sanitized audit fields, correlation header, and response body | TB6 log forging, response-header injection (closes G3) | `WebApi.cs:129-130,136,421`, `LogSanitizer.cs:25-47`; fuzz: `tests/BotMod.Tests/LogSanitizerFuzzTests.cs` |
| Request tag echoed in `X-BotMod-Request-Id` on every outcome | TB2 repudiation / correlation | `WebApi.cs:136`, `NextRequestTag` at `:435-439` |
| `Cache-Control: no-store` on every response path | roster/config retention in shared caches, bfcache | `WebApi.cs:462-465` |
| Generic 500 envelope, exception detail to log only | TB2 information disclosure | `WebApi.cs:402-406` |
| Serialized atomic persists + `.bak` recovery under one gate (readers included) | A3 tampering/durability | `ModApi.cs:216,252-254`, `AtomicTextFile.cs:46,52-77`, `BotConfig.cs:197-260` |
| Locked team-map access | race between web writes and damage-event reads | `BotConfig.cs:73-130` |
| Input clamps and tri-state field reads (all POST fields, config `Normalize`) | TB2 DoS/value abuse | `WebApi.cs:189-386,498`, `RequestFields.cs:38-110`, `BotConfig.cs:266-336` |
| Bounded idempotency ledger + key validation + eviction warning | retry storms, unbounded memory, silent dedup loss (G6) | `IdempotencyLedger.cs:34,38,41,103-105,174-193`, `WebApi.cs:79-80,137-142` |
| Main-thread dispatch + 15 s timeout + claim-on-timeout + abandoned-dispatch warning | TB5 crash/double-exec/lost response | `WebApi.cs:76-78,390-401,472-478`, `MainThreadDispatch.cs:47-98` |
| Weight structural validation (version, shape, length, NaN/Inf) + fuzz suites | TB4 malformed artifacts | `BotNeuralBrain.cs:184-289`, `tests/BotMod.Tests/BotNeuralBrainFuzzTests.cs` |
| `spawnpoints.xml` parsed with DTD processing off and a null resolver | TB3 XXE / entity-expansion DoS from a crafted world file | `Source/BotMod/Core/BotSpawner.cs:303-308` |
| Text-node rendering (React children), no raw HTML sink in the bundle | TB7 XSS into the admin session | `Source/BotMod/WebMod/bundle.ts:692,424,521,593` |

Threats with no mitigation, ranked by exploitability x impact: G1 and G4
(authenticated, but a single credential away from unauthenticated remote control
and of a server-wide stall), then G2 (unauthenticated but opt-in), then G8
(authenticated, forensic-only impact), then G6 and G7 (narrow triggers).
G5 is an accepted operator-boundary risk, not an open finding.

Single points of failure: the stock webserver credential is the one control
carrying every TB2 threat; the game main thread is the one resource every
mutation and every tick shares; `PersistGate` is the one lock that keeps the
config file coherent across the web, console, and reload surfaces.

Documentation claims checked against code this pass: README's "authenticated
GET/POST /api/bot ... permission level 0" matches `WebApi.cs:426`; the
auth-bypass description matches `BotPatches.cs:21-38`, and README's statement
that the bypass log carries the entity id only, not the Steam id or client IP,
matches `BotPatches.cs:42-47`. Two claims this document itself had wrong were
corrected rather than kept: the per-join bypass line was described as recording
the peer IP (it records the in-world entity id, by deliberate design, per
`BotPatches.cs:42-46`), and an earlier pass claimed `SECURITY.md` named 0.4.0
as the supported release and cited `WebApi.cs:298` for the permission-level
declaration; `SECURITY.md` now names 0.7.1 (`Source/BotMod/Core/BotModVersion.cs:10`,
mirrored by `Source/BotMod/ModInfo.xml:7`) and cites `WebApi.cs:426`. No
remaining contradicted claim found.

## Response readiness (note only)

Mutation audit lines cover success, replay, rejection, and failure outcomes
with durations and a request tag, giving investigators a per-action trail;
status polling is unlogged by design. Two silent failure modes of the web
plumbing are also surfaced to the server log: a main-thread dispatch that ran
after its caller timed out (`WebApi.cs:76-78`) and idempotency-ledger capacity
evictions (`WebApi.cs:79-80`). What is still untraced: the game's own auth
decisions for non-synthetic joins; and `bot` console/telnet commands, which
produce no mod audit line and no sender identity (G8). There is no documented
vulnerability-report-to-fix path: `SECURITY.md` states that absence explicitly;
defining the process is an organizational decision.
