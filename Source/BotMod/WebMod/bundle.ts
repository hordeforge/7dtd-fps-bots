// BotMod WebMod (TypeScript source), compiled to bundle.js by
// `tsc -p Source/BotMod/WebMod/tsconfig.json` (wired into scripts/build.sh).
// The dashboard loads /webmods/BotMod/bundle.js and reads window["BotMod"]:
// the "Bot" route is a direct sidebar entry (hidden until the sid session
// cookie is present). Do not hand-edit bundle.js; regenerate from this file.
//
// The whole body is an IIFE on purpose: webmod bundles are plain <script> tags
// sharing the global scope, and a bare top-level const (e.g. modId) collides
// across mods (SyntaxError kills the later bundle's registration).
//
// Lint: scripts/lint-webui.sh (tsc --strict + oxlint with the anti-slop +
// strict rule set in .oxlintrc.jsonc, plus a bundle freshness gate).
((): void => {

const modId = "BotMod";

const POLL_INTERVAL_MS = 5000;
const ARM_TIMEOUT_MS = 4000;
// A POST that never settles (a proxy holding the connection open) would leave
// `busy` set, and every control in the panel stays disabled until a full page
// reload. Give up on the callback after this long and let the user act again.
const COMMAND_TIMEOUT_MS = 10_000;
// The spawn count range the two number inputs declare (min/max) and the server
// clamps to (WebApi.OptCount). Kept in one place so the input, the posted
// value, and the "Spawn N bots: done" result line cannot disagree.
const SPAWN_COUNT_MIN = 1;
const SPAWN_COUNT_MAX = 16;
// The class names the control rows repeat, declared once so the minified
// bundle emits a one-letter reference instead of the string. The webserver
// serves bundle.js uncompressed under a 14 KiB budget, and each of these
// appears in five or more places.
const BTN = "botmod-btn";
const PRIMARY = "botmod-primary";
const DANGER = "botmod-danger";
const WINDOW = "botmod-window";
const ROW = "botmod-row";

// Dashboard-injected props (kl wrapper passes the stock React, an axios-ish
// HTTP client, and the react-query useQuery hook). The payload is untyped
// runtime JSON, so it is read through the shape guards below.
type CreateElement = (...args: Array<unknown>) => unknown;
type QueryResult = {
  data?: unknown;
  isError?: boolean;
  error?: { response?: { status?: number } };
  refetch?: () => Promise<unknown>;
};
type PanelProps = {
  React: {
    createElement: CreateElement;
    useState: <T>(init: T) => [T, (v: T | ((prev: T) => T)) => void];
    useEffect: (fn: () => unknown, deps?: Array<unknown>) => unknown;
  };
  HTTP: { get: (url: string) => Promise<unknown>; post: (url: string, body?: unknown) => Promise<unknown> };
  useQuery: (key: string, fn: () => Promise<unknown>, opts?: {
    refetchInterval?: number;
    enabled?: boolean;
    retry?: boolean;
  }) => QueryResult;
};

type BotStat = {
  name: string;
  entityId: number;
  team?: number;
  weapon: string;
  status: string;
  health: number;
  deaths: number;
  zombies: number;
  players: number;
  score: number;
  level: number;
  nearestPlayer?: string;
  nearestPlayerDist?: number;
};
type BotPlayer = {
  name: string;
  entityId: number;
};
type BotStatus = {
  enabled?: boolean;
  targetBotCount?: number;
  maxBots?: number;
  alive?: number;
  difficulty?: number;
  neural?: boolean;
  neuralLoaded?: boolean;
  neuralPath?: string;
  visionRange?: number;
  attackRange?: number;
  spawnRadius?: number;
  strafeChance?: number;
  dodgeOnHitChance?: number;
  botVsBot?: boolean;
  botVsZombie?: boolean;
  botVsPlayer?: boolean;
  botTeam?: boolean;
  teamCount?: number;
  botHealth?: number;
  players?: Array<BotPlayer>;
  bots?: Array<BotStat>;
};
type BotAction = {
  action: string;
  count?: number;
  entityId?: number;
  level?: number;
  player?: string;
  weapon?: string;
  target?: string;
  name?: string;
  team?: number;
  on?: boolean;
  requestId?: string;
};
type SortState = { key: string; dir: number };
// Result of the last command: what was sent, and whether the server took it.
type CommandStatus = { text: string; bad: boolean };
type WebModContract = {
  about: string;
  routes: Record<string, unknown>;
  settings: Record<string, unknown>;
  mapComponents: Array<unknown>;
};

// The dashboard HTTP wrapper may hand us the axios response, the {data: ...}
// envelope, or the bare payload; accept all three. The payload is untyped
// runtime JSON, so the envelope unwrap is the boundary parse. One unwrap for
// the polled status and the POST result, so the two cannot disagree on where
// the server's fields live.
function unwrapData(o: unknown): Record<string, unknown> {
  if (typeof o !== "object" || o === null) {
    return {};
  }
  // oxlint-disable-next-line typescript/no-unsafe-type-assertion -- deliberate: untyped JSON payload boundary; SAFETY: the typeof guard above proves the runtime value is a non-null object
  let cur = o as Record<string, unknown>;
  // Two unwraps: the axios response, then the {data: ...} envelope inside it.
  for (let depth = 0; depth < 2; depth++) {
    const next = cur.data;
    if (typeof next !== "object" || next === null) {
      break;
    }
    // oxlint-disable-next-line typescript/no-unsafe-type-assertion -- deliberate: untyped JSON payload boundary; SAFETY: the typeof guard in the loop head proves next is a non-null object
    cur = next as Record<string, unknown>;
  }
  return cur;
}

function numOr(v: unknown, fallback: number): number {
  return typeof v === "number" && Number.isFinite(v) ? v : fallback;
}

const num = (v: unknown): number => numOr(v, 0);

// Output formatting, for text the viewer reads. Raw `String(v)` bakes in
// en-US digits and grouping, which a viewer running under ar-EG (Arabic-Indic
// digits) or de-DE (comma grouping) reads as foreign. These follow the runtime
// locale instead. They are for display only: a posted count, an <input value>
// and a <select value> are protocol tokens and keep the plain String(v) form,
// so a localized digit never reaches the server.
const COUNT_FORMAT: Intl.NumberFormat = new Intl.NumberFormat();

// Distances are in game world units, which are meters in every server build:
// the locale changes how the number and the unit are written, never what is
// measured, so the unit is pinned and only the number is localized.
const METER_FORMAT: Intl.NumberFormat = new Intl.NumberFormat(undefined, { style: "unit", unit: "meter" });

// Poll and command timeouts are wall-clock seconds in every deployment, so
// the same reasoning as the meter applies: the unit is pinned, and the
// formatter supplies the digits, the abbreviation and its spacing. A hardcoded
// "5s" or "every 5 seconds" reads as foreign text under an ar-EG or ja-JP
// viewer.
const SECONDS_FORMAT: Intl.NumberFormat = new Intl.NumberFormat(undefined, { style: "unit", unit: "second" });

// The chance knobs arrive as fractions, not as already-scaled percents: the
// percent style does the scaling itself and supplies the locale's digits,
// separator and sign placement ("35%" in en-US, "35 %" in de-DE), so the panel
// never hardcodes the "35%" shape.
const PERCENT_FORMAT: Intl.NumberFormat = new Intl.NumberFormat(undefined, { style: "percent" });

// Plural category for n in the viewer's locale. `n === 1` is an English rule:
// Polish has one/few/many, Arabic zero/one/two/few/many/other, and the category
// is what picks the noun form. The panel's English labels only need one and
// other, so the selection collapses to that here; a translated label set adds
// the categories its language defines, keyed off the same call.
const PLURAL_RULES: Intl.PluralRules = new Intl.PluralRules();

// The two formatters are the only way a number reaches the screen, and every
// call site goes through them rather than naming the Intl object: the shipped
// bundle has a wire budget, and spelling out COUNT_FORMAT.format at each of the
// scoreboard and status-line call sites is bytes the panel pays on every load.
const formatCount = (n: number): string => COUNT_FORMAT.format(n);
const formatMeters = (n: number): string => METER_FORMAT.format(n);
const formatPercent = (fraction: number): string => PERCENT_FORMAT.format(fraction);
const formatSeconds = (n: number): string => SECONDS_FORMAT.format(n);

function isSingular(n: number): boolean {
  return PLURAL_RULES.select(n) === "one";
}

// A scoreboard cell whose field the server left out renders empty, as the
// bare `undefined` child did, instead of a formatted 0: a missing reading and
// a real zero must not read the same. A zero the server did send is formatted.
function fmtCell(v: number | undefined): string | undefined {
  return v === undefined ? undefined : formatCount(v);
}

function strOrEmpty(v: unknown): string {
  return typeof v === "string" ? v : "";
}

function listOrEmpty<T>(candidate: unknown): Array<T> {
  if (Array.isArray(candidate)) {
    // oxlint-disable-next-line typescript/no-unsafe-type-assertion -- deliberate: untyped JSON payload boundary; SAFETY: Array.isArray is the runtime proof for the element cast
    return candidate as Array<T>;
  }
  return [];
}

// Clamp into SPAWN_COUNT_MIN..SPAWN_COUNT_MAX rather than only guarding the
// low end: the server clamps a posted count the same way, so an unclamped
// send would spawn 16 while the result line announced the number typed.
function toCount(v: string): number {
  const n = Number.parseInt(v, 10);
  if (!Number.isFinite(n) || n < SPAWN_COUNT_MIN) {
    return SPAWN_COUNT_MIN;
  }
  return Math.min(n, SPAWN_COUNT_MAX);
}

// One idempotency key per logical command (per click): the server records the
// first response under this key, so a retried POST (lost response after the
// server acted, proxy retry) replays it instead of spawning twice. The key
// must not repeat across clicks, so the fallback stays on the CSPRNG:
// Math.random() is seeded per context and its output is predictable, which
// would let a retried request collide with (and replay) a different click's
// response.
function newRequestId(): string {
  const c: Crypto | undefined = typeof crypto === "undefined" ? undefined : crypto;
  // Feature presence through the optional chain, not
  // `c !== undefined && typeof c.x === "function"`: the bundle has a hard wire
  // budget and the same answer spelled out is bytes the panel pays on every
  // load. The call itself stays unguarded so it keeps the getRandomValues
  // overload that types the result as the Uint8Array it always returns.
  if (c?.randomUUID !== undefined) {
    return c.randomUUID();
  }
  if (c?.getRandomValues !== undefined) {
    const bytes = c.getRandomValues(new Uint8Array(16));
    let hex = "";
    for (const b of bytes) {
      hex += b.toString(16).padStart(2, "0");
    }
    return `botmod-${hex}`;
  }
  return `botmod-${Date.now().toString(36)}-${Math.floor(Math.random() * 2 ** 32).toString(36)}`;
}

function botCount(count: number | undefined): string {
  const n = numOr(count, 1);
  return `${formatCount(n)} ${isSingular(n) ? "bot" : "bots"}`;
}

// Plain-language name of a command, for the command-result line. The button
// labels are short ("Spawn", "+4"), which does not tell the user afterwards
// which of them ran.
function actionLabel(body: BotAction): string {
  switch (body.action) {
    case "spawn":
      return `Spawn ${botCount(body.count)}`;
    case "spawnNear":
      return `Spawn ${botCount(body.count)} near ${strOrEmpty(body.player) === "" ? "a player" : strOrEmpty(body.player)}`;
    case "remove":
      return "Remove all";
    case "removeOne":
      return "Remove bot";
    case "enable":
      return "Enable bots";
    case "disable":
      return "Disable bots";
    case "skill":
      return `Skill ${formatCount(num(body.level))}`;
    case "neural":
      return body.on === true ? "GA brain on" : "GA brain off";
    case "team":
      return body.on === true ? "Squad mode on" : "Squad mode off";
    case "vs":
      return `Shoot at ${strOrEmpty(body.target)}: ${body.on === true ? "on" : "off"}`;
    case "setTeam":
      return `${strOrEmpty(body.name)} to ${teamLabel(body.team)}`;
    case "teamCount":
      return `Teams: ${formatCount(num(body.count))}`;
    case "clearTeams":
      return "Clear teams";
    default:
      return body.action;
  }
}

// The server names every rejection it returns (meta.errorCode in the stock
// webserver error envelope), and the panel's HTTP client leaves that body on
// the rejected promise. Walk the unknown error to that one string so a refused
// command says which field was wrong instead of a blanket "rejected".
function rejectionCode(err: unknown): string {
  let cur: unknown = err;
  for (const key of ["response", "data", "meta", "errorCode"]) {
    if (typeof cur !== "object" || cur === null) {
      return "";
    }
    // oxlint-disable-next-line typescript/no-unsafe-type-assertion -- deliberate: untyped HTTP error at the panel boundary; SAFETY: the typeof guard on the line above proves cur is a non-null object
    cur = (cur as Record<string, unknown>)[key];
  }
  return strOrEmpty(cur);
}

// What the server actually did, for the actions that answer with a count or a
// flag. A 200 is not a success by itself: `spawnNear` answers
// {"spawned":0,"found":false} for a player who left between the poll and the
// click, `spawn` answers {"spawned":0} once MaxBots is reached, `removeOne`
// answers {"removed":false} for an id that is already gone, and `neural`
// answers {"loaded":false,"reason":"..."} when the weights file did not load.
// Announcing "done" for those told the operator the command worked when
// nothing changed. The other actions answer with the value they applied and
// carry none of these fields, so they fall through to the plain "done".
function outcome(body: BotAction, label: string, response: unknown): { text: string; bad: boolean } | null {
  const d = unwrapData(response);
  if (body.action === "neural" && body.on === true && d.loaded === false) {
    return { text: `${label}: failed, weights not loaded (${strOrEmpty(d.reason)}).`, bad: true };
  }
  let why = "";
  if (d.found === false) {
    why = "that player is offline";
  } else if (d.removed === false) {
    why = "that bot is gone";
  } else if (d.spawned === 0) {
    why = "the bot cap is reached";
  } else if (d.spawned === undefined) {
    return null;
  }
  if (why !== "") {
    return { text: `${label}: failed, ${why}.`, bad: true };
  }
  const n = numOr(d.spawned, 0);
  const want = numOr(body.count, 1);
  return n < want
    ? { text: `${label}: partial, ${formatCount(n)}/${formatCount(want)} spawned.`, bad: true }
    : { text: `${label}: done, ${formatCount(n)} spawned.`, bad: false };
}

// A command whose response never arrived is not a command that did not run:
// the server handed it to the main thread and may still act on it, which is
// why the watchdog below reports an unknown outcome rather than a failure. A
// click after that is a retry, and a retry is only deduped when it carries the
// requestId the first attempt used. The server's ledger
// (Source/BotMod/Web/IdempotencyLedger.cs) binds a key to the request it was
// first used for; a fresh key looks like a second, unrelated request, so the
// action runs again and one click spawns twice.
//
// One string, NUL-separated, because the bundle has a hard wire budget
// (scripts/lint-webui.sh) and a second variable with two property names is
// bytes the panel pays on every load.
let unknownRequest = "";

// The entry is consumed by the next send, whatever it is, so a key is only
// ever reused by the immediately following click. That bounds how long a stale
// key can sit here without a second copy of the server's retention window to
// keep in step, and a deliberate command in between is itself reason enough to
// stop replaying the earlier one.

// Fire a bot command. The 5s poll shows the real state after the call, so the
// result line carries the outcome: a rejected POST is reported instead of the
// button silently springing back, and a 200 that reports nothing done is too
// (see outcome above). The result line is its own live region, so the outcome
// reaches assistive tech as well as sighted users.
function postAction(opts: {
  HTTP: PanelProps["HTTP"];
  busy: boolean;
  setBusy: (v: boolean) => void;
  setArmed: (v: string) => void;
  setStatus: (v: CommandStatus) => void;
  refetch: () => Promise<unknown>;
  body: BotAction;
}): void {
  if (opts.busy) {
    return;
  }
  const label = actionLabel(opts.body);
  opts.setBusy(true);
  opts.setArmed("");
  // The result line appears at once, so a slow or stuck request leaves a
  // "sending" note instead of only dimmed controls.
  opts.setStatus({ text: `${label}: sending...`, bad: false });
  // Same command as the one whose outcome is still unknown: reuse its key, so
  // the server replays the recorded answer (or answers 409 while the first
  // attempt is still in flight) instead of executing the action a second time.
  // The body decides identity: every control builds its body the same way from
  // the same state, so a repeat of one click stringifies identically while a
  // different count, player, target or team does not and gets a new key. The
  // requestId is not in the body yet, so this is the string the server
  // fingerprints (it drops requestId from the same body).
  const sent = `${JSON.stringify(opts.body)}\0`;
  const requestId = unknownRequest.startsWith(sent) ? unknownRequest.slice(sent.length) : newRequestId();
  unknownRequest = ""; // this send consumes the entry, whatever it is
  const body: BotAction = { ...opts.body, requestId };
  // The watchdog and the response race the same `busy` flag, so the loser must
  // stay silent: an answer that lands after the timeout must not clear the busy
  // flag of whatever command the user started meanwhile. The refetch it would
  // trigger is still wanted, so only the state writes are dropped.
  let abandoned = false;
  const giveUp = setTimeout((): void => {
    abandoned = true;
    // The outcome is unknown, not failed, so a repeat of this same command has
    // to carry the same key (see unknownRequest).
    unknownRequest = sent + requestId;
    opts.setBusy(false);
    opts.setStatus({ text: `${label}: no answer after ${formatSeconds(COMMAND_TIMEOUT_MS / 1000)}. It may still run; a repeat will not run it twice.`, bad: true });
    void opts.refetch();
  }, COMMAND_TIMEOUT_MS);
  const settle = (report: CommandStatus): void => {
    clearTimeout(giveUp);
    if (abandoned) {
      return;
    }
    opts.setStatus(report);
    opts.setBusy(false);
  };
  void opts.HTTP.post("/api/bot", body)
    .then((response: unknown): void => {
      const done = outcome(opts.body, label, response);
      settle(done ?? { text: `${label}: done`, bad: false });
      void opts.refetch();
    })
    .catch((error: unknown): void => {
      const code = rejectionCode(error);
      settle({ text: code === "" ? `${label}: failed, the server rejected the command.` : `${label}: rejected, ${code}.`, bad: true });
    });
}

// `off` disables the control for a reason other than a command in flight
// (the Spawn near inputs have no player to target). A destructive control
// passes `arm`: its first click arms it and the second one runs it, with the
// arm step announced through `say` because the only visual cue is the label
// change. Arming is per-control, so one `armed` label is all the state
// needed to tell a disarmed control from an armed one, and the armed label
// repeats the action it confirms (a bare "Confirm?" left the user reading a
// question, with the destructive verb off screen, right above buttons that
// also disarm on a second click).
function makeBtn(h: CreateElement, busy: boolean, post: (body: BotAction) => void, arm?: { armed: string; setArmed: (v: string | ((prev: string) => string)) => void; say: (v: string) => void }): (label: string, body: BotAction, cls?: string, off?: boolean) => unknown {
  return (label: string, body: BotAction, cls?: string, off?: boolean): unknown => {
    const isArmed = arm?.armed === label;
    return h("button", {
      className: `${BTN}${cls === undefined ? "" : ` ${cls}`}${isArmed ? " botmod-armed" : ""}`,
      disabled: busy || off === true,
      onClick: (): void => {
        if (arm === undefined || arm.armed === label) {
          post(body);
          return;
        }
        arm.setArmed(label);
        arm.say(`${label} armed, activate again within ${ARM_TIMEOUT_MS / 1000} seconds to confirm`);
        setTimeout((): void => arm.setArmed((a: string) => (a === label ? "" : a)), ARM_TIMEOUT_MS);
      }
    }, isArmed ? `Confirm ${label}?` : label);
  };
}

// Locale-aware collation for the name and weapon columns. Code-unit `<` on a
// toLowerCase()d string orders by Unicode scalar, so "Ähne" sorts after "Zulu"
// and an NFD-decomposed name compares against nothing; a collator orders by the
// viewer's collation (accent-insensitive, case-insensitive) and `numeric`
// keeps "Bot 2" ahead of "Bot 10". A collator is locale-sensitive, so build it
// once and reuse it: constructing one per comparison is a per-row cost on a
// list that re-sorts on every 5 s poll.
const TEXT_COLLATOR: Intl.Collator = new Intl.Collator(undefined, { sensitivity: "base", numeric: true });

function bySortKey(sort: SortState): (a: BotStat, b: BotStat) => number {
  return (a, b): number => {
    const textKey = sort.key === "name" || sort.key === "weapon";
    if (textKey) {
      // SAFETY: bot rows are read from the untyped JSON payload; sort.key is a known column of the same rows
      const av = strOrEmpty((a as Record<string, unknown>)[sort.key]);
      // SAFETY: same keyed access as av, on the other row
      const bv = strOrEmpty((b as Record<string, unknown>)[sort.key]);
      return -sort.dir * TEXT_COLLATOR.compare(av, bv);
    }
    // SAFETY: same keyed access as av, on the other row
    const av = numOr((a as Record<string, unknown>)[sort.key], -1);
    // SAFETY: same keyed access as av, on the other row
    const bv = numOr((b as Record<string, unknown>)[sort.key], -1);
    if (av < bv) {
      return -sort.dir;
    }
    return av > bv ? sort.dir : 0;
  };
}

function brainLabel(neural: boolean | undefined, neuralLoaded: boolean | undefined): string {
  if (neural !== true) {
    return "static AI";
  }
  return neuralLoaded === true ? "GA" : "GA (not loaded)";
}

function nearLabel(b: BotStat): string {
  if (b.nearestPlayerDist === undefined || b.nearestPlayerDist < 0) {
    return "n/a";
  }
  return `${formatMeters(b.nearestPlayerDist)}${b.nearestPlayer === undefined ? "" : ` ${b.nearestPlayer}`}`;
}

// Team palette: index 0 = free-for-all (neutral), 1..TEAM_LIMIT team colors.
// Kept in sync with the buckets, row dots, chips, and per-row selects.
const TEAM_LIMIT = 8;
const TEAM_COLORS: ReadonlyArray<string> = [
  "#9aa0a6", "#ff7070", "#8ab4f8", "#57d977", "#f9ab00", "#c58af9", "#4dd0e1", "#f48fb1", "#ffe082"
];

// One clamp for every team index: the palette, the bucket labels, and the
// squad size. A config asking for more teams than the panel has colors for
// would otherwise index past TEAM_COLORS and render "undefined" as a chip
// color. The labels are derived, not a parallel array that can fall out of
// step with TEAM_LIMIT (and with the count the +/- buttons allow).
function clampTeam(team: number | undefined): number {
  return Math.min(Math.max(numOr(team, 0), 0), TEAM_LIMIT);
}

// Bucket count the panel renders, clamped to the 0..TEAM_LIMIT range the
// server holds (BotConfig.SetTeamCount). One definition, so the buckets, the
// Team-column options and the +/- buttons cannot drift on how many teams
// exist.
function teamCountOf(s: BotStatus): number {
  return clampTeam(numOr(s.teamCount, 2));
}

function teamColor(team: number | undefined): string {
  return TEAM_COLORS[clampTeam(team)];
}

function teamLabel(team: number | undefined): string {
  const t = clampTeam(team);
  return t === 0 ? "FFA" : `Team ${t}`;
}

function renderBotHeader(h: CreateElement, s: BotStatus, onlinePlayers: Array<BotPlayer>, pill: (on: boolean, onLabel: string, offLabel: string) => unknown): unknown {
  const online = onlinePlayers.length;
  // The name list is the one user-chosen run in this line (players join with
  // whatever name their client carries, Arabic and CJK included), so it gets
  // its own dir=auto span: nested directly in the fixed English it would
  // inherit the panel's base direction and reorder against it.
  const onlineText: unknown = online === 0
    ? "no players online"
    : h("span", null,
        `${formatCount(online)} ${isSingular(online) ? "player" : "players"} online (`,
        h("span", { dir: "auto" }, onlinePlayers.map((p): string => p.name).join(", ")),
        ")");
  return h("div", { className: "botmod-head" },
    h("h2", null, "Bot Control"),
    pill(s.enabled === true, "ENABLED", "DISABLED"),
    note(h,
      `alive ${formatCount(num(s.alive))}/${formatCount(num(s.targetBotCount))} · max ${formatCount(num(s.maxBots))} · brain ${brainLabel(s.neural, s.neuralLoaded)} · `,
      onlineText));
}

// Outcome of the last command. It stays until the next command or a dismiss,
// so a failure is not a toast the user has already missed.
function renderCommandStatus(h: CreateElement, status: CommandStatus | null, onDismiss: () => void): unknown {
  if (status === null) {
    return null;
  }
  return h("div", { className: `botmod-status${status.bad ? " botmod-status-bad" : ""}`, role: status.bad ? "alert" : "status" },
    // dir=auto: the result line names a bot by its player-chosen name ("setTeam"
    // leads with it), so a mixed Arabic/CJK run in an English sentence resolves
    // its own base direction instead of borrowing the panel's.
    h("span", { className: "botmod-status-text", dir: "auto" }, status.text),
    h("button", { className: BTN, onClick: onDismiss }, "Dismiss"));
}

// Enable/Disable is a toggle, not a destructive action, and every other
// toggle in the panel (Skill, Brain, Squad, Shoot at) acts on a single click.
// Only the actions that remove bots ask for a confirmation.
function renderSpawnRow(h: CreateElement, enabled: boolean, busy: boolean, spawnCount: string, setSpawnCount: (v: string) => void, post: (body: BotAction) => void, btn: (label: string, body: BotAction, cls?: string, off?: boolean) => unknown, armedBtn: (label: string, body: BotAction, cls?: string) => unknown): unknown {
  return h("div", { className: ROW },
    btn(enabled ? "Disable" : "Enable", { action: enabled ? "disable" : "enable" },
      enabled ? DANGER : PRIMARY),
    armedBtn("Remove all", { action: "remove" }, DANGER),
    h("input", {
      className: "botmod-num", type: "number", min: SPAWN_COUNT_MIN, max: SPAWN_COUNT_MAX, value: spawnCount,
      "aria-label": "Bots to spawn",
      onChange: (e: { target: { value: string } }): void => setSpawnCount(e.target.value)
    }),
    btn("Spawn", { action: "spawn", count: toCount(spawnCount) }, PRIMARY),
    [1, 4, 8].map((n): unknown =>
      h("button", {
        key: n, className: BTN, disabled: busy,
        title: `Spawn ${botCount(n)} at the default spot`,
        onClick: (): void => post({ action: "spawn", count: n })
      }, `+${formatCount(n)}`)));
}

// The row class every brain-family row shares (skill, brain, squad, vs, teams).
// One constant so the rows cannot drift apart on padding or spacing.
const BRAIN_ROW_CLASS = `${ROW} botmod-brain`;

function renderSkillRow(h: CreateElement, s: BotStatus, busy: boolean, post: (body: BotAction) => void): unknown {
  return h("div", { className: BRAIN_ROW_CLASS },
    rowLabel(h, "Skill"),
    [0, 1, 2, 3, 4].map((d): unknown =>
      h("button", {
        key: d, className: s.difficulty === d ? `${BTN} ${PRIMARY}` : BTN, disabled: busy,
        onClick: (): void => post({ action: "skill", level: d })
      }, String(d))),
    note(h, "0 bot · 1 easy · 2 normal · 3 hard · 4 nightmare"));
}

// With no player online there is nothing to spawn near: the count, weapon and
// Spawn near controls go dead rather than posting a command the server drops.
function renderNearRow(h: CreateElement, onlinePlayers: Array<BotPlayer>, nearPlayer: string, setNearPlayer: (v: string) => void, nearCount: string, setNearCount: (v: string) => void, nearWeapon: string, setNearWeapon: (v: string) => void, btn: (label: string, body: BotAction, cls?: string, off?: boolean) => unknown): unknown {
  const noPlayers = onlinePlayers.length === 0;
  return h("div", { className: ROW },
    rowLabel(h, "Near player"),
    noPlayers
      ? note(h, "no players online")
      : h("select", {
          className: "botmod-select", value: nearPlayer,
          "aria-label": "Player",
          onChange: (e: { target: { value: string } }): void => setNearPlayer(e.target.value)
        }, onlinePlayers.map((p): unknown => h("option", { key: p.entityId, value: p.name }, p.name))),
    h("input", {
      className: "botmod-num", type: "number", min: SPAWN_COUNT_MIN, max: SPAWN_COUNT_MAX, value: nearCount, disabled: noPlayers,
      "aria-label": "Bots to spawn near player",
      onChange: (e: { target: { value: string } }): void => setNearCount(e.target.value)
    }),
    h("input", {
      className: "botmod-weapon", type: "text", placeholder: "weapon (opt)", value: nearWeapon, disabled: noPlayers,
      "aria-label": "Weapon (optional)",
      onChange: (e: { target: { value: string } }): void => setNearWeapon(e.target.value)
    }),
    btn("Spawn near", {
      action: "spawnNear", player: nearPlayer,
      count: toCount(nearCount),
      weapon: nearWeapon === "" ? undefined : nearWeapon
    }, PRIMARY, noPlayers));
}

function renderBrainRow(h: CreateElement, s: BotStatus, btn: (label: string, body: BotAction, cls?: string) => unknown): unknown {
  return h("div", { className: BRAIN_ROW_CLASS },
    rowLabel(h, "Brain"),
    btn(s.neural === true ? "Static AI" : "GA brain", { action: "neural", on: s.neural !== true }),
    s.neuralPath !== undefined && s.neuralPath !== "" ? note(h, `weights: ${s.neuralPath}`) : null);
}

function renderTeamRow(h: CreateElement, s: BotStatus, btn: (label: string, body: BotAction, cls?: string) => unknown): unknown {
  const team = s.botTeam === true;
  return h("div", { className: BRAIN_ROW_CLASS },
    rowLabel(h, "Squad"),
    btn(team ? "Free-for-all" : "Squad mode", { action: "team", on: !team }, team ? PRIMARY : ""),
    note(h, team ? "all bots are allies" : "bots fight each other"));
}

function renderVsRow(h: CreateElement, s: BotStatus, busy: boolean, post: (body: BotAction) => void): unknown {
  const toggles: Array<{ label: string; target: string; on: boolean }> = [
    { label: "Bots", target: "bot", on: s.botVsBot === true },
    { label: "Zombies", target: "zombie", on: s.botVsZombie === true },
    { label: "Players", target: "player", on: s.botVsPlayer === true }
  ];
  return h("div", { className: BRAIN_ROW_CLASS },
    rowLabel(h, "Shoot at"),
    toggles.map((t): unknown =>
      h("button", {
        key: t.target, className: t.on ? `${BTN} ${PRIMARY}` : BTN, disabled: busy,
        onClick: (): void => post({ action: "vs", target: t.target, on: !t.on })
      }, `${t.label}${t.on ? "" : " OFF"}`)),
    note(h, "squad mode overrides vs Bots"));
}

function renderTeamsCard(h: CreateElement, s: BotStatus, bots: Array<BotStat>, busy: boolean, post: (body: BotAction) => void, armedBtn: (label: string, body: BotAction, cls?: string) => unknown, dragName: string | null, setDragName: (v: string | null) => void, dropOver: number | null, setDropOver: (v: number | null) => void): unknown {
  const teamCount = teamCountOf(s);
  const buckets: Array<{ team: number; label: string; color: string; members: Array<BotStat> }> = [];
  for (let t = 0; t <= teamCount; t++) {
    buckets.push({
      team: t,
      label: teamLabel(t),
      color: teamColor(t),
      members: bots.filter((b): boolean => num(b.team) === t)
    });
  }
  return h("div", { className: `${ROW} botmod-brain botmod-teams` },
    rowLabel(h, "Teams"),
    buckets.map((bkt): unknown =>
      h("div", {
        key: bkt.team,
        className: `botmod-bucket${dropOver === bkt.team ? " botmod-drop-active" : ""}`,
        style: { borderColor: bkt.color },
        onDragOver: (e: { preventDefault: () => void }): void => {
          e.preventDefault();
          if (dragName !== null) {
            setDropOver(bkt.team);
          }
        },
        onDragLeave: (): void => {
          if (dropOver === bkt.team) {
            setDropOver(null);
          }
        },
        onDrop: (): void => {
          if (dragName !== null && dragName !== "") {
            post({ action: "setTeam", name: dragName, team: bkt.team });
          }
          setDropOver(null);
          setDragName(null);
        }
      },
      h("span", { className: "botmod-bucket-head", style: { color: bkt.color } }, `${bkt.label} · ${formatCount(bkt.members.length)}`),
      bkt.members.length === 0
        ? h("span", { className: "botmod-bucket-empty" }, "drag a bot here")
        : bkt.members.map((b): unknown =>
            h("span", {
              key: b.entityId,
              className: "botmod-chip",
              dir: "auto",
              draggable: true,
              onDragStart: (): void => setDragName(b.name),
              onDragEnd: (): void => setDragName(null)
            }, b.name)))),
    h("button", {
      className: BTN, title: "Fewer teams", disabled: busy || teamCount <= 0,
      onClick: (): void => post({ action: "teamCount", count: teamCount - 1 })
    }, "− teams"),
    h("button", {
      className: BTN, title: "More teams", disabled: busy || teamCount >= TEAM_LIMIT,
      onClick: (): void => post({ action: "teamCount", count: teamCount + 1 })
    }, "+ teams"),
    armedBtn("Clear teams", { action: "clearTeams" }, DANGER));
}

function renderConfigRow(h: CreateElement, s: BotStatus): unknown {
  return h("div", { className: `${ROW} botmod-cfg` },
    note(h,
      `vision ${formatMeters(num(s.visionRange))} · attack ${formatMeters(num(s.attackRange))} · spawn r ${formatMeters(num(s.spawnRadius))}` +
      ` · strafe ${formatPercent(num(s.strafeChance))} · dodge ${formatPercent(num(s.dodgeOnHitChance))}` +
      `${s.botVsBot === true ? " · vsBot" : ""} · hp ${formatCount(num(s.botHealth))}`));
}

function ariaSortValue(sort: SortState, key: string): string {
  if (sort.key !== key) {
    return "none";
  }
  return sort.dir < 0 ? "descending" : "ascending";
}

// The arrow glyph duplicates what aria-sort already announces; hide it from AT.
function sortArrowNode(h: CreateElement, sort: SortState, key: string): unknown {
  if (sort.key !== key) {
    return null;
  }
  return h("span", { key: "arrow", "aria-hidden": "true" }, sort.dir < 0 ? " ▼" : " ▲");
}

// Row label: the same span heads every control row, so the class and the
// trailing colon live in one place.
function rowLabel(h: CreateElement, text: string): unknown {
  return h("span", { className: "botmod-label" }, `${text}:`);
}

// The read-only text that ends a control row. Same span in every row, so the
// element and its class live in one place: the bundle is served uncompressed
// and every row would otherwise carry its own copy of the class name.
function note(h: CreateElement, ...text: Array<unknown>): unknown {
  return h("span", { className: WINDOW }, text);
}

// One scoreboard row: draggable for pointer users; the Team select is the
// keyboard/screen-reader path to the same action (dragging needs an
// alternative that does not rely on pointer precision, WCAG 2.5.7).
// changedSig (when non-null) turns the flash class on so a changed bot blinks
// once; the class drops on the next unchanged poll. The key stays the entity
// id: a key that changed with the signature remounted the row on every poll
// that moved a live field, which took the focus away from a Team select the
// user was editing.
function botRow(h: CreateElement, b: BotStat, busy: boolean, post: (body: BotAction) => void, teamOptions: Array<unknown>, dragName: string | null, setDragName: (v: string | null) => void, setDropOver: (v: number | null) => void, changedSig: string | null): unknown {
  let rowClass = "";
  if (changedSig !== null) {
    rowClass = "botmod-flash";
  }
  if (dragName === b.name) {
    rowClass = rowClass === "" ? "botmod-drag" : `${rowClass} botmod-drag`;
  }
  return h("tr", {
    key: b.entityId,
    draggable: true,
    className: rowClass,
    onDragStart: (e: { dataTransfer: { setData: (t: string, v: string) => void; effectAllowed: string } }): void => {
      e.dataTransfer.setData("text/plain", b.name);
      e.dataTransfer.effectAllowed = "move";
      setDragName(b.name);
    },
    onDragEnd: (): void => {
      setDragName(null);
      setDropOver(null);
    }
  },
    // dir=auto on every cell that carries a player-chosen name, whether as
    // visible text or inside an aria-label: an Arabic or Hebrew bot name
    // inside an LTR table, or a mixed run of Latin and RTL text, resolves its
    // own base direction instead of borrowing the page's.
    h("td", { dir: "auto" },
      h("span", { className: "botmod-teamdot", style: { background: teamColor(b.team) }, "aria-hidden": "true" }),
      b.name),
    h("td", null, b.weapon),
    h("td", null, fmtCell(b.health)),
    h("td", null, fmtCell(b.players)),
    h("td", null, fmtCell(b.zombies)),
    h("td", null, fmtCell(b.deaths)),
    h("td", null, fmtCell(b.score)),
    h("td", null, fmtCell(b.level)),
    h("td", { dir: "auto" }, nearLabel(b)),
    h("td", { dir: "auto" }, h("select", {
      className: "botmod-teamsel", value: String(num(b.team)), disabled: busy,
      "aria-label": `Team for ${b.name}`,
      onChange: (e: { target: { value: string } }): void => post({ action: "setTeam", name: b.name, team: Number.parseInt(e.target.value, 10) })
    }, teamOptions)),
    h("td", { className: "botmod-state" }, b.status),
    h("td", { dir: "auto" }, h("button", {
      className: "botmod-btn botmod-danger botmod-remove", title: "Remove bot",
      "aria-label": `Remove bot ${b.name}`,
      disabled: busy, onClick: (): void => post({ action: "removeOne", entityId: b.entityId })
    }, "✕")));
}

// Sortable column header: a real button inside the th keeps sorting keyboard
// operable (2.1.1); aria-sort exposes the current direction so the arrow glyph
// can stay hidden from assistive tech.
// Churn visibility: signature of the per-bot fields that change during play.
// Compared against the previous poll so changed rows flash (see botRow).
function rowSig(b: BotStat): string {
  return `${numOr(b.health, -1)}|${b.status}|${num(b.team)}|${num(b.players)}|${num(b.zombies)}|${num(b.deaths)}|${num(b.score)}|${num(b.level)}`;
}

let prevRowSigs: Map<number, string> = new Map();

function renderScoreboard(h: CreateElement, s: BotStatus, bots: Array<BotStat>, busy: boolean, post: (body: BotAction) => void, sort: SortState, setSort: (v: SortState | ((prev: SortState) => SortState)) => void, dragName: string | null, setDragName: (v: string | null) => void, setDropOver: (v: number | null) => void): unknown {
  // `hint` spells out the abbreviations the header has to stay narrow for
  // ("Kills P"); it rides on the button, which is the visible header label.
  const th = (label: string, key: string, hint?: string): unknown =>
    h("th", {
      key: label,
      className: "botmod-sortable",
      "aria-sort": ariaSortValue(sort, key)
    },
      h("button", {
        className: "botmod-sort-btn",
        title: hint === undefined ? `Sort by ${label}` : `Sort by ${hint}`,
        onClick: (): void => setSort((srt: SortState): SortState => ({ key, dir: srt.key === key ? -srt.dir : -1 }))
      }, label, sortArrowNode(h, sort, key)));
  const teamCount = teamCountOf(s);
  const teamOptions: Array<unknown> = [];
  for (let t = 0; t <= teamCount; t++) {
    teamOptions.push(h("option", { key: t, value: String(t) }, teamLabel(t)));
  }
  const sigs = new Map<number, string>();
  for (const b of bots) {
    sigs.set(b.entityId, rowSig(b));
  }
  const changed = (id: number): string | null => {
    const current = sigs.get(id);
    // No flash on the first paint; only actual poll-to-poll changes flash.
    if (prevRowSigs.size === 0 || current === undefined || prevRowSigs.get(id) === current) {
      return null;
    }
    return current;
  };
  prevRowSigs = sigs;
  return h("div", { className: "botmod-scoreboard" },
    h("h3", null, `Scoreboard (${formatCount(bots.length)}) · drag a row onto a team`),
    bots.length === 0
      ? h("p", { className: "botmod-empty" }, "No bots alive. Set a count above and press Spawn to add some.")
      : h("div", { className: "botmod-tablescroll" },
        h("table", { className: "botmod-table" },
          h("caption", { className: "botmod-sronly" }, "Bot scoreboard"),
          h("thead", null, h("tr", null,
            th("Bot", "name"), th("Weapon", "weapon"), th("HP", "health"),
            th("Kills P", "players", "Kills on players"), th("Kills Z", "zombies", "Kills on zombies"), th("Deaths", "deaths"),
            th("Score", "score"), th("Lvl", "level", "Bot level"),
            th("Near", "nearestPlayerDist", "Nearest player"),
            th("Team", "team"),
            h("th", { key: "state" }, "State"), h("th", { key: "x" }, ""))),
          h("tbody", null, [...bots].sort(bySortKey(sort)).map((b): unknown =>
            botRow(h, b, busy, post, teamOptions, dragName, setDragName, setDropOver, changed(b.entityId)))))));
}

// A rejected login needs a different action from a server that is merely
// down: offering "Log in" for a 500 sends the user after the wrong problem.
function renderQueryError(h: CreateElement, errStatus: number, onRetry: () => void): unknown {
  const auth = errStatus === 401 || errStatus === 403;
  return h("div", { className: "botmod-panel" },
    h("h2", null, "Bot Control"),
    h("span", { className: `botmod-pill ${auth ? "botmod-bad" : "botmod-off"}`, role: "status" }, auth ? "AUTH REQUIRED" : "API ERROR"),
    h("p", { role: "alert" }, auth
      ? "Authentication required: log in to the dashboard as an admin to control bots."
      : `Bot API not responding (HTTP ${errStatus === 0 ? "error" : errStatus}); retrying every ${formatSeconds(POLL_INTERVAL_MS / 1000)}.`),
    auth
      ? h("button", { className: BTN, onClick: (): void => { location.href = "/"; } }, "Log in")
      : h("button", { className: BTN, onClick: onRetry }, "Retry now"));
}

// First paint, before the first /api/bot response lands. The panel used to
// render from the empty snapshot the absent payload unwraps to, which read as
// a real reading: DISABLED, alive 0/0, "no players online", and an empty
// scoreboard telling the user to spawn bots. Say the reading has not arrived
// yet instead of inventing one.
function renderLoading(h: CreateElement): unknown {
  return h("div", { className: "botmod-panel", role: "status" },
    h("h2", null, "Bot Control"),
    h("span", { className: "botmod-pill botmod-off" }, "LOADING"),
    h("p", { className: "botmod-empty" }, "Reading bot status from the server..."));
}

function BotPanel({ React, HTTP, useQuery }: PanelProps): unknown {
  const h = React.createElement;

  // Stop polling on auth rejection instead of hammering the API. Any other
  // failure (server restart, network blip) keeps polling so the panel
  // recovers by itself; blocking on those froze the dashboard on stale data
  // until a manual reload.
  const [blocked, setBlocked] = React.useState(false);
  const query = useQuery("botmod-status", (): Promise<unknown> => HTTP.get("/api/bot"), {
    refetchInterval: POLL_INTERVAL_MS,
    enabled: !blocked,
    retry: false
  });
  React.useEffect((): void => {
    const status = num(query.error?.response?.status);
    if (query.isError === true && (status === 401 || status === 403)) {
      setBlocked(true);
    }
  }, [query.isError, query.error]);
  const [busy, setBusy] = React.useState(false); // a command is in flight; every control that posts one is disabled
  const [spawnCount, setSpawnCount] = React.useState("2");
  const [nearPlayer, setNearPlayer] = React.useState("");
  const [nearCount, setNearCount] = React.useState("1");
  const [nearWeapon, setNearWeapon] = React.useState("");
  const [armed, setArmed] = React.useState(""); // destructive buttons: click to arm, click again to run
  const [announce, setAnnounce] = React.useState(""); // polite live region for state changes SR users would otherwise miss
  const [status, setStatus] = React.useState<CommandStatus | null>(null); // outcome of the last command
  const [sort, setSort] = React.useState({ key: "score", dir: -1 });
  const [dragName, setDragName] = React.useState<string | null>(null); const [dropOver, setDropOver] = React.useState<number | null>(null); // dragged bot name + hovered team bucket

  const refetch = (): Promise<unknown> => (query.refetch === undefined ? Promise.resolve() : query.refetch());

  if (query.isError === true) {
    return renderQueryError(h, num(query.error?.response?.status), (): void => { setBlocked(false); void refetch(); });
  }

  if (query.data === undefined) {
    return renderLoading(h);
  }

  const s: BotStatus = unwrapData(query.data);
  const enabled = s.enabled === true;
  const bots = listOrEmpty<BotStat>(s.bots);
  const onlinePlayers = listOrEmpty<BotPlayer>(s.players);
  // Keep the selected target valid across refetches (players can leave); an
  // unset selection also picks the first player here.
  if (onlinePlayers.length > 0 && !onlinePlayers.some((p): boolean => p.name === nearPlayer)) {
    setNearPlayer(onlinePlayers[0].name);
  }
  const post = (body: BotAction): void => postAction({ HTTP, busy, setBusy, setArmed, setStatus, refetch, body });
  const btn = makeBtn(h, busy, post);
  const armedBtn = makeBtn(h, busy, post, { armed, setArmed, say: setAnnounce });
  const pill = (on: boolean, onLabel: string, offLabel: string): unknown =>
    h("span", { className: `botmod-pill ${on ? "botmod-ok" : "botmod-off"}` }, on ? onLabel : offLabel);

  return h("div", { className: "botmod-panel", "aria-busy": busy },
    renderBotHeader(h, s, onlinePlayers, pill),
    // Screen-reader channel for arm/confirm and command-sent state changes;
    // role=status implies a polite live region.
    h("p", { key: "srstatus", className: "botmod-sronly", role: "status" }, announce),
    renderCommandStatus(h, status, (): void => setStatus(null)),
    renderSpawnRow(h, enabled, busy, spawnCount, setSpawnCount, post, btn, armedBtn),
    renderSkillRow(h, s, busy, post),
    renderNearRow(h, onlinePlayers, nearPlayer, setNearPlayer, nearCount, setNearCount, nearWeapon, setNearWeapon, btn),
    renderBrainRow(h, s, btn),
    renderTeamRow(h, s, btn),
    renderVsRow(h, s, busy, post),
    renderTeamsCard(h, s, bots, busy, post, armedBtn, dragName, setDragName, dropOver, setDropOver),
    renderConfigRow(h, s),
    renderScoreboard(h, s, bots, busy, post, sort, setSort, dragName, setDragName, setDropOver));
}

// Menu entry registered only when the web session cookie is present; the
// dashboard reloads the page after login/logout, so this re-evaluates.
const loggedIn = document.cookie.split(";").some((c): boolean => c.trim().startsWith("sid="));
const webMod: WebModContract = {
  about: "FPS bots: enable/disable, spawn, static AI vs GA brain, drag-and-drop teams, scoreboard.",
  routes: loggedIn ? { "Bot": BotPanel } : {},
  settings: {},
  mapComponents: []
};
Object.assign(globalThis, { [modId]: webMod });
globalThis.dispatchEvent(new Event(`mod:${modId}:ready`));
})();
