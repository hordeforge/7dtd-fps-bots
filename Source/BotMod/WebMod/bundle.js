"use strict";
(() => {
    const modId = "BotMod";
    const POLL_INTERVAL_MS = 5000;
    const ARM_TIMEOUT_MS = 4000;
    function unwrapSnap(o) {
        if (typeof o !== "object" || o === null) {
            return {};
        }
        const outer = o;
        const data1 = outer.data;
        if (typeof data1 !== "object" || data1 === null) {
            return outer;
        }
        const inner = data1;
        const data2 = inner.data;
        if (typeof data2 !== "object" || data2 === null) {
            return inner;
        }
        return data2;
    }
    function numOr(v, fallback) {
        return typeof v === "number" && Number.isFinite(v) ? v : fallback;
    }
    const num = (v) => numOr(v, 0);
    function strOrEmpty(v) {
        return typeof v === "string" ? v : "";
    }
    function listOrEmpty(candidate) {
        if (Array.isArray(candidate)) {
            return candidate;
        }
        return [];
    }
    function optNum(v) {
        if (v === undefined || v === 0) {
            return "";
        }
        return String(v);
    }
    function toCount(v) {
        const n = Number.parseInt(v, 10);
        if (!Number.isFinite(n) || n <= 0) {
            return 1;
        }
        return n;
    }
    function newRequestId() {
        const c = typeof crypto === "undefined" ? undefined : crypto;
        if (c !== undefined && typeof c.randomUUID === "function") {
            return c.randomUUID();
        }
        return `botmod-${Date.now().toString(36)}-${Math.floor(Math.random() * 4294967296).toString(36)}`;
    }
    function postAction(opts) {
        if (opts.busy !== "") {
            return;
        }
        opts.setBusy(`${opts.body.action}${optNum(opts.body.count)}${optNum(opts.body.entityId)}`);
        opts.setArmed("");
        opts.say(`${opts.body.action}: command sent`);
        const body = Object.assign(Object.assign({}, opts.body), { requestId: newRequestId() });
        void opts.HTTP.post("/api/bot", body)
            .then(() => { void opts.refetch(); })
            .catch(() => {
            opts.setBusy("");
        })
            .then(() => {
            opts.setBusy("");
        });
    }
    function armOrRun(opts) {
        if (opts.armed === opts.label) {
            opts.onConfirm();
            return;
        }
        opts.setArmed(opts.label);
        opts.say(`${opts.label} armed, activate again within 4 seconds to confirm`);
        setTimeout(() => opts.setArmed((a) => (a === opts.label ? "" : a)), ARM_TIMEOUT_MS);
    }
    function makeBtn(h, busy, post) {
        return (label, body, cls) => h("button", {
            className: `botmod-btn${cls === undefined ? "" : ` ${cls}`}`,
            disabled: busy !== "",
            onClick: () => post(body)
        }, label);
    }
    function makeArmedBtn(h, armed, setArmed, busy, say, post) {
        return (label, body, cls) => {
            const isArmed = armed === label;
            return h("button", {
                className: `botmod-btn${cls === undefined ? "" : ` ${cls}`}${isArmed ? " botmod-armed" : ""}`,
                disabled: busy !== "",
                onClick: () => armOrRun({ armed, setArmed, label, say, onConfirm: () => post(body) })
            }, isArmed ? "Confirm?" : label);
        };
    }
    function bySortKey(sort) {
        return (a, b) => {
            const textKey = sort.key === "name" || sort.key === "weapon";
            const av = textKey ? strOrEmpty(a[sort.key]).toLowerCase() : numOr(a[sort.key], -1);
            const bv = textKey ? strOrEmpty(b[sort.key]).toLowerCase() : numOr(b[sort.key], -1);
            if (av < bv) {
                return -sort.dir;
            }
            if (av > bv) {
                return sort.dir;
            }
            return 0;
        };
    }
    function brainLabel(neural, neuralLoaded) {
        if (neural !== true) {
            return "static AI";
        }
        return neuralLoaded === true ? "GA" : "GA (not loaded)";
    }
    function nearLabel(b) {
        if (b.nearestPlayerDist === undefined || b.nearestPlayerDist < 0) {
            return "n/a";
        }
        return `${b.nearestPlayerDist}m${b.nearestPlayer === undefined ? "" : ` ${b.nearestPlayer}`}`;
    }
    const TEAM_COLORS = [
        "#9aa0a6", "#ff7070", "#8ab4f8", "#57d977", "#f9ab00", "#c58af9", "#4dd0e1", "#f48fb1", "#ffe082"
    ];
    const TEAM_LABELS = [
        "FFA", "Team 1", "Team 2", "Team 3", "Team 4", "Team 5", "Team 6", "Team 7", "Team 8"
    ];
    function teamColor(team) {
        return TEAM_COLORS[Math.min(numOr(team, 0), TEAM_COLORS.length - 1)];
    }
    function teamLabel(team) {
        return TEAM_LABELS[Math.min(numOr(team, 0), TEAM_LABELS.length - 1)];
    }
    function renderBotHeader(h, s, onlinePlayers, pill) {
        const playerSuffix = onlinePlayers.length > 1 ? "s" : "";
        const onlineText = onlinePlayers.length > 0
            ? `${onlinePlayers.length} player${playerSuffix} online (${onlinePlayers.map((p) => p.name).join(", ")})`
            : "no players online";
        return h("div", { className: "botmod-head" }, h("h2", null, "Bot Control"), pill(s.enabled === true, "ENABLED", "DISABLED"), h("span", { className: "botmod-window" }, `alive ${num(s.alive)}/${num(s.targetBotCount)} · max ${num(s.maxBots)} · brain ${brainLabel(s.neural, s.neuralLoaded)} · ${onlineText}`));
    }
    function renderSpawnRow(h, enabled, busy, spawnCount, setSpawnCount, post, btn, armedBtn) {
        return h("div", { className: "botmod-row" }, armedBtn(enabled ? "Disable" : "Enable", { action: enabled ? "disable" : "enable" }, enabled ? "botmod-danger" : "botmod-primary"), armedBtn("Remove all", { action: "remove" }, "botmod-danger"), h("input", {
            className: "botmod-num", type: "number", min: 1, max: 16, value: spawnCount,
            "aria-label": "Bots to spawn",
            onChange: (e) => setSpawnCount(e.target.value)
        }), btn("Spawn", { action: "spawn", count: toCount(spawnCount) }, "botmod-primary"), [1, 4, 8].map((n) => h("button", { key: n, className: "botmod-btn", disabled: busy !== "", onClick: () => post({ action: "spawn", count: n }) }, `+${n}`)));
    }
    function renderSkillRow(h, s, busy, post) {
        return h("div", { className: "botmod-row botmod-brain" }, h("span", { className: "botmod-label" }, "Skill:"), [0, 1, 2, 3, 4].map((d) => h("button", {
            key: d, className: `botmod-btn${s.difficulty === d ? " botmod-primary" : ""}`, disabled: busy !== "",
            onClick: () => post({ action: "skill", level: d })
        }, String(d))), h("span", { className: "botmod-window" }, "0 bot · 1 easy · 2 normal · 3 hard · 4 nightmare"));
    }
    function renderNearRow(h, onlinePlayers, nearPlayer, setNearPlayer, nearCount, setNearCount, nearWeapon, setNearWeapon, btn) {
        return h("div", { className: "botmod-row" }, h("span", { className: "botmod-label" }, "Near player:"), onlinePlayers.length === 0
            ? h("span", { className: "botmod-window" }, "no players online")
            : h("select", {
                className: "botmod-select", value: nearPlayer,
                "aria-label": "Player",
                onChange: (e) => setNearPlayer(e.target.value)
            }, onlinePlayers.map((p) => h("option", { key: p.entityId, value: p.name }, p.name))), h("input", {
            className: "botmod-num", type: "number", min: 1, max: 16, value: nearCount,
            "aria-label": "Bots to spawn near player",
            onChange: (e) => setNearCount(e.target.value)
        }), h("input", {
            className: "botmod-weapon", type: "text", placeholder: "weapon (opt)", value: nearWeapon,
            "aria-label": "Weapon (optional)",
            onChange: (e) => setNearWeapon(e.target.value)
        }), btn("Spawn near", {
            action: "spawnNear", player: nearPlayer,
            count: toCount(nearCount),
            weapon: nearWeapon === "" ? undefined : nearWeapon
        }, "botmod-primary"));
    }
    function renderBrainRow(h, s, busy, btn) {
        return h("div", { className: "botmod-row botmod-brain" }, h("span", { className: "botmod-label" }, "Brain:"), btn(s.neural === true ? "Static AI" : "GA brain", { action: "neural", on: s.neural !== true }), s.neuralPath !== undefined && s.neuralPath !== "" ? h("span", { className: "botmod-window" }, `weights: ${s.neuralPath}`) : null);
    }
    function renderTeamRow(h, s, busy, btn) {
        const team = s.botTeam === true;
        return h("div", { className: "botmod-row botmod-brain" }, h("span", { className: "botmod-label" }, "Squad:"), btn(team ? "Free-for-all" : "Squad mode", { action: "team", on: !team }, team ? "botmod-primary" : ""), h("span", { className: "botmod-window" }, team ? "all bots are allies" : "bots fight each other"));
    }
    function renderVsRow(h, s, busy, post) {
        const toggles = [
            { label: "Bots", target: "bot", on: s.botVsBot === true },
            { label: "Zombies", target: "zombie", on: s.botVsZombie === true },
            { label: "Players", target: "player", on: s.botVsPlayer === true }
        ];
        return h("div", { className: "botmod-row botmod-brain" }, h("span", { className: "botmod-label" }, "Shoot at:"), toggles.map((t) => h("button", {
            key: t.target, className: `botmod-btn${t.on ? " botmod-primary" : ""}`, disabled: busy !== "",
            onClick: () => post({ action: "vs", target: t.target, on: !t.on })
        }, `${t.label}${t.on ? "" : " OFF"}`)), h("span", { className: "botmod-window" }, "squad mode overrides vs Bots"));
    }
    function renderTeamsCard(h, s, bots, busy, post, armedBtn, dragName, setDragName, dropOver, setDropOver) {
        const teamCount = Math.max(0, Math.min(8, numOr(s.teamCount, 2)));
        const buckets = [];
        for (let t = 0; t <= teamCount; t++) {
            buckets.push({
                team: t,
                label: teamLabel(t),
                color: teamColor(t),
                members: bots.filter((b) => numOr(b.team, 0) === t)
            });
        }
        return h("div", { className: "botmod-row botmod-brain botmod-teams" }, h("span", { className: "botmod-label" }, "Teams:"), buckets.map((bkt) => h("div", {
            key: bkt.team,
            className: `botmod-bucket${dropOver === bkt.team ? " botmod-drop-active" : ""}`,
            style: { borderColor: bkt.color },
            onDragOver: (e) => {
                e.preventDefault();
                if (dragName !== null) {
                    setDropOver(bkt.team);
                }
            },
            onDragLeave: () => {
                if (dropOver === bkt.team) {
                    setDropOver(null);
                }
            },
            onDrop: () => {
                if (dragName !== null && dragName !== "") {
                    post({ action: "setTeam", name: dragName, team: bkt.team });
                }
                setDropOver(null);
                setDragName(null);
            }
        }, h("span", { className: "botmod-bucket-head", style: { color: bkt.color } }, `${bkt.label} · ${bkt.members.length}`), bkt.members.length === 0
            ? h("span", { className: "botmod-bucket-empty" }, "drag a bot here")
            : bkt.members.map((b) => h("span", {
                key: b.entityId,
                className: "botmod-chip",
                draggable: true,
                onDragStart: () => setDragName(b.name),
                onDragEnd: () => setDragName(null)
            }, b.name)))), h("button", {
            className: "botmod-btn", title: "Fewer teams", disabled: busy !== "" || teamCount <= 0,
            onClick: () => post({ action: "teamCount", count: teamCount - 1 })
        }, "− teams"), h("button", {
            className: "botmod-btn", title: "More teams", disabled: busy !== "" || teamCount >= 8,
            onClick: () => post({ action: "teamCount", count: teamCount + 1 })
        }, "+ teams"), armedBtn("Clear teams", { action: "clearTeams" }, "botmod-danger"), h("span", { className: "botmod-window" }, "drag a bot onto a team (or use its Team column) · picks persist"));
    }
    function renderConfigRow(h, s) {
        return h("div", { className: "botmod-row botmod-cfg" }, h("span", { className: "botmod-window" }, `vision ${num(s.visionRange)}m · attack ${num(s.attackRange)}m · spawn r ${num(s.spawnRadius)}m` +
            ` · strafe ${Math.round(num(s.strafeChance) * 100)}% · dodge ${Math.round(num(s.dodgeOnHitChance) * 100)}%` +
            `${s.botVsBot === true ? " · vsBot" : ""} · hp ${num(s.botHealth)}`));
    }
    function ariaSortValue(sort, key) {
        if (sort.key !== key) {
            return "none";
        }
        return sort.dir < 0 ? "descending" : "ascending";
    }
    function sortArrowNode(h, sort, key) {
        if (sort.key !== key) {
            return null;
        }
        return h("span", { key: "arrow", "aria-hidden": "true" }, sort.dir < 0 ? " ▼" : " ▲");
    }
    function botRow(h, b, busy, post, teamOptions, dragName, setDragName, setDropOver, changedSig) {
        let rowClass = "";
        if (changedSig !== null) {
            rowClass = "botmod-flash";
            if (dragName === b.name) {
                rowClass += " botmod-drag";
            }
        }
        else if (dragName === b.name) {
            rowClass = "botmod-drag";
        }
        return h("tr", {
            key: changedSig === null ? String(b.entityId) : `${b.entityId}:${changedSig}`,
            draggable: true,
            className: rowClass,
            title: "Drag onto a team bucket",
            onDragStart: (e) => {
                e.dataTransfer.setData("text/plain", b.name);
                e.dataTransfer.effectAllowed = "move";
                setDragName(b.name);
            },
            onDragEnd: () => {
                setDragName(null);
                setDropOver(null);
            }
        }, h("td", null, h("span", { className: "botmod-teamdot", style: { background: teamColor(b.team) }, "aria-hidden": "true" }), b.name), h("td", null, b.weapon), h("td", null, b.health), h("td", null, b.players), h("td", null, b.zombies), h("td", null, b.deaths), h("td", null, b.score), h("td", null, b.level), h("td", null, nearLabel(b)), h("td", null, h("select", {
            className: "botmod-teamsel", value: String(numOr(b.team, 0)), disabled: busy !== "",
            "aria-label": `Team for ${b.name}`,
            onChange: (e) => post({ action: "setTeam", name: b.name, team: Number.parseInt(e.target.value, 10) })
        }, teamOptions)), h("td", { className: "botmod-state" }, b.status), h("td", null, h("button", {
            className: "botmod-btn botmod-danger botmod-remove", title: "Remove bot",
            "aria-label": `Remove bot ${b.name}`,
            disabled: busy !== "", onClick: () => post({ action: "removeOne", entityId: b.entityId })
        }, "✕")));
    }
    function rowSig(b) {
        return `${numOr(b.health, -1)}|${b.status}|${numOr(b.team, 0)}|${numOr(b.players, 0)}|${numOr(b.zombies, 0)}|${numOr(b.deaths, 0)}|${numOr(b.score, 0)}|${numOr(b.level, 0)}`;
    }
    let prevRowSigs = new Map();
    function renderScoreboard(h, s, bots, busy, post, sort, setSort, dragName, setDragName, setDropOver) {
        const th = (label, key) => h("th", {
            key: label,
            className: "botmod-sortable",
            "aria-sort": ariaSortValue(sort, key)
        }, h("button", {
            className: "botmod-sort-btn",
            onClick: () => setSort((srt) => ({ key, dir: srt.key === key ? -srt.dir : -1 }))
        }, label, sortArrowNode(h, sort, key)));
        const teamCount = Math.max(0, Math.min(8, numOr(s.teamCount, 2)));
        const teamOptions = [];
        for (let t = 0; t <= teamCount; t++) {
            teamOptions.push(h("option", { key: t, value: String(t) }, teamLabel(t)));
        }
        const sigs = new Map();
        for (const b of bots) {
            sigs.set(b.entityId, rowSig(b));
        }
        const changed = (id) => {
            const current = sigs.get(id);
            if (prevRowSigs.size === 0 || current === undefined || prevRowSigs.get(id) === current) {
                return null;
            }
            return current;
        };
        prevRowSigs = sigs;
        return h("div", { className: "botmod-scoreboard" }, h("h3", null, `Scoreboard (${bots.length}) · drag rows onto a team or use the Team column`), bots.length === 0
            ? h("p", { className: "botmod-empty" }, "No bots alive.")
            : h("table", { className: "botmod-table" }, h("caption", { className: "botmod-sronly" }, "Bot scoreboard"), h("thead", null, h("tr", null, th("Bot", "name"), th("Weapon", "weapon"), th("HP", "health"), th("Kills P", "players"), th("Kills Z", "zombies"), th("Deaths", "deaths"), th("Score", "score"), th("Lvl", "level"), th("Near", "nearestPlayerDist"), th("Team", "team"), h("th", { key: "state" }, "State"), h("th", { key: "x" }, ""))), h("tbody", null, [...bots].sort(bySortKey(sort)).map((b) => botRow(h, b, busy, post, teamOptions, dragName, setDragName, setDropOver, changed(b.entityId))))));
    }
    function BotPanel({ React, HTTP, useQuery }) {
        var _a, _b;
        const h = React.createElement;
        const [blocked, setBlocked] = React.useState(false);
        const query = useQuery("botmod-status", () => HTTP.get("/api/bot"), {
            refetchInterval: POLL_INTERVAL_MS,
            enabled: !blocked,
            retry: false
        });
        React.useEffect(() => {
            var _a, _b;
            const status = num((_b = (_a = query.error) === null || _a === void 0 ? void 0 : _a.response) === null || _b === void 0 ? void 0 : _b.status);
            if (query.isError === true && (status === 401 || status === 403)) {
                setBlocked(true);
            }
        }, [query.isError, query.error]);
        const [busy, setBusy] = React.useState("");
        const [spawnCount, setSpawnCount] = React.useState("2");
        const [nearPlayer, setNearPlayer] = React.useState("");
        const [nearCount, setNearCount] = React.useState("1");
        const [nearWeapon, setNearWeapon] = React.useState("");
        const [armed, setArmed] = React.useState("");
        const [announce, setAnnounce] = React.useState("");
        const [sort, setSort] = React.useState({ key: "score", dir: -1 });
        const [dragName, setDragName] = React.useState(null);
        const [dropOver, setDropOver] = React.useState(null);
        if (query.isError === true) {
            const status = num((_b = (_a = query.error) === null || _a === void 0 ? void 0 : _a.response) === null || _b === void 0 ? void 0 : _b.status);
            const msg = status === 403
                ? "Authentication required: log in to the dashboard as an admin (permission level 0) to control bots."
                : `Bot API unavailable (HTTP ${status === 0 ? "error" : String(status)}).`;
            return h("div", { className: "botmod-panel" }, h("h2", null, "Bot Control"), h("span", { className: "botmod-pill botmod-bad", role: "status" }, "AUTH REQUIRED"), h("p", { role: "alert" }, msg), h("button", { className: "botmod-btn", onClick: () => { location.href = "/"; } }, "Log in"));
        }
        const s = unwrapSnap(query.data);
        const enabled = s.enabled === true;
        const bots = listOrEmpty(s.bots);
        const onlinePlayers = listOrEmpty(s.players);
        if (onlinePlayers.length > 0 && !onlinePlayers.some((p) => p.name === nearPlayer)) {
            setNearPlayer(onlinePlayers[0].name);
        }
        const refetch = () => (query.refetch === undefined ? Promise.resolve() : query.refetch());
        const post = (body) => postAction({ HTTP, busy, setBusy, setArmed, say: setAnnounce, refetch, body });
        const btn = makeBtn(h, busy, post);
        const armedBtn = makeArmedBtn(h, armed, setArmed, busy, setAnnounce, post);
        const pill = (on, onLabel, offLabel) => h("span", { className: `botmod-pill ${on ? "botmod-ok" : "botmod-off"}` }, on ? onLabel : offLabel);
        return h("div", { className: "botmod-panel", "aria-busy": busy !== "" }, renderBotHeader(h, s, onlinePlayers, pill), h("p", { key: "srstatus", className: "botmod-sronly", role: "status" }, announce), renderSpawnRow(h, enabled, busy, spawnCount, setSpawnCount, post, btn, armedBtn), renderSkillRow(h, s, busy, post), renderNearRow(h, onlinePlayers, nearPlayer, setNearPlayer, nearCount, setNearCount, nearWeapon, setNearWeapon, btn), renderBrainRow(h, s, busy, btn), renderTeamRow(h, s, busy, btn), renderVsRow(h, s, busy, post), renderTeamsCard(h, s, bots, busy, post, armedBtn, dragName, setDragName, dropOver, setDropOver), renderConfigRow(h, s), renderScoreboard(h, s, bots, busy, post, sort, setSort, dragName, setDragName, setDropOver));
    }
    const loggedIn = document.cookie.split(";").some((c) => c.trim().startsWith("sid="));
    const webMod = {
        about: "FPS bots: enable/disable, spawn, static AI vs GA brain, drag-and-drop teams, scoreboard.",
        routes: loggedIn ? { "Bot": BotPanel } : {},
        settings: {},
        mapComponents: []
    };
    Object.assign(globalThis, { [modId]: webMod });
    globalThis.dispatchEvent(new Event(`mod:${modId}:ready`));
})();
