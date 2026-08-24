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

function numOr(v: unknown, fallback: number): number {
  return typeof v === "number" && Number.isFinite(v) ? v : fallback;
}

const num = (v: unknown): number => numOr(v, 0);

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

// One idempotency key per logical command (per click): the server records the
// first response under this key, so a retried POST (lost response after the
// server acted, proxy retry) replays it instead of spawning twice.
function newRequestId(): string {
  const c: Crypto | undefined = typeof crypto === "undefined" ? undefined : crypto;
  if (c !== undefined && typeof c.randomUUID === "function") {
    return c.randomUUID();
  }
  return `botmod-${Date.now().toString(36)}-${Math.floor(Math.random() * 4_294_967_296).toString(36)}`;
}

// Fire a bot command. The 5s poll shows the real state after the call, so a
// failure only clears the busy flag; errors surface through the polled status.
// `say` feeds the screen-reader status region (the busy state disables every
// control, which sighted users see but assistive tech would not announce).
function postAction(opts: {
  HTTP: PanelProps["HTTP"];
  busy: string;
  setBusy: (v: string) => void;
  setArmed: (v: string) => void;
  say: (v: string) => void;
  refetch: () => Promise<unknown>;
  body: BotAction;
}): void {
  if (opts.busy !== "") {
    return;
  }
  opts.setBusy(`${opts.body.action}${optNum(opts.body.count)}${optNum(opts.body.entityId)}`);
  opts.setArmed("");
  opts.say(`${opts.body.action}: command sent`);
  const body: BotAction = { ...opts.body, requestId: newRequestId() };
  void opts.HTTP.post("/api/bot", body)
    .then((): void => { void opts.refetch(); })
    .catch((): void => {
      // the next poll shows the real state after a failed command
      opts.setBusy("");
    })
    .then((): void => {
      opts.setBusy("");
    });
}

// Destructive buttons: click to arm, click again within 4 s to run. The arm
// step is announced (`say`) because the only visual cue is the label change.
function armOrRun(opts: {
  armed: string;
  setArmed: (v: string | ((prev: string) => string)) => void;
  label: string;
  say: (v: string) => void;
  onConfirm: () => void;
}): void {
  if (opts.armed === opts.label) {
    opts.onConfirm();
    return;
  }
  opts.setArmed(opts.label);
  opts.say(`${opts.label} armed, activate again within 4 seconds to confirm`);
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

function makeArmedBtn(h: CreateElement, armed: string, setArmed: (v: string | ((prev: string) => string)) => void, busy: string, say: (v: string) => void, post: (body: BotAction) => void): (label: string, body: BotAction, cls?: string) => unknown {
  return (label: string, body: BotAction, cls?: string): unknown => {
    const isArmed = armed === label;
    return h("button", {
      className: `botmod-btn${cls === undefined ? "" : ` ${cls}`}${isArmed ? " botmod-armed" : ""}`,
      disabled: busy !== "",
      onClick: (): void => armOrRun({ armed, setArmed, label, say, onConfirm: (): void => post(body) })
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
    return "n/a";
  }
  return `${b.nearestPlayerDist}m${b.nearestPlayer === undefined ? "" : ` ${b.nearestPlayer}`}`;
}

// Team palette: index 0 = free-for-all (neutral), 1..8 team colors. Kept in
// sync with the buckets, row dots, chips, and per-row selects.
const TEAM_COLORS: ReadonlyArray<string> = [
  "#9aa0a6", "#ff7070", "#8ab4f8", "#57d977", "#f9ab00", "#c58af9", "#4dd0e1", "#f48fb1", "#ffe082"
];
const TEAM_LABELS: ReadonlyArray<string> = [
  "FFA", "Team 1", "Team 2", "Team 3", "Team 4", "Team 5", "Team 6", "Team 7", "Team 8"
];

function teamColor(team: number | undefined): string {
  return TEAM_COLORS[Math.min(numOr(team, 0), TEAM_COLORS.length - 1)];
}

function teamLabel(team: number | undefined): string {
  return TEAM_LABELS[Math.min(numOr(team, 0), TEAM_LABELS.length - 1)];
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
      "aria-label": "Bots to spawn",
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
          "aria-label": "Player",
          onChange: (e: { target: { value: string } }): void => setNearPlayer(e.target.value)
        }, onlinePlayers.map((p): unknown => h("option", { key: p.entityId, value: p.name }, p.name))),
    h("input", {
      className: "botmod-num", type: "number", min: 1, max: 16, value: nearCount,
      "aria-label": "Bots to spawn near player",
      onChange: (e: { target: { value: string } }): void => setNearCount(e.target.value)
    }),
    h("input", {
      className: "botmod-weapon", type: "text", placeholder: "weapon (opt)", value: nearWeapon,
      "aria-label": "Weapon (optional)",
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

function renderTeamRow(h: CreateElement, s: BotStatus, busy: string, btn: (label: string, body: BotAction, cls?: string) => unknown): unknown {
  const team = s.botTeam === true;
  return h("div", { className: "botmod-row botmod-brain" },
    h("span", { className: "botmod-label" }, "Squad:"),
    btn(team ? "Free-for-all" : "Squad mode", { action: "team", on: !team }, team ? "botmod-primary" : ""),
    h("span", { className: "botmod-window" }, team ? "all bots are allies" : "bots fight each other"));
}

function renderVsRow(h: CreateElement, s: BotStatus, busy: string, post: (body: BotAction) => void): unknown {
  const toggles: Array<{ label: string; target: string; on: boolean }> = [
    { label: "Bots", target: "bot", on: s.botVsBot === true },
    { label: "Zombies", target: "zombie", on: s.botVsZombie === true },
    { label: "Players", target: "player", on: s.botVsPlayer === true }
  ];
  return h("div", { className: "botmod-row botmod-brain" },
    h("span", { className: "botmod-label" }, "Shoot at:"),
    toggles.map((t): unknown =>
      h("button", {
        key: t.target, className: `botmod-btn${t.on ? " botmod-primary" : ""}`, disabled: busy !== "",
        onClick: (): void => post({ action: "vs", target: t.target, on: !t.on })
      }, `${t.label}${t.on ? "" : " OFF"}`)),
    h("span", { className: "botmod-window" }, "squad mode overrides vs Bots"));
}

function renderTeamsCard(h: CreateElement, s: BotStatus, bots: Array<BotStat>, busy: string, post: (body: BotAction) => void, armedBtn: (label: string, body: BotAction, cls?: string) => unknown, dragName: string | null, setDragName: (v: string | null) => void, dropOver: number | null, setDropOver: (v: number | null) => void): unknown {
  const teamCount = Math.max(0, Math.min(8, numOr(s.teamCount, 2)));
  const buckets: Array<{ team: number; label: string; color: string; members: Array<BotStat> }> = [];
  for (let t = 0; t <= teamCount; t++) {
    buckets.push({
      team: t,
      label: teamLabel(t),
      color: teamColor(t),
      members: bots.filter((b): boolean => numOr(b.team, 0) === t)
    });
  }
  return h("div", { className: "botmod-row botmod-brain botmod-teams" },
    h("span", { className: "botmod-label" }, "Teams:"),
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
      h("span", { className: "botmod-bucket-head", style: { color: bkt.color } }, `${bkt.label} · ${bkt.members.length}`),
      bkt.members.length === 0
        ? h("span", { className: "botmod-bucket-empty" }, "drag a bot here")
        : bkt.members.map((b): unknown =>
            h("span", {
              key: b.entityId,
              className: "botmod-chip",
              draggable: true,
              onDragStart: (): void => setDragName(b.name),
              onDragEnd: (): void => setDragName(null)
            }, b.name)))),
    h("button", {
      className: "botmod-btn", title: "Fewer teams", disabled: busy !== "" || teamCount <= 0,
      onClick: (): void => post({ action: "teamCount", count: teamCount - 1 })
    }, "− teams"),
    h("button", {
      className: "botmod-btn", title: "More teams", disabled: busy !== "" || teamCount >= 8,
      onClick: (): void => post({ action: "teamCount", count: teamCount + 1 })
    }, "+ teams"),
    armedBtn("Clear teams", { action: "clearTeams" }, "botmod-danger"),
    h("span", { className: "botmod-window" }, "drag a bot onto a team (or use its Team column) · picks persist"));
}

function renderConfigRow(h: CreateElement, s: BotStatus): unknown {
  return h("div", { className: "botmod-row botmod-cfg" },
    h("span", { className: "botmod-window" },
      `vision ${num(s.visionRange)}m · attack ${num(s.attackRange)}m · spawn r ${num(s.spawnRadius)}m` +
      ` · strafe ${Math.round(num(s.strafeChance) * 100)}% · dodge ${Math.round(num(s.dodgeOnHitChance) * 100)}%` +
      `${s.botVsBot === true ? " · vsBot" : ""} · hp ${num(s.botHealth)}`));
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

// One scoreboard row: draggable for pointer users; the Team select is the
// keyboard/screen-reader path to the same action (dragging needs an
// alternative that does not rely on pointer precision, WCAG 2.5.7).
// changedSig (when non-null) remounts the row with the flash class so a
// changed bot blinks once; the class drops on the next unchanged poll.
function botRow(h: CreateElement, b: BotStat, busy: string, post: (body: BotAction) => void, teamOptions: Array<unknown>, dragName: string | null, setDragName: (v: string | null) => void, setDropOver: (v: number | null) => void, changedSig: string | null): unknown {
  let rowClass = "";
  if (changedSig !== null) {
    rowClass = "botmod-flash";
    if (dragName === b.name) {
      rowClass += " botmod-drag";
    }
  } else if (dragName === b.name) {
    rowClass = "botmod-drag";
  }
  return h("tr", {
    key: changedSig === null ? String(b.entityId) : `${b.entityId}:${changedSig}`,
    draggable: true,
    className: rowClass,
    title: "Drag onto a team bucket",
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
    h("td", null,
      h("span", { className: "botmod-teamdot", style: { background: teamColor(b.team) }, "aria-hidden": "true" }),
      b.name),
    h("td", null, b.weapon),
    h("td", null, b.health),
    h("td", null, b.players),
    h("td", null, b.zombies),
    h("td", null, b.deaths),
    h("td", null, b.score),
    h("td", null, b.level),
    h("td", null, nearLabel(b)),
    h("td", null, h("select", {
      className: "botmod-teamsel", value: String(numOr(b.team, 0)), disabled: busy !== "",
      "aria-label": `Team for ${b.name}`,
      onChange: (e: { target: { value: string } }): void => post({ action: "setTeam", name: b.name, team: Number.parseInt(e.target.value, 10) })
    }, teamOptions)),
    h("td", { className: "botmod-state" }, b.status),
    h("td", null, h("button", {
      className: "botmod-btn botmod-danger botmod-remove", title: "Remove bot",
      "aria-label": `Remove bot ${b.name}`,
      disabled: busy !== "", onClick: (): void => post({ action: "removeOne", entityId: b.entityId })
    }, "✕")));
}

// Sortable column header: a real button inside the th keeps sorting keyboard
// operable (2.1.1); aria-sort exposes the current direction so the arrow glyph
// can stay hidden from assistive tech.
// Churn visibility: signature of the per-bot fields that change during play.
// Compared against the previous poll so changed rows flash (see botRow).
function rowSig(b: BotStat): string {
  return `${numOr(b.health, -1)}|${b.status}|${numOr(b.team, 0)}|${numOr(b.players, 0)}|${numOr(b.zombies, 0)}|${numOr(b.deaths, 0)}|${numOr(b.score, 0)}|${numOr(b.level, 0)}`;
}

let prevRowSigs: Map<number, string> = new Map();

function renderScoreboard(h: CreateElement, s: BotStatus, bots: Array<BotStat>, busy: string, post: (body: BotAction) => void, sort: SortState, setSort: (v: SortState | ((prev: SortState) => SortState)) => void, dragName: string | null, setDragName: (v: string | null) => void, setDropOver: (v: number | null) => void): unknown {
  const th = (label: string, key: string): unknown =>
    h("th", {
      key: label,
      className: "botmod-sortable",
      "aria-sort": ariaSortValue(sort, key)
    },
      h("button", {
        className: "botmod-sort-btn",
        onClick: (): void => setSort((srt: SortState): SortState => ({ key, dir: srt.key === key ? -srt.dir : -1 }))
      }, label, sortArrowNode(h, sort, key)));
  const teamCount = Math.max(0, Math.min(8, numOr(s.teamCount, 2)));
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
    h("h3", null, `Scoreboard (${bots.length}) · drag rows onto a team or use the Team column`),
    bots.length === 0
      ? h("p", { className: "botmod-empty" }, "No bots alive.")
      : h("table", { className: "botmod-table" },
          h("caption", { className: "botmod-sronly" }, "Bot scoreboard"),
          h("thead", null, h("tr", null,
            th("Bot", "name"), th("Weapon", "weapon"), th("HP", "health"),
            th("Kills P", "players"), th("Kills Z", "zombies"), th("Deaths", "deaths"),
            th("Score", "score"), th("Lvl", "level"), th("Near", "nearestPlayerDist"),
            th("Team", "team"),
            h("th", { key: "state" }, "State"), h("th", { key: "x" }, ""))),
          h("tbody", null, [...bots].sort(bySortKey(sort)).map((b): unknown =>
            botRow(h, b, busy, post, teamOptions, dragName, setDragName, setDropOver, changed(b.entityId))))));
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
  const [busy, setBusy] = React.useState("");
  const [spawnCount, setSpawnCount] = React.useState("2");
  const [nearPlayer, setNearPlayer] = React.useState("");
  const [nearCount, setNearCount] = React.useState("1");
  const [nearWeapon, setNearWeapon] = React.useState("");
  const [armed, setArmed] = React.useState(""); // destructive buttons: click to arm, click again to run
  const [announce, setAnnounce] = React.useState(""); // polite live region for state changes SR users would otherwise miss
  const [sort, setSort] = React.useState({ key: "score", dir: -1 });
  const [dragName, setDragName] = React.useState<string | null>(null); const [dropOver, setDropOver] = React.useState<number | null>(null); // dragged bot name + hovered team bucket

  if (query.isError === true) {
    const status = num(query.error?.response?.status);
    const msg = status === 403
      ? "Authentication required: log in to the dashboard as an admin (permission level 0) to control bots."
      : `Bot API unavailable (HTTP ${status === 0 ? "error" : String(status)}).`;
    return h("div", { className: "botmod-panel" },
      h("h2", null, "Bot Control"),
      h("span", { className: "botmod-pill botmod-bad", role: "status" }, "AUTH REQUIRED"),
      h("p", { role: "alert" }, msg),
      h("button", { className: "botmod-btn", onClick: (): void => { location.href = "/"; } }, "Log in"));
  }

  const s = unwrapSnap(query.data);
  const enabled = s.enabled === true;
  const bots = listOrEmpty<BotStat>(s.bots);
  const onlinePlayers = listOrEmpty<BotPlayer>(s.players);
  // Keep the selected target valid across refetches (players can leave); an
  // unset selection also picks the first player here.
  if (onlinePlayers.length > 0 && !onlinePlayers.some((p): boolean => p.name === nearPlayer)) {
    setNearPlayer(onlinePlayers[0].name);
  }
  const refetch = (): Promise<unknown> => (query.refetch === undefined ? Promise.resolve() : query.refetch());
  const post = (body: BotAction): void => postAction({ HTTP, busy, setBusy, setArmed, say: setAnnounce, refetch, body });
  const btn = makeBtn(h, busy, post);
  const armedBtn = makeArmedBtn(h, armed, setArmed, busy, setAnnounce, post);
  const pill = (on: boolean, onLabel: string, offLabel: string): unknown =>
    h("span", { className: `botmod-pill ${on ? "botmod-ok" : "botmod-off"}` }, on ? onLabel : offLabel);

  return h("div", { className: "botmod-panel", "aria-busy": busy !== "" },
    renderBotHeader(h, s, onlinePlayers, pill),
    // Screen-reader channel for arm/confirm and command-sent state changes;
    // role=status implies a polite live region.
    h("p", { key: "srstatus", className: "botmod-sronly", role: "status" }, announce),
    renderSpawnRow(h, enabled, busy, spawnCount, setSpawnCount, post, btn, armedBtn),
    renderSkillRow(h, s, busy, post),
    renderNearRow(h, onlinePlayers, nearPlayer, setNearPlayer, nearCount, setNearCount, nearWeapon, setNearWeapon, btn),
    renderBrainRow(h, s, busy, btn),
    renderTeamRow(h, s, busy, btn),
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
