// BotMod WebMod (TypeScript source), compiled to bundle.js by
// `tsc -p Source/BotMod/WebMod/tsconfig.json` (wired into scripts/build.sh).
// The dashboard loads /webmods/BotMod/bundle.js and reads window["BotMod"]:
// the "Bot" route is a direct sidebar entry (hidden until the sid session
// cookie is present). Do not hand-edit bundle.js; regenerate from this file.
//
// The whole body is an IIFE on purpose: webmod bundles are plain <script> tags
// sharing the global scope, and a bare top-level const (e.g. modId) collides
// across mods (SyntaxError kills the later bundle's registration).
(() => {

type Any = any;

interface BotStat {
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
}
interface BotPlayer {
  name: string;
  entityId: number;
}
interface BotStatus {
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
  players?: BotPlayer[];
  bots?: BotStat[];
}

interface PanelProps {
  React: {
    createElement: (...args: Any[]) => Any;
    useRef: <T>(init: T) => { current: T };
    useState: <T>(init: T) => [T, (v: T | ((prev: T) => T)) => void];
    useEffect: (fn: () => Any, deps?: Any[]) => Any;
  };
  HTTP: { get: (url: string) => Promise<Any>; post: (url: string, body?: Any) => Promise<Any> };
  useQuery: (key: string, fn: () => Promise<Any>, opts?: {
    refetchInterval?: number;
    enabled?: boolean;
    retry?: boolean;
  }) => {
    data?: Any;
    isError?: boolean;
    error?: { response?: { status?: number } };
  };
}

const modId = "BotMod";

// The dashboard HTTP wrapper may hand us the axios response, the {data: ...}
// envelope, or the bare payload; accept all three.
function unwrapSnap(o: Any): Any {
  const s = o && o.data && typeof o.data === "object" ? o.data : null;
  if (s && s.data && typeof s.data === "object") return s.data;
  if (s && (s.enabled || s.alive !== undefined || s.bots)) return s;
  return o || {};
}

function BotPanel({ React, HTTP, useQuery }: PanelProps): Any {
  const h = React.createElement;

  // Stop polling after the first auth failure instead of hammering the API.
  const [blocked, setBlocked] = React.useState(false);
  const query = useQuery("botmod-status", async () => HTTP.get("/api/bot"), {
    refetchInterval: 5000,
    enabled: !blocked,
    retry: false
  });
  React.useEffect(() => { if (query.isError) setBlocked(true); }, [query.isError]);
  const [busy, setBusy] = React.useState("");
  const [spawnCount, setSpawnCount] = React.useState("2");
  const [nearPlayer, setNearPlayer] = React.useState("");
  const [nearCount, setNearCount] = React.useState("1");
  const [nearWeapon, setNearWeapon] = React.useState("");

  if (query.isError) {
    const status = (query.error && query.error.response && query.error.response.status) || 0;
    const msg = status === 403
      ? "Authentication required: log in to the dashboard as an admin (permission level 0) to control bots."
      : "Bot API unavailable (HTTP " + (status || "error") + ").";
    return h("div", { className: "botmod-panel" },
      h("h2", null, "Bot Control"),
      h("span", { className: "botmod-pill botmod-bad" }, "AUTH REQUIRED"),
      h("p", null, msg),
      h("button", { className: "botmod-btn", onClick: () => { location.href = "/"; } }, "Log in"));
  }

  const s: BotStatus = unwrapSnap(query.data);
  const enabled = !!s.enabled;
  const bots = s.bots || [];
  const onlinePlayers = s.players || [];
  // Keep the selected target valid across refetches (players can leave).
  if (nearPlayer && onlinePlayers.length > 0 && !onlinePlayers.some((p) => p.name === nearPlayer)) {
    setNearPlayer(onlinePlayers[0].name);
  }
  if (!nearPlayer && onlinePlayers.length > 0) {
    setNearPlayer(onlinePlayers[0].name);
  }

  const act = async (body: Any) => {
    if (busy) return;
    setBusy(body.action + (body.count || ""));
    try { await HTTP.post("/api/bot", body); }
    catch (e) { /* query refetch shows the real state */ }
    setBusy("");
  };

  const btn = (label: string, body: Any, cls?: string) =>
    h("button", { className: "botmod-btn" + (cls ? " " + cls : ""), disabled: !!busy, onClick: () => act(body) }, label);

  const pill = (on: boolean, onLabel: string, offLabel: string) =>
    h("span", { className: "botmod-pill " + (on ? "botmod-ok" : "botmod-off") }, on ? onLabel : offLabel);

  return h("div", { className: "botmod-panel" },
    h("div", { className: "botmod-head" },
      h("h2", null, "Bot Control"),
      pill(enabled, "ENABLED", "DISABLED"),
      h("span", { className: "botmod-window" },
        "alive " + (s.alive || 0) + "/" + (s.targetBotCount || 0) + " · max " + (s.maxBots || 0) +
        " · diff " + (s.difficulty || 0) + " · " + (s.weapon || "?") + " · brain " +
        (s.neural ? (s.neuralLoaded ? "GA" : "GA (not loaded)") : "static AI"))),

    h("div", { className: "botmod-row" },
      btn(enabled ? "Disable" : "Enable", { action: enabled ? "disable" : "enable" },
        enabled ? "botmod-danger" : "botmod-primary"),
      btn("Remove all", { action: "remove" }, "botmod-danger"),
      h("input", {
        className: "botmod-num", type: "number", min: 1, max: 16, value: spawnCount,
        onChange: (e: Any) => setSpawnCount(e.target.value)
      }),
      btn("Spawn bots", { action: "spawn", count: parseInt(spawnCount, 10) || 1 }, "botmod-primary")),

    h("div", { className: "botmod-row" },
      h("span", { className: "botmod-label" }, "Near player:"),
      onlinePlayers.length === 0
        ? h("span", { className: "botmod-window" }, "no players online")
        : h("select", {
            className: "botmod-select", value: nearPlayer,
            onChange: (e: Any) => setNearPlayer(e.target.value)
          }, onlinePlayers.map((p) => h("option", { key: p.entityId, value: p.name }, p.name))),
      h("input", {
        className: "botmod-num", type: "number", min: 1, max: 16, value: nearCount,
        onChange: (e: Any) => setNearCount(e.target.value)
      }),
      h("input", {
        className: "botmod-weapon", type: "text", placeholder: "weapon (opt)", value: nearWeapon,
        onChange: (e: Any) => setNearWeapon(e.target.value)
      }),
      btn("Spawn near", {
        action: "spawnNear", player: nearPlayer,
        count: parseInt(nearCount, 10) || 1,
        weapon: nearWeapon || undefined
      }, "botmod-primary")),

    h("div", { className: "botmod-row botmod-brain" },
      h("span", { className: "botmod-label" }, "Brain:"),
      btn(s.neural ? "Static AI" : "GA brain", { action: "neural", on: !s.neural }),
      s.neuralPath ? h("span", { className: "botmod-window" }, "weights: " + s.neuralPath) : null),

    h("h3", null, "Scoreboard"),
    bots.length === 0
      ? h("p", { className: "botmod-empty" }, "No bots alive.")
      : h("table", { className: "botmod-table" },
          h("thead", null, h("tr", null,
            ["Bot", "Weapon", "HP", "Kills P", "Kills Z", "Deaths", "Score", "Lvl", "Near", "State"].map((x) => h("th", { key: x }, x)))),
          h("tbody", null, bots.map((b) =>
            h("tr", { key: b.entityId },
              h("td", null, b.name),
              h("td", null, b.weapon),
              h("td", null, b.health),
              h("td", null, b.players),
              h("td", null, b.zombies),
              h("td", null, b.deaths),
              h("td", null, b.score),
              h("td", null, b.level),
              h("td", null, b.nearestPlayerDist !== undefined && b.nearestPlayerDist >= 0
                ? b.nearestPlayerDist + "m" + (b.nearestPlayer ? " " + b.nearestPlayer : "")
                : "—"),
              h("td", { className: "botmod-state" }, b.status))))));
}

// Menu entry registered only when the web session cookie is present; the
// dashboard reloads the page after login/logout, so this re-evaluates.
const loggedIn = document.cookie.split(";").some((c) => c.trim().startsWith("sid="));
const webMod: Any = {
  about: "FPS bots: enable/disable, spawn, static AI vs GA brain, scoreboard.",
  routes: loggedIn ? { "Bot": BotPanel } : {},
  settings: {},
  mapComponents: []
};
(window as Any)[modId] = webMod;
window.dispatchEvent(new Event(`mod:${modId}:ready`));
})();
