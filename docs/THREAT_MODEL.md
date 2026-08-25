# Threat Model - BotMod

Systemic view of what this mod exposes to attack, what it costs if broken, and
what stands in the way. Derived from code and deployment artifacts on `main`
(last reviewed 2026-08-23, commit c01c074; WebApi.cs line refs re-anchored 2026-08-26, mod version 0.4.0). Every claim
carries a file reference so the next review can re-verify it.

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
| G2 | Opt-in auth bypass (`AllowSyntheticAuthBypass=true`) lets anyone who can reach the server port join with a predictable synthetic Steam id, without owning the game. Controls: default off (`Source/BotMod/Config/BotConfig.cs:17`) and the `AuthBypass=` startup log line (`Source/BotMod/ModApi.cs:31`). | TB1 | Documented residual (README Install) |
| G3 | Audit line appends the raw response body, which embeds request-supplied identifiers and world player names that never pass `LogSanitizer`; player-controlled text crosses into the audit trail unsanitized. Location: `Source/BotMod/Web/WebApi.cs:384` fed from `:217`, `:255`, `:319`. | TB6 -> TB1 | Threat recorded; fix belongs to sec-review |
| G4 | No mod-layer rate limit or quota on `/api/bot`: each call is clamped, but aggregate calls (mutations and the every-5s-per-session status poll, `Source/BotMod/Web/WebApi.cs:496`) are unbounded. | TB2 | Named gap |
| G5 | Operator-trusted files (`botmod.json`, neural weights, `characters.json`) are parsed without integrity verification; weights get structural validation only (`Source/BotMod/AI/BotNeuralBrain.cs:188-207`). | TB3/TB4 | Accepted risk (operator boundary) |
| G6 | Idempotency ledger eviction (oldest-first, capacity 256) can drop an active claim under key churn, allowing a late duplicate to execute twice. Admin-only trigger. `Source/BotMod/Web/IdempotencyLedger.cs:106-117`. | TB2 | Named gap, low |

## Assets and impact

- **A1 Dedicated server availability** - the game main thread is the chokepoint;
  the code records that touching Unity/world state off it segfaulted the server
  (`Source/BotMod/Web/WebApi.cs:169-171`). Loss: whole server down.
- **A2 Game-world fairness** - bots shoot players; admins can retarget them at a
  specific player (`spawnNear`, `vs player`). Loss: griefing at scale, PvP
  balance destroyed.
- **A3 Operator config integrity** - `Mods/BotMod/Config/botmod.json` holds every
  persisted admin decision; a torn write resets state to defaults
  (`docs/recovery.md`). Protected by atomic write + `.bak`
  (`Source/BotMod/Config/AtomicTextFile.cs`, fallback in `BotConfig.Load`,
  `Source/BotMod/Config/BotConfig.cs:162-181`).
- **A4 Audit trail integrity** - one log line per executed/replayed/rejected
  mutation is the investigation record (`Source/BotMod/Web/WebApi.cs:104-111`).
  Loss: repudiation, hidden actions.
- **A5 Player identity data** - online player names + entity ids served by
  `GET /api/bot` (`BuildStatus`, `Source/BotMod/Web/WebApi.cs:432-519`).
  Exposure limited to permission-0 holders.
- **A6 Admin browser session** - the dashboard runs in the admin's browser
  against the stock webserver; server-controlled strings render through React
  (`Source/BotMod/WebMod/bundle.ts:509`), which escapes text by default; no
  `dangerouslySetInnerHTML`/`document.write` anywhere in the bundle.
- **Secrets** - the mod holds none. Admin web tokens and telnet credentials live
  in the game's own configuration, outside this repository.

## Trust boundaries

- **TB1 Player/game client <-> dedicated server (network).** The mod widens
  vanilla Steam auth exactly once: `Patch_SteamAuthServer_SyntheticBypass`
  auto-passes ids 76561199000000000..76561199000010000 when
  `AllowSyntheticAuthBypass=true` (`Source/BotMod/Patches/BotPatches.cs:11-33`).
  Deployments running code mods have EAC disabled anyway (README, Install).
- **TB2 Admin browser <-> stock webserver <-> BotMod REST API.**
  `GET/POST /api/bot` (`Source/BotMod/Web/WebApi.cs:72,94`) is discovered by the
  game's webserver as an `AbsRestApi` subclass; the mod declares permission
  level 0 for all methods (`WebApi.cs:389`) and performs **no** authentication,
  authorization, or rate limiting of its own. Enforcement point is entirely in
  game-owned code/config.
- **TB3 Operator filesystem <-> mod.** Config (`BotConfig.Load`),
  characters (`BotCharacterDB.Load`, `Source/BotMod/ModApi.cs:29`), world XML
  (`entityclasses.xml` injection, `ModApi.cs:56`; `spawnpoints.xml` read at
  spawn) are trusted as operator-controlled and parsed at load/reload.
- **TB4 Build/GA artifacts <-> runtime.** `evolved/best.json` is promoted via
  git commit (`evolved/README.md`, see `docs/recovery.md`) and loaded at
  startup, reload, or `neural on` (`Source/BotMod/AI/BotNeuralBrain.TryLoad`).
  Build-to-runtime trust: whatever lands in the installed mod dir is executed
  as data driving combat decisions.
- **TB5 Web thread pool <-> game main thread.** Every world-touching action is
  marshaled via `RunOnMain` -> `MainThreadDispatch.Execute` with a 15 s timeout
  (`Source/BotMod/Web/WebApi.cs:396-401`, `Source/BotMod/Web/MainThreadDispatch.cs`).
  This is a privilege transition: queued work still runs after a dispatch
  timeout, which the error path handles by keeping the idempotency claim
  (`WebApi.cs:352-369`).
- **TB6 Mod <-> server log.** Request-derived strings are scrubbed before
  logging (`LogSanitizer.Clean`, `Source/BotMod/Config/LogSanitizer.cs:40-53`,
  applied at `WebApi.cs:110-111`); see G3 for what escapes it.
- **TB7 Server data <-> admin browser.** Status JSON renders player/bot names in
  the dashboard (React default escaping, asset A6).

## Entry points

| Entry point | Untrusted input | Reference |
|---|---|---|
| `GET /api/bot` | none (read-only status) | `Source/BotMod/Web/WebApi.cs:72` |
| `POST /api/bot` | `action`, `requestId`, `count`, `player`, `weapon`, `entityId`, `level`, `target`, `name`, `team`, `on` fields | `Source/BotMod/Web/WebApi.cs:94-346` |
| Console command `bot` (console/telnet) | subcommand args incl. player name/id lookups | `Source/BotMod/Commands/BotConsoleCommands.cs:30-57` |
| `Config/botmod.json` (+ `.bak`) | full config object; unknown keys warned | `Source/BotMod/Config/BotConfig.cs:152-196` |
| `evolved/best.json` weights | version/inputs/shape validated, length-checked | `Source/BotMod/AI/BotNeuralBrain.cs:159-215`; fuzz: `tests/BotMod.Web.Tests/BotNeuralBrainFuzzTests.cs` |
| `characters.json`, `entityclasses.xml`, `spawnpoints.xml` | game/XML data | `Source/BotMod/ModApi.cs:29,56`; `Source/BotMod/Core/BotSpawner.cs` |
| Harmony hooks | game-call surfaces: `DamageEntity`, `OnEntityDeath`, `AuthenticateUser`, `listplayers` | `Source/BotMod/Patches/BotPatches.cs:11,38,72,91` |
| Mod events | `GameStartDone`, `GameUpdate`, `WorldShuttingDown` | `Source/BotMod/ModApi.cs:42-49` |

Nothing listed here lacks a named validation point except where noted (G3:
response-embedded names; G4: no aggregate quota). Inputs treated as trusted from
outside a boundary: config and weight files (operator boundary, TB3/TB4), and
world player names echoed into responses/logs (G3).

## Threats per boundary (STRIDE, tied to code)

**TB1 (client <-> server)**
- *Spoofing/EoP:* forged synthetic Steam id joins without owning the game when
  the bypass flag is on; range is fixed and documented
  (`BotPatches.cs:24-27`). Each grant is logged (`BotPatches.cs:27`). See G2.
- *Tampering/repudiation:* vanilla cheating - owned by the game/EAC layer, out
  of scope.
- *DoS:* connection floods - game-owned; the mod adds entity load only after an
  authorized spawn.

**TB2 (admin -> API)**
- *Spoofing/EoP (SPOF, G1):* one control - webserver token authn + level-0
  declaration (`WebApi.cs:389`). No second gate in mod code.
- *Tampering:* concurrent persists interleaving - mitigated: serialized by
  `PersistGate` (`Source/BotMod/ModApi.cs:157-175`); torn writes recovered from
  `.bak` (`BotConfig.cs:162-181`).
- *Repudiation:* every executed/replayed/rejected mutation logs one sanitized
  line (`WebApi.cs:114,124,131,365,384`); GET polling deliberately unlogged
  (`WebApi.cs:104-105`) - acceptable volume tradeoff, noted for investigators.
- *Information disclosure:* exception type/message/stack suppressed from
  responses; generic 500 envelope only (`WebApi.cs:365-369`), detail to log.
- *DoS:* body-size limits are game-owned; per-call clamps everywhere
  (spawn count 1..16 `WebApi.cs:168,193,413-420`; team 0..BotTeamCount `:311`;
  skill 0..4 `:269`; global bot ceiling 64 via `Normalize`
  `BotConfig.cs:199-200`); ledger capped at 256 keys, 128-char keys, 10 min
  retention (`IdempotencyLedger.cs:26-30`); dispatch timeout 15 s
  (`WebApi.cs:396-401`). Aggregate rate: unbounded (G4).

**TB3/TB4 (files/artifacts -> runtime)**
- *Tampering/EoP:* hand-edited or substituted config/weights change bot behavior
  (damage filters, target selection). Mitigations: unknown-key warnings
  (`BotConfig.cs:187-196`), full value clamping (`Normalize`,
  `BotConfig.cs:197-241`), weights rejected on version/input-count/length
  mismatch (`BotNeuralBrain.cs:188-207`) plus fuzz suites. Residual: no
  signature/integrity check (G5) - accepted because the source is the operator.

**TB5 (web thread -> main thread)**
- *DoS/crash:* wrong-thread world access historically segfaulted the dedi
  (`WebApi.cs:169-171`); mitigated by mandatory `RunOnMain` marshaling and the
  ambiguous-timeout rule preventing double execution
  (`WebApi.cs:352-369`, `MainThreadDispatch.cs`).

**TB6 (output -> audit log)**
- *Repudiation/tampering:* log forging via CR/LF, terminal escapes, bidi/zero-
  width controls - mitigated for `action`/`requestId` (`WebApi.cs:110-111`);
  **not** mitigated for response-embedded player/request text (G3).

## Abuse cases

- **Hostile-but-authenticated admin (griefing at scale).** An authenticated
  permission-0 user can aim bots at a chosen player (`POST /api/bot`
  `action=spawnNear`, `WebApi.cs:182-218`) and keep them there across respawns,
  or flip `vs player on`. Bounded only by MaxBots <= 64. This is the tool's
  intended power; the mitigation is webserver credential hygiene (game-owned),
  not mod code.
- **Network peer joins without owning the game.** With
  `AllowSyntheticAuthBypass=true`, any reachable peer presents an id in
  76561199000000000..10000 and authenticates (`BotPatches.cs:24-27`). Enabling
  scenario is documented in README (Install). Residual risk accepted by config.
- **Dedup exhaustion.** An authenticated caller submits many distinct
  `requestId`s; at 256 live entries the oldest claims are evicted
  (`IdempotencyLedger.cs:106-117`), so a retried spawn inside the window may
  execute twice. Impact: duplicate bots up to the hard cap; no crash path.
- **Client-side enforcement:** none relied upon. Every dashboard-controllable
  value is re-clamped server-side (clamps cited above); the bundle is display +
  submit only.

## Mitigation-to-threat map (existing controls)

| Control | Covers | Reference |
|---|---|---|
| Webserver authn + permission level 0 declaration | all TB2 spoofing/EoP (sole gate, G1) | `Source/BotMod/Web/WebApi.cs:389` |
| Deny-side matrix tests: web API method levels and console default level pinned to 0 | TB2 gate regression (widened declaration fails `make test`) | `tests/BotMod.Web.Tests/WebApiAuthzTests.cs` |
| Bypass flag default-off + startup visibility | TB1 spoofing blast radius (G2) | `BotConfig.cs:17`, `ModApi.cs:31` |
| Per-join bypass logging | TB1 attribution | `BotPatches.cs:27` |
| Sanitized audit fields, generic 500s | TB6 forgery, TB2 disclosure | `WebApi.cs:110-111,365-369`, `LogSanitizer.cs` |
| Serialized atomic persists + `.bak` recovery | A3 tampering/durability | `ModApi.cs:157-175`, `AtomicTextFile.cs`, `BotConfig.cs:162-181` |
| Locked team-map access | race between web writes and tick reads | `BotConfig.cs:64-107` |
| Input clamps (all POST fields, config Normalize) | TB2 DoS/value abuse | `WebApi.cs:154-346,413-420`, `BotConfig.cs:197-241` |
| Bounded idempotency ledger | retry storms, unbounded memory | `IdempotencyLedger.cs` |
| Main-thread dispatch + 15 s timeout + claim-on-timeout | TB5 crash/double-exec | `WebApi.cs:352-369,396-401` |
| Weight structural validation + fuzz suites | TB4 malformed artifacts | `BotNeuralBrain.cs:159-215`, `tests/BotMod.Web.Tests/` |
| React default escaping (no raw HTML sinks) | TB7 XSS into admin session | `Source/BotMod/WebMod/bundle.ts` |

Documentation claims checked against code this pass: README's "authenticated
GET/POST /api/bot ... permission level 0" matches `WebApi.cs:389`;
"admin login required" for the dashboard matches the stock-webserver pattern;
the auth-bypass description matches `BotPatches.cs`. No contradicted claim
found.

## Response readiness (note only)

Mutation audit lines cover success, replay, rejection, and failure outcomes
with durations, giving investigators a per-action trail; status polling is
unlogged by design. There is no documented vulnerability-report-to-fix path:
`SECURITY.md` (created alongside this model) states that absence explicitly;
defining the process is an organizational decision.
