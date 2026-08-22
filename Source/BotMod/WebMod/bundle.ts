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
  dedicatedOnly?: boolean;
  targetBotCount?: number;
  maxBots?: number;
  alive?: number;
  difficulty?: number;
  weapon?: string;
  neural?: boolean;
  neuralLoaded?: boolean;
  neuralPath?: string;
  visionRange?: number;
  attackRange?: number;
  spawnRadius?: number;
  strafeChance?: number;
  dodgeOnHitChance?: number;
  botVsBot?: boolean;
  botHealth?: number;
  useSpawnpoints?: boolean;
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
  on?: boolean;
};
type SortState = { key: string; dir: number };
type WebModContract = {
  about: string;
  routes: Record<string, unknown>;
  settings: Record<string, unknown>;
  mapComponents: Array<unknown>;
};

// The dashboard HTTP wrapper may hand us the axios response, the {data: ...}
// envelope, or the bare payload; accept all three. The payload is untyped
// runtime JSON, so the envelope unwrap is the boundary parse.
function unwrapSnap(o: unknown): BotStatus {
  if (typeof o !== "object" || o === null) {
    return {};
  }
  // oxlint-disable-next-line typescript/no-unsafe-type-assertion -- deliberate: untyped JSON payload boundary; SAFETY: typeof above proves the runtime value is an object
  const outer = o as Record<string, unknown>;
  const data1 = outer.data;
  if (typeof data1 !== "object" || data1 === null) {
    return outer;
  }
  // oxlint-disable-next-line typescript/no-unsafe-type-assertion -- deliberate: untyped JSON payload boundary; SAFETY: typeof above proves the runtime value is an object
  const inner = data1 as Record<string, unknown>;
  const data2 = inner.data;
  if (typeof data2 !== "object" || data2 === null) {
    return inner;
  }
  return data2;
}

const num = (v: unknown): number => (typeof v === "number" && Number.isFinite(v) ? v : 0);

function numOr(v: unknown, fallback: number): number {
  return typeof v === "number" && Number.isFinite(v) ? v : fallback;
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

function optNum(v: number | undefined): string {
  if (v === undefined || v === 0) {
    return "";
  }
  return String(v);
}

function toCount(v: string): number {
  const n = Number.parseInt(v, 10);
  if (!Number.isFinite(n) || n <= 0) {
    return 1;
  }
  return n;
}

// Fire a bot command. The 5s poll shows the real state after the call, so a
// failure only clears the busy flag; errors surface through the polled status.
function postAction(opts: {
  HTTP: PanelProps["HTTP"];
  busy: string;
  setBusy: (v: string) => void;
  setArmed: (v: string) => void;
  refetch: () => Promise<unknown>;
  body: BotAction;
}): void {
  if (opts.busy !== "") {
    return;
  }
  opts.setBusy(`${opts.body.action}${optNum(opts.body.count)}${optNum(opts.body.entityId)}`);
  opts.setArmed("");
  void opts.HTTP.post("/api/bot", opts.body)
    .then((): void => { void opts.refetch(); })
    .catch((): void => {
      // the next poll shows the real state after a failed command
      opts.setBusy("");
    })
    .then((): void => {
      opts.setBusy("");
    });
}

// Destructive buttons: click to arm, click again within 4 s to run.
function armOrRun(opts: {
  armed: string;
  setArmed: (v: string | ((prev: string) => string)) => void;
  label: string;
  onConfirm: () => void;
}): void {
  if (opts.armed === opts.label) {
    opts.onConfirm();
    return;
  }
  opts.setArmed(opts.label);
  setTimeout((): void => opts.setArmed((a: string) => (a === opts.label ? "" : a)), ARM_TIMEOUT_MS);
}

function makeBtn(h: CreateElement, busy: string, post: (body: BotAction) => void): (label: string, body: BotAction, cls?: string) => unknown {
  return (label: string, body: BotAction, cls?: string): unknown =>
    h("button", {
      className: `botmod-btn${cls === undefined ? "" : ` ${cls}`}`,
      disabled: busy !== "",
      onClick: (): void => post(body)
    }, label);
}

function makeArmedBtn(h: CreateElement, armed: string, setArmed: (v: string | ((prev: string) => string)) => void, busy: string, post: (body: BotAction) => void): (label: string, body: BotAction, cls?: string) => unknown {
  return (label: string, body: BotAction, cls?: string): unknown => {
    const isArmed = armed === label;
    return h("button", {
      className: `botmod-btn${cls === undefined ? "" : ` ${cls}`}${isArmed ? " botmod-armed" : ""}`,
      disabled: busy !== "",
      onClick: (): void => armOrRun({ armed, setArmed, label, onConfirm: (): void => post(body) })
    }, isArmed ? "Confirm?" : label);
  };
}

function bySortKey(sort: SortState): (a: BotStat, b: BotStat) => number {
  return (a, b): number => {
    const textKey = sort.key === "name" || sort.key === "weapon";
    // SAFETY: bot rows are read from the untyped JSON payload; sort.key is a known column of the same rows
    const av = textKey ? strOrEmpty((a as Record<string, unknown>)[sort.key]).toLowerCase() : numOr((a as Record<string, unknown>)[sort.key], -1);
    // SAFETY: same keyed access as av, on the other row
    const bv = textKey ? strOrEmpty((b as Record<string, unknown>)[sort.key]).toLowerCase() : numOr((b as Record<string, unknown>)[sort.key], -1);
    if (av < bv) {
      return -sort.dir;
    }
    if (av > bv) {
      return sort.dir;
    }
    return 0;
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
    return "—";
  }
  return `${b.nearestPlayerDist}m${b.nearestPlayer === undefined ? "" : ` ${b.nearestPlayer}`}`;
}

function renderBotHeader(h: CreateElement, s: BotStatus, onlinePlayers: Array<BotPlayer>, pill: (on: boolean, onLabel: string, offLabel: string) => unknown): unknown {
  const playerSuffix = onlinePlayers.length > 1 ? "s" : "";
  const onlineText = onlinePlayers.length > 0
    ? `${onlinePlayers.length} player${playerSuffix} online (${onlinePlayers.map((p): string => p.name).join(", ")})`
    : "no players online";
  return h("div", { className: "botmod-head" },
    h("h2", null, "Bot Control"),
    pill(s.enabled === true, "ENABLED", "DISABLED"),
    h("span", { className: "botmod-window" },
      `alive ${num(s.alive)}/${num(s.targetBotCount)} · max ${num(s.maxBots)} · brain ${brainLabel(s.neural, s.neuralLoaded)} · ${onlineText}`));
}

function renderSpawnRow(h: CreateElement, enabled: boolean, busy: string, spawnCount: string, setSpawnCount: (v: string) => void, post: (body: BotAction) => void, btn: (label: string, body: BotAction, cls?: string) => unknown, armedBtn: (label: string, body: BotAction, cls?: string) => unknown): unknown {
  return h("div", { className: "botmod-row" },
    armedBtn(enabled ? "Disable" : "Enable", { action: enabled ? "disable" : "enable" },
      enabled ? "botmod-danger" : "botmod-primary"),
    armedBtn("Remove all", { action: "remove" }, "botmod-danger"),
    h("input", {
      className: "botmod-num", type: "number", min: 1, max: 16, value: spawnCount,
      onChange: (e: { target: { value: string } }): void => setSpawnCount(e.target.value)
    }),
    btn("Spawn", { action: "spawn", count: toCount(spawnCount) }, "botmod-primary"),
    [1, 4, 8].map((n): unknown =>
      h("button", { key: n, className: "botmod-btn", disabled: busy !== "", onClick: (): void => post({ action: "spawn", count: n }) }, `+${n}`)));
}

function renderSkillRow(h: CreateElement, s: BotStatus, busy: string, post: (body: BotAction) => void): unknown {
  return h("div", { className: "botmod-row botmod-brain" },
    h("span", { className: "botmod-label" }, "Skill:"),
    [0, 1, 2, 3, 4].map((d): unknown =>
      h("button", {
        key: d, className: `botmod-btn${s.difficulty === d ? " botmod-primary" : ""}`, disabled: busy !== "",
        onClick: (): void => post({ action: "skill", level: d })
      }, String(d))),
    h("span", { className: "botmod-window" }, "0 bot · 1 easy · 2 normal · 3 hard · 4 nightmare"));
}

function renderNearRow(h: CreateElement, onlinePlayers: Array<BotPlayer>, nearPlayer: string, setNearPlayer: (v: string) => void, nearCount: string, setNearCount: (v: string) => void, nearWeapon: string, setNearWeapon: (v: string) => void, btn: (label: string, body: BotAction, cls?: string) => unknown): unknown {
  return h("div", { className: "botmod-row" },
    h("span", { className: "botmod-label" }, "Near player:"),
    onlinePlayers.length === 0
      ? h("span", { className: "botmod-window" }, "no players online")
      : h("select", {
          className: "botmod-select", value: nearPlayer,
          onChange: (e: { target: { value: string } }): void => setNearPlayer(e.target.value)
        }, onlinePlayers.map((p): unknown => h("option", { key: p.entityId, value: p.name }, p.name))),
    h("input", {
      className: "botmod-num", type: "number", min: 1, max: 16, value: nearCount,
      onChange: (e: { target: { value: string } }): void => setNearCount(e.target.value)
    }),
    h("input", {
      className: "botmod-weapon", type: "text", placeholder: "weapon (opt)", value: nearWeapon,
      onChange: (e: { target: { value: string } }): void => setNearWeapon(e.target.value)
    }),
    btn("Spawn near", {
      action: "spawnNear", player: nearPlayer,
      count: toCount(nearCount),
      weapon: nearWeapon === "" ? undefined : nearWeapon
    }, "botmod-primary"));
}

function renderBrainRow(h: CreateElement, s: BotStatus, busy: string, btn: (label: string, body: BotAction, cls?: string) => unknown): unknown {
  return h("div", { className: "botmod-row botmod-brain" },
    h("span", { className: "botmod-label" }, "Brain:"),
    btn(s.neural === true ? "Static AI" : "GA brain", { action: "neural", on: s.neural !== true }),
    s.neuralPath !== undefined && s.neuralPath !== "" ? h("span", { className: "botmod-window" }, `weights: ${s.neuralPath}`) : null);
}

function renderConfigRow(h: CreateElement, s: BotStatus): unknown {
  return h("div", { className: "botmod-row botmod-cfg" },
    h("span", { className: "botmod-window" },
      `vision ${num(s.visionRange)}m · attack ${num(s.attackRange)}m · spawn r ${num(s.spawnRadius)}m` +
      ` · strafe ${Math.round(num(s.strafeChance) * 100)}% · dodge ${Math.round(num(s.dodgeOnHitChance) * 100)}%` +
      `${s.botVsBot === true ? " · vsBot" : ""} · hp ${num(s.botHealth)}`));
}

function sortArrow(sort: SortState, key: string): string {
  if (sort.key !== key) {
    return "";
  }
  return sort.dir < 0 ? " ▼" : " ▲";
}

function renderScoreboard(h: CreateElement, bots: Array<BotStat>, busy: string, post: (body: BotAction) => void, sort: SortState, setSort: (v: SortState | ((prev: SortState) => SortState)) => void): unknown {
  const th = (label: string, key: string): unknown =>
    h("th", { key: label, className: "botmod-sortable", onClick: (): void => setSort((srt: SortState): SortState => ({ key, dir: srt.key === key ? -srt.dir : -1 })) },
      `${label}${sortArrow(sort, key)}`);
  return h("div", { className: "botmod-scoreboard" },
    h("h3", null, `Scoreboard (${bots.length})`),
    bots.length === 0
      ? h("p", { className: "botmod-empty" }, "No bots alive.")
      : h("table", { className: "botmod-table" },
          h("thead", null, h("tr", null,
            th("Bot", "name"), th("Weapon", "weapon"), th("HP", "health"),
            th("Kills P", "players"), th("Kills Z", "zombies"), th("Deaths", "deaths"),
            th("Score", "score"), th("Lvl", "level"), th("Near", "nearestPlayerDist"),
            h("th", { key: "state" }, "State"), h("th", { key: "x" }, ""))),
          h("tbody", null, [...bots].sort(bySortKey(sort)).map((b): unknown =>
            h("tr", { key: b.entityId },
              h("td", null, b.name),
              h("td", null, b.weapon),
              h("td", null, b.health),
              h("td", null, b.players),
              h("td", null, b.zombies),
              h("td", null, b.deaths),
              h("td", null, b.score),
              h("td", null, b.level),
              h("td", null, nearLabel(b)),
              h("td", { className: "botmod-state" }, b.status),
              h("td", null, h("button", {
                className: "botmod-btn botmod-danger botmod-remove", title: "Remove bot",
                disabled: busy !== "", onClick: (): void => post({ action: "removeOne", entityId: b.entityId })
              }, "✕")))))));
}

function BotPanel({ React, HTTP, useQuery }: PanelProps): unknown {
  const h = React.createElement;

  // Stop polling after the first auth failure instead of hammering the API.
  const [blocked, setBlocked] = React.useState(false);
  const query = useQuery("botmod-status", (): Promise<unknown> => HTTP.get("/api/bot"), {
    refetchInterval: POLL_INTERVAL_MS,
    enabled: !blocked,
    retry: false
  });
  React.useEffect((): void => {
    if (query.isError === true) {
      setBlocked(true);
    }
  }, [query.isError]);
  const [busy, setBusy] = React.useState("");
  const [spawnCount, setSpawnCount] = React.useState("2");
  const [nearPlayer, setNearPlayer] = React.useState("");
  const [nearCount, setNearCount] = React.useState("1");
  const [nearWeapon, setNearWeapon] = React.useState("");
  const [armed, setArmed] = React.useState(""); // destructive buttons: click to arm, click again to run
  const [sort, setSort] = React.useState({ key: "score", dir: -1 });

  if (query.isError === true) {
    const status = num(query.error?.response?.status);
    const msg = status === 403
      ? "Authentication required: log in to the dashboard as an admin (permission level 0) to control bots."
      : `Bot API unavailable (HTTP ${status === 0 ? "error" : String(status)}).`;
    return h("div", { className: "botmod-panel" },
      h("h2", null, "Bot Control"),
      h("span", { className: "botmod-pill botmod-bad" }, "AUTH REQUIRED"),
      h("p", null, msg),
      h("button", { className: "botmod-btn", onClick: (): void => { location.href = "/"; } }, "Log in"));
  }

  const s = unwrapSnap(query.data);
  const enabled = s.enabled === true;
  const bots = listOrEmpty<BotStat>(s.bots);
  const onlinePlayers = listOrEmpty<BotPlayer>(s.players);
  // Keep the selected target valid across refetches (players can leave).
  if (nearPlayer !== "" && onlinePlayers.length > 0 && !onlinePlayers.some((p): boolean => p.name === nearPlayer)) {
    setNearPlayer(onlinePlayers[0].name);
  }
  if (nearPlayer === "" && onlinePlayers.length > 0) {
    setNearPlayer(onlinePlayers[0].name);
  }
  const refetch = (): Promise<unknown> => (query.refetch === undefined ? Promise.resolve() : query.refetch());
  const post = (body: BotAction): void => postAction({ HTTP, busy, setBusy, setArmed, refetch, body });
  const btn = makeBtn(h, busy, post);
  const armedBtn = makeArmedBtn(h, armed, setArmed, busy, post);
  const pill = (on: boolean, onLabel: string, offLabel: string): unknown =>
    h("span", { className: `botmod-pill ${on ? "botmod-ok" : "botmod-off"}` }, on ? onLabel : offLabel);

  return h("div", { className: "botmod-panel" },
    renderBotHeader(h, s, onlinePlayers, pill),
    renderSpawnRow(h, enabled, busy, spawnCount, setSpawnCount, post, btn, armedBtn),
    renderSkillRow(h, s, busy, post),
    renderNearRow(h, onlinePlayers, nearPlayer, setNearPlayer, nearCount, setNearCount, nearWeapon, setNearWeapon, btn),
    renderBrainRow(h, s, busy, btn),
    renderConfigRow(h, s),
    renderScoreboard(h, bots, busy, post, sort, setSort));
}

// Menu entry registered only when the web session cookie is present; the
// dashboard reloads the page after login/logout, so this re-evaluates.
const loggedIn = document.cookie.split(";").some((c): boolean => c.trim().startsWith("sid="));
const webMod: WebModContract = {
  about: "FPS bots: enable/disable, spawn, static AI vs GA brain, scoreboard.",
  routes: loggedIn ? { "Bot": BotPanel } : {},
  settings: {},
  mapComponents: []
};
Object.assign(globalThis, { [modId]: webMod });
globalThis.dispatchEvent(new Event(`mod:${modId}:ready`));
})();
