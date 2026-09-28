using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using Utf8Json;
using Webserver;
using Webserver.WebAPI;
using BotMod.Config;
using BotMod.Core;
using BotMod.Foundation;
using UnityEngine;

namespace BotMod.Web
{
    /// <summary>
    /// Admin web API for the bot mod: GET /api/bot reports config + alive bots
    /// with the stock entity scoreboard stats; POST /api/bot drives the same
    /// paths as the `bot` console command. Actions: enable, disable, spawn,
    /// spawnNear, remove (alias clear), removeOne, skill, neural, team, vs,
    /// setTeam, teamCount, clearTeams.
    /// The menu entry and data are admin-only (permission level 0).
    ///
    /// Threading: handlers run on web thread pool threads, but every action
    /// body (config mutation included) executes via RunOnMain on the game's
    /// main thread, serialized with console commands and the tick loop; see
    /// HandleRestPost.
    ///
    /// Duplicate semantics: POST bodies accept an optional client-generated
    /// "requestId" idempotency key. Retries reusing a key within the ledger
    /// retention window replay the recorded response instead of executing
    /// again (a retried spawn does not spawn twice); a concurrent duplicate
    /// with the same key gets 409 REQUEST_IN_PROGRESS; a key reused for a
    /// different body gets 409 REQUEST_ID_REUSED, because replaying the first
    /// request's response would report an operation the caller never asked for
    /// and drop the one it did; failures are not cached, so a retry may run
    /// again. The one ambiguous outcome is a
    /// main-thread dispatch timeout: the queued work still runs afterwards,
    /// so that key stays claimed (retries get 409 until the entry ages out)
    /// rather than risking a second execution. Without a key, execution is
    /// unchanged and repeated calls repeat the effect.
    ///
    /// Validation: optional numeric fields fall back to their documented
    /// defaults only when absent (JSON null counts as absent); a value that
    /// is present but malformed rejects the whole request with 400 and a
    /// named INVALID_* code instead of silently executing something else.
    /// Required fields (spawnNear's player, removeOne's entityId, setTeam's
    /// name, vs's target, the toggles' on flag) reject absence the same way,
    /// each action checking the field that names its target before the ones
    /// that qualify it. A team name is stored in a map with a hard cap
    /// (BotConfig.SetTeamAssignment), so setTeam answers 400 INVALID_NAME for
    /// an empty or over-long name and 400 TEAM_ASSIGNMENT_LIMIT once the map
    /// is full, rather than reporting a team that was never stored. A requestId
    /// that is present but
    /// unusable (empty or over the ledger key limit) is INVALID_REQUEST_ID:
    /// the caller must learn its retry protection is not active. The key is
    /// canonicalized through BotText.IdentityKey before any of its uses (ledger
    /// claim, audit tag, response header), so an NFD spelling or a pasted
    /// zero-width character cannot split one logical key into two.
    /// Range clamping (count 1..16, skill 0..4, teams 0..8) stays shared with the
    /// console command's setters in BotConfig.
    ///
    /// Correlation: every POST audit line names the request (the client
    /// idempotency key, or a server-side "auto-N" tag when none was sent), and
    /// the same tag is returned in the X-BotMod-Request-Id response header on
    /// every outcome, so a failed call is traceable to one log line. GET tags
    /// itself the same way ("auto-N": the verb carries no client key) so a
    /// failing or slow status poll names itself in the log and in the header
    /// the caller holds. The line also names the caller (see CallerTag):
    /// permission level 0 says an admin acted, not which one, and a stolen
    /// webtoken has to be attributable to the token row an operator can
    /// revoke. The `bot` console command logs its issuer the same way, so both
    /// admin surfaces reconstruct alike.
    ///
    /// CSRF: the dashboard authenticates with the stock `sid` session cookie,
    /// which a browser attaches to a cross-origin POST just as it does to a
    /// same-origin one, so cookie auth alone does not prove the call came from
    /// this dashboard. Every POST here is a mutation (spawn, remove, enable,
    /// config writes), so a page on any origin could otherwise drive an admin's
    /// server. The POST handler therefore refuses a request whose Origin does
    /// not name the host the request arrived on (see OriginAllowed). An absent
    /// Origin is allowed, because the non-browser callers (curl, the API-token
    /// shape) send none and refusing them would break every scripted admin path
    /// to close a browser-only hole. GET is not gated: it mutates nothing.
    ///
    /// Caching: every response (200, 400, 409, 500, on both verbs) carries
    /// Cache-Control: no-store plus X-Content-Type-Options: nosniff; see
    /// MarkNoStore. The bodies are live state, not a stable representation of
    /// anything.
    /// </summary>
    public sealed class Bot : AbsRestApi
    {
        static Bot()
        {
            // Observability sinks for the two silent failure modes of the
            // dispatch/ledger plumbing (both hooks are null in headless unit
            // runs and wired here once for the server):
            // - a dispatch whose caller already timed out still runs later;
            //   its outcome must reach the log or a spawn that "failed" with
            //   a 500 can actually have executed (or failed afterwards).
            // - ledger capacity overflow silently drops the oldest idempotency
            //   keys, so retries reusing them execute again instead of
            //   replaying; sustained overflow means a runaway or abusive
            //   client and must be visible.
            MainThreadDispatch.Abandoned = (op, error) => ModApi.Warn(
                "web api dispatch '" + LogSanitizer.Clean(op) + "' ran after its caller timed out"
                + (error != null ? " and failed: " + error : " (completed; response was lost)"));
            IdempotencyLedger.CapacityEvicted = n => ModApi.Warn(
                "idempotency ledger at capacity: evicted " + n + " oldest entries; retries reusing those requestId values will re-execute");
        }

        public Bot() : base(null) { }

        public override void HandleRestGet(RequestContext context)
        {
            MarkNoStore(context);
            // Correlation on the read path as well: the dashboard polls this
            // every few seconds, so a failing or slow build was logged with no
            // way to tie the line back to the client that saw it, and the
            // response carried no id to quote. Same server-side tag and same
            // response header the POST handler uses, so both verbs name
            // themselves one way. GET carries no client idempotency key, and
            // the tag costs one increment; a healthy poll still logs nothing.
            string reqTag = LogSanitizer.Clean(NextRequestTag());
            context.Response.Headers["X-BotMod-Request-Id"] = reqTag;
            PrepareEnvelopedResult(out JsonWriter writer);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                writer.WriteRaw(Encoding.UTF8.GetBytes(RunOnMain(() => TimedStatus(reqTag), "status")));
            }
            catch (Exception ex)
            {
                // Same contract as the POST handler below: full detail to the
                // server log, generic 500 envelope to the client. Without this
                // a timed-out or failing status build escapes HandleRestGet
                // unhandled (dispatch timeouts are expected here: RunOnMain
                // throws TimeoutException when the main thread is stuck).
                ModApi.Error("web api status req=" + reqTag + " failed 500 after " + sw.ElapsedMilliseconds + "ms: " + ex);
                SendEmptyResponse(context, HttpStatusCode.InternalServerError, null, "ERROR", null);
                return;
            }
            SendEnvelopedResult(context, ref writer, HttpStatusCode.OK, null, null, null);
        }

        public override void HandleRestPost(RequestContext context, IDictionary<string, object> _jsonInput, byte[] _jsonInputData)
        {
            MarkNoStore(context);
            PrepareEnvelopedResult(out JsonWriter writer);
            string action = RequestFields.OptString(_jsonInput, "action")?.ToLowerInvariant();
            // Optional idempotency key: one per logical request, reused across
            // retries (see class doc + IdempotencyLedger). Present but unusable
            // is a 400: silently degrading to keyless execution would leave the
            // caller believing retries replay when they would re-execute.
            //
            // Canonicalized once, here, before any of its uses (validity check,
            // ledger claim, audit tag, response header) so they cannot disagree.
            // The ledger compares keys ordinally, and a client that re-serialized
            // its key from an NFD name, or that pasted one carrying a
            // zero-width character, sent a different string for the same logical
            // key: the retry missed the entry entirely and the action ran a
            // second time (two spawns, two persisted writes) instead of
            // replaying, which is the exact failure the ledger exists to
            // prevent. Same policy as every other name-keyed surface
            // (BotText.IdentityKey: no control/invisible characters, then NFC).
            string requestIdRaw = RequestFields.OptString(_jsonInput, "requestId");
            string requestId = requestIdRaw == null ? null : BotText.IdentityKey(requestIdRaw);
            bool keyed = requestId != null;
            // One audit line per executed/replayed/rejected mutation; GET stays
            // unlogged because the dashboard polls it continuously.
            // Keyless requests (curl, an older panel, a script) still get a
            // server-side tag so every line of such a request is greppable and
            // quotable back to the caller; a client-supplied key keeps priority
            // so a retry is identifiable as the same logical request.
            string reqTag = keyed ? requestId : NextRequestTag();
            // Log-safe copies: requestId/action are request-supplied and must
            // not carry control characters into the audit trail (see
            // LogSanitizer). The raw values still drive routing and the ledger.
            string logAction = LogSanitizer.Clean(action);
            string logTag = LogSanitizer.Clean(reqTag);
            // Who ran it. The request tag identifies a retry, not an issuer:
            // without the principal on the line, a stolen webtoken or a shared
            // admin account is unattributable after the fact, which is the one
            // thing an escalation audit cannot be reconstructed without. The
            // console surface already names its issuer (SenderTag); this is the
            // same identity on the web surface, so both admin paths reconstruct
            // the same way. Resolved once, before any exit.
            string caller = CallerTag(context);
            // Correlation, not decoration: the tag reaches the client in a
            // response header on every outcome (200/400/409/500) so a failed
            // call can be tied back to the exact server log line, which for a
            // keyless request carries no other identifier. Sanitized value, so
            // a request-supplied key cannot inject header structure.
            context.Response.Headers["X-BotMod-Request-Id"] = logTag;
            // Cross-site request forgery. Every POST here is a mutation an
            // authenticated admin owns (spawn, remove, enable, config writes),
            // and the dashboard authenticates with the stock `sid` session
            // cookie, which the browser attaches to a cross-origin POST exactly
            // as it does to a same-origin one. Nothing else in the request
            // proves the call came from this dashboard: AbsRestApi's own
            // dispatch reads the cookie and the permission level, and the
            // attacker does not need either, because the victim's browser
            // supplies them. So a page on any origin could spawn bots, remove
            // them, or flip the mod off, and the victim would see a normal
            // dashboard and a 200 the script cannot read. The Origin check below
            // is the standard answer and needs no token in the panel: a browser
            // always sends Origin on a cross-origin POST, and sends it as the
            // panel's own origin on a same-origin one.
            //
            // The mod is a server plugin with no way to read the stock
            // webserver's cookie flags, so this cannot be delegated upward; it
            // has to hold here. An absent Origin stays allowed, because the
            // non-browser callers (curl, the API-token shape named in
            // CallerTag, a health probe) send none, and refusing them would
            // break every scripted admin path for a browser-only threat. A
            // present-but-unparseable Origin is refused: "null" (a sandboxed
            // iframe, a data: document) and a relative value are what a
            // cross-origin forger falls back to when a strict origin refuses to
            // emit the real one.
            if (!OriginAllowed(context))
            {
                ModApi.Log("web api action=" + logAction + " req=" + logTag + " caller=" + caller
                    + " rejected CROSS_ORIGIN (Origin: " + LogSanitizer.Clean(RequestHeader(context, "Origin")) + ")");
                SendEmptyResponse(context, HttpStatusCode.Forbidden, null, "CROSS_ORIGIN", null);
                return;
            }
            if (keyed && !IdempotencyLedger.IsValidKey(requestId))
            {
                ModApi.Log("web api action=" + logAction + " req=" + logTag + " caller=" + caller + " rejected INVALID_REQUEST_ID");
                SendEmptyResponse(context, HttpStatusCode.BadRequest, null, "INVALID_REQUEST_ID", null);
                return;
            }
            if (keyed)
            {
                string cached;
                IdempotencyLedger.BeginResult begin = IdempotencyLedger.TryBegin(
                    requestId, RequestFields.Fingerprint(_jsonInput, "requestId"), out cached);
                if (begin == IdempotencyLedger.BeginResult.Replay)
                {
                    ModApi.Log("web api action=" + logAction + " req=" + logTag + " caller=" + caller + " replay (cached response resent)");
                    writer.WriteRaw(Encoding.UTF8.GetBytes(cached));
                    SendEnvelopedResult(context, ref writer, HttpStatusCode.OK, null, null, null);
                    return;
                }
                if (begin == IdempotencyLedger.BeginResult.InProgress)
                {
                    ModApi.Log("web api action=" + logAction + " req=" + logTag + " caller=" + caller + " rejected REQUEST_IN_PROGRESS");
                    SendEmptyResponse(context, HttpStatusCode.Conflict, null, "REQUEST_IN_PROGRESS", null);
                    return;
                }
                // The key is live for a different request. Replaying the other
                // request's response would report a spawn/enable/... that this
                // body never asked for as this one's result and drop the actual
                // operation; the original claim stays intact, so a retry of the
                // original request still replays.
                if (begin == IdempotencyLedger.BeginResult.Mismatched)
                {
                    ModApi.Log("web api action=" + logAction + " req=" + logTag + " caller=" + caller + " rejected REQUEST_ID_REUSED (key already used for a different request)");
                    SendEmptyResponse(context, HttpStatusCode.Conflict, null, "REQUEST_ID_REUSED", null);
                    return;
                }
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string respBody;
            string errorCode;
            try
            {
                respBody = RunAction(action, logAction, _jsonInput, out errorCode);
            }
            catch (Exception ex)
            {
                // A dispatch timeout is not a clean failure: MainThreadDispatch
                // leaves the enqueued world-touching work queued, so it still
                // runs later and the action may yet take effect after this 500
                // ("lost response after the server acted"). Keep that key's
                // ledger claim InProgress so a same-key retry is rejected with
                // 409 instead of executing twice; the entry ages out via
                // Retention. Any other exception means nothing ran or the work
                // threw before producing a response: release the claim so a
                // retry can resubmit.
                if (keyed && !(ex is TimeoutException)) IdempotencyLedger.Fail(requestId);
                ModApi.Error("web api action=" + logAction + " req=" + logTag + " caller=" + caller + " failed 500 after " + sw.ElapsedMilliseconds + "ms: " + ex);
                // Full exception detail goes to the server log above only; the
                // webserver envelope would otherwise embed the exception type,
                // message and stack trace in the response body.
                SendEmptyResponse(context, HttpStatusCode.InternalServerError, null, "ERROR", null);
                return;
            }
            if (errorCode != null)
            {
                if (keyed) IdempotencyLedger.Fail(requestId); // client error: retry may resubmit
                ModApi.Log("web api action=" + logAction + " req=" + logTag + " caller=" + caller + " rejected " + errorCode);
                SendEmptyResponse(context, HttpStatusCode.BadRequest, null, errorCode, null);
                return;
            }
            if (keyed) IdempotencyLedger.Complete(requestId, respBody);
            // The body echoes request-supplied text (spawnNear's player name /
            // ident). Json.NET escapes C0 controls but passes DEL/C1, bidi
            // controls and zero-width characters through verbatim, so the same
            // LogSanitizer contract that guards action/requestId guards it too.
            // Redaction runs first: the client's copy of the body keeps the
            // name, the log copy does not (the audit value is the outcome
            // fields, and entity id already names the player in every other
            // log line).
            ModApi.Log("web api action=" + logAction + " req=" + logTag + " caller=" + caller + " ok in " + sw.ElapsedMilliseconds + "ms " + LogSanitizer.Clean(LogRedactor.Redact(respBody)));
            writer.WriteRaw(Encoding.UTF8.GetBytes(respBody));
            SendEnvelopedResult(context, ref writer, HttpStatusCode.OK, null, null, null);
        }

        /// <summary>Execute one admin action and return its success body, or
        /// set <paramref name="errorCode"/> to the INVALID_* code the caller
        /// answers with a 400. <paramref name="logAction"/> is the sanitized
        /// action name, also used as the dispatch's operation tag.</summary>
        static string RunAction(string action, string logAction, IDictionary<string, object> body, out string errorCode)
        {
            string respBody = null;
            string failure = null;
            // Every action body runs on the game's main thread, not just
            // the world-touching ones: config mutations (skill/vs/team/
            // teamCount also call Normalize(), which rewrites ~30 fields
            // non-atomically) previously executed directly on this web
            // thread pool thread, racing each other and the console's
            // `bot reload` instance swap (a handler could mutate the
            // config object ReloadConfig had just replaced and report
            // success while the live config never changed). Dispatching
            // serializes all mutations with console commands and ticks by
            // construction; nested RunOnMain calls below short-circuit
            // via ThreadManager.IsMainThread().
            RunOnMain<object>(() =>
            {
                switch (action)
                {
                    case "enable":
                        ModApi.Config.Enabled = true;
                        ModApi.PersistConfigField("Enabled", true);
                        respBody = RespondJson("enabled", true);
                        break;
                    case "disable":
                        ModApi.Config.Enabled = false;
                        ModApi.PersistConfigField("Enabled", false);
                        respBody = RespondJson("enabled", false);
                        break;
                    case "spawn":
                        {
                            if (!OptCount(body, out int count)) { failure = "INVALID_COUNT"; break; }
                            // Bot spawning touches Unity/world state and must run
                            // on the main thread (a direct call from the web
                            // thread pool segfaulted the server).
                            int spawned = RunOnMain(() =>
                            {
                                int n = 0;
                                for (int i = 0; i < count; i++)
                                    if (BotManager.Instance.TrySpawnOne()) n++;
                                return n;
                            }, "spawn");
                            respBody = RespondJson("spawned", spawned);
                        }
                        break;
                    case "spawnnear":
                        {
                            // {"action":"spawnNear","player":"<name|id>","count":N,"weapon":"<gunId|mixed>"}
                            // Same path as `bot player <name>`: bots spawn near the
                            // target player, out-of-sight preferred (11-42m via DM
                            // spawnpoints with a ~22m sweet spot, else a 14-30m ring).
                            string ident = RequestFields.OptString(body, "player");
                            // Absent player is a client bug, not "player not found":
                            // both used to answer 200 {"found":false}, which made a
                            // malformed body indistinguishable from a left player.
                            if (string.IsNullOrEmpty(ident)) { failure = "INVALID_PLAYER"; break; }
                            if (!OptCount(body, out int count)) { failure = "INVALID_COUNT"; break; }
                            string weapon = null;
                            {
                                string wv = RequestFields.OptString(body, "weapon");
                                if (!string.IsNullOrEmpty(wv))
                                {
                                    // Same grammar as `bot player <name> [count]
                                    // [weapon]` (BotArgParser.LooksLikeWeapon): an
                                    // off-grammar id used to be dropped silently and
                                    // the bots spawned with random loadouts instead
                                    // of the requested one.
                                    if (!BotMod.Commands.BotArgParser.LooksLikeWeapon(wv)) { failure = "INVALID_WEAPON"; break; }
                                    weapon = wv;
                                }
                            }
                            var r = RunOnMain(() =>
                            {
                                var world = GameManager.Instance?.World;
                                if (world == null || string.IsNullOrEmpty(ident)) return new { spawned = 0, found = false, name = ident };
                                EntityPlayer target = BotManager.FindPlayerByNameOrId(world, ident);
                                if (target == null) return new { spawned = 0, found = false, name = ident };
                                int spawned = BotManager.Instance.SpawnNearPlayer(target, count, weapon);
                                return new { spawned, found = true, name = target.EntityName ?? target.PlayerDisplayName ?? ident };
                            }, "spawnNear");
                            respBody = RespondJson("spawned", r.spawned, "found", r.found, "player", r.name);
                        }
                        break;
                    case "remove":
                    case "clear":
                        {
                            int removed = RunOnMain(() => BotManager.Instance.RemoveAllBots("web"), "removeAll");
                            respBody = RespondJson("removed", removed);
                        }
                        break;
                    case "neural":
                        {
                            // The toggles require an explicit on flag: absence or
                            // garbage used to read as false and silently flip the
                            // live setting (e.g. squad mode off) with a 200.
                            if (RequestFields.RequireBool(body, "on", out bool on) != FieldRead.Ok) { failure = "INVALID_ON"; break; }
                            ModApi.Config.UseNeuralBrain = on;
                            ModApi.PersistConfigField("UseNeuralBrain", on);
                            string why = "";
                            if (on)
                            {
                                bool ok = RunOnMain(
                                    () => BotMod.AI.BotNeuralBrain.TryLoad(ModApi.Config.BotNeuralWeightPath, out why),
                                    "neuralLoad");
                                // Load failure stays visible in the response body
                                // ("loaded":false,"reason":"...") and in this line.
                                if (!ok) ModApi.Warn("web api neural on: weights load failed: " + why);
                            }
                            respBody = RespondJson("neural", on, "loaded", on ? BotMod.AI.BotNeuralBrain.Loaded : false, "reason", why);
                        }
                        break;
                    case "removeone":
                        {
                            // {"action":"removeOne","entityId":N} - remove a single bot.
                            // Absence ran a lookup for id 0 and answered 200
                            // {"removed":false}; a malformed body is a 400 instead.
                            if (RequestFields.OptInt(body, "entityId", out int entityId) != FieldRead.Ok) { failure = "INVALID_ENTITY_ID"; break; }
                            bool removed = RunOnMain(() => BotManager.Instance.RemoveBot(entityId, "web"), "removeOne");
                            respBody = RespondJson("removed", removed, "entityId", entityId);
                        }
                        break;
                    case "skill":
                        {
                            // {"action":"skill","level":0-4} - same as `bot skill`.
                            // Clamp/Normalize live in BotConfig.SetDifficulty (shared
                            // with the console command); the persisted value is the
                            // post-clamp property. Absent level re-applies the
                            // current difficulty (a no-op refresh); garbage rejects.
                            int level = ModApi.Config.Difficulty;
                            FieldRead read = RequestFields.OptInt(body, "level", out int parsed);
                            if (read == FieldRead.Invalid) { failure = "INVALID_LEVEL"; break; }
                            if (read == FieldRead.Ok) level = parsed;
                            string field = ModApi.Config.SetDifficulty(level);
                            ModApi.PersistConfigField(field, ModApi.Config.Difficulty);
                            respBody = RespondJson("difficulty", ModApi.Config.Difficulty);
                        }
                        break;
                    case "team":
                        {
                            // {"action":"team","on":bool} - squad mode: all bots are
                            // one team (never target/damage each other). Persisted.
                            if (RequestFields.RequireBool(body, "on", out bool on) != FieldRead.Ok) { failure = "INVALID_ON"; break; }
                            ModApi.Config.BotTeam = on;
                            ModApi.PersistConfigField("BotTeam", on);
                            respBody = RespondJson("team", on);
                        }
                        break;
                    case "vs":
                        {
                            // {"action":"vs","target":"bot|zombie|player","on":bool} -
                            // bots shoot that target class (same as `bot vs`). Persisted.
                            string target = RequestFields.OptString(body, "target")?.ToLowerInvariant() ?? "";
                            if (RequestFields.RequireBool(body, "on", out bool on) != FieldRead.Ok) { failure = "INVALID_ON"; break; }
                            if (ModApi.Config.SetVsTarget(target, on, out string field))
                            {
                                ModApi.PersistConfigField(field, on);
                                respBody = RespondJson("vs", target, "on", on);
                            }
                            else failure = "INVALID_TARGET";
                        }
                        break;
                    case "setteam":
                        {
                            // {"action":"setTeam","name":"<botName>","team":N} - assign
                            // a bot to a team (0 = free-for-all). Keyed by base name,
                            // persists to config, applies to live bots immediately.
                            string name = RequestFields.OptString(body, "name") ?? "";
                            int team = 0;
                            FieldRead teamRead = RequestFields.OptInt(body, "team", out int teamParsed);
                            if (teamRead == FieldRead.Invalid) { failure = "INVALID_TEAM"; break; }
                            if (teamRead == FieldRead.Ok) team = teamParsed;
                            if (string.IsNullOrEmpty(name)) { failure = "INVALID_NAME"; break; }
                            string baseName = BotText.BaseName(name);
                            var cfg = ModApi.Config;
                            team = Math.Max(0, Math.Min(cfg.BotTeamCount, team));
                            // Locked helper + snapshot: TeamAssignments is also
                            // read per damage event; the lock keeps lookups and
                            // this write from ever touching the dictionary
                            // concurrently (this body now runs on the main
                            // thread, but console/web surfaces share it).
                            // The helper refuses an over-long name or a map past
                            // its cap, so the answer has to name that: a 200
                            // here would report a team that was never stored.
                            var assigned = cfg.SetTeamAssignment(baseName, team);
                            if (assigned == BotConfig.TeamAssignResult.AtCapacity)
                            { failure = "TEAM_ASSIGNMENT_LIMIT"; break; }
                            if (assigned != BotConfig.TeamAssignResult.Ok)
                            { failure = "INVALID_NAME"; break; }
                            ModApi.PersistConfigField("TeamAssignments", cfg.SnapshotTeamAssignments());
                            respBody = RespondJson("name", baseName, "team", team);
                        }
                        break;
                    case "teamcount":
                        {
                            // {"action":"teamCount","count":N} - number of team
                            // buckets (0 = free-for-all only). Persisted. Clamp +
                            // assignment pruning live in BotConfig.SetTeamCount
                            // (shared with the console command). Absent count
                            // re-applies the current value; garbage rejects.
                            int count = ModApi.Config.BotTeamCount;
                            FieldRead countRead = RequestFields.OptInt(body, "count", out int countParsed);
                            if (countRead == FieldRead.Invalid) { failure = "INVALID_COUNT"; break; }
                            if (countRead == FieldRead.Ok) count = countParsed;
                            string field = ModApi.Config.SetTeamCount(count);
                            // One write for both fields: two separate persists
                            // re-read, re-parsed, fsynced and backed up the same
                            // config file twice for one request.
                            ModApi.PersistConfigFields(new[]
                            {
                                new KeyValuePair<string, object>(field, ModApi.Config.BotTeamCount),
                                new KeyValuePair<string, object>("TeamAssignments", ModApi.Config.SnapshotTeamAssignments())
                            });
                            respBody = RespondJson("teamCount", ModApi.Config.BotTeamCount);
                        }
                        break;
                    case "clearteams":
                        {
                            ModApi.Config.ClearTeamAssignments();
                            ModApi.PersistConfigField("TeamAssignments", ModApi.Config.SnapshotTeamAssignments());
                            respBody = RespondJson("cleared", true);
                        }
                        break;
                    default:
                        failure = "INVALID_ACTION";
                        break;
                    }
                    return null;
                }, "action:" + logAction);
            errorCode = failure;
            return respBody;
        }

        public override int[] DefaultMethodPermissionLevels() => new[] { 0, 0, 0, 0, 0 };

        static int _requestSeq;

        /// <summary>Correlation tag for a request that carried no client
        /// idempotency key. Monotonic per process, incremented off the web
        /// thread pool: its only job is to make a keyless request's audit
        /// lines (ok, rejected, 500) findable as one group, and the same value
        /// is echoed in the X-BotMod-Request-Id response header.</summary>
        static string NextRequestTag()
        {
            return "auto-" + System.Threading.Interlocked.Increment(ref _requestSeq)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Engine floats (health, score, distance) reach the scoreboard
        /// as JSON numbers, so they cannot keep a fraction. Round rather than
        /// truncate (a 99.9 hp bot read as 99), and clamp: a cast of a value
        /// outside int range, NaN or Infinity is undefined in C#.</summary>
        static int ToScoreInt(float v)
        {
            if (float.IsNaN(v)) return 0;
            if (v >= int.MaxValue) return int.MaxValue;
            if (v <= int.MinValue) return int.MinValue;
            return Mathf.RoundToInt(v);
        }

        /// <summary>Header name/value pairs every /api/bot answer carries,
        /// applied by MarkNoStore. A table rather than inline assignments so
        /// the contract is assertable: WebApiAuthzTests reads it without a
        /// live RequestContext, which only exists inside a running server.
        /// Cache-Control: no-store because nothing this API returns is a
        /// stable representation of anything (GET is live world state: alive
        /// bots, health, nearest player; every POST mutates config or the
        /// world), so a shared cache, the dashboard's HTTP cache, or a replayed
        /// GET after a bfcache restore would hand an admin a roster and config
        /// that no longer exist. X-Content-Type-Options: nosniff because the
        /// stock webserver sets no security header on any response (no CSP,
        /// HSTS or nosniff anywhere in its send path), so a browser pointed at
        /// /api/bot could otherwise sniff a body as HTML and run an admin's
        /// bot roster as script in the dashboard's origin.</summary>
        internal static readonly string[][] ResponseHeaders =
        {
            new[] { "Cache-Control", "no-store" },
            new[] { "X-Content-Type-Options", "nosniff" },
        };

        /// <summary>Apply <see cref="ResponseHeaders"/>. Called at the top of
        /// both handlers so every send path (200, 400, 409, 500) carries them;
        /// the listener writes headers when the response is sent, not before,
        /// so setting them here is still in time.</summary>
        static void MarkNoStore(RequestContext context)
        {
            for (int i = 0; i < ResponseHeaders.Length; i++)
                context.Response.Headers[ResponseHeaders[i][0]] = ResponseHeaders[i][1];
        }

        /// <summary>The principal behind a request, for the audit line.
        /// Every /api/bot call is already gated to permission level 0 by the
        /// stock webserver (see DefaultMethodPermissionLevels), but "an admin
        /// did this" is not an audit answer: the audit trail has to name who.
        /// Two caller shapes reach this handler, because the stock
        /// DoAuthentication resolves a level from either one:
        ///
        /// - a browser session: context.Connection is set, and its Username and
        ///   remote Endpoint are the identity. Reported as "name@ip", so a
        ///   shared account still leaves the source address behind.
        /// - an API token (X-SDTD-API-TOKENNAME / X-SDTD-API-SECRET, no sid
        ///   cookie): no connection exists at all, so the token name from the
        ///   request header is the only identifier. It is request-supplied and
        ///   sanitized like every other untrusted field, and it is labeled
        ///   "api-token" so the line never reads as a verified identity: the
        ///   stock authenticator checks the secret, not the name, and a
        ///   mismatched pair still authorizes. What the line proves is which
        ///   token name was presented, which is what makes a stolen token's
        ///   use attributable to the token row an operator can revoke.
        ///
        /// A null connection and no token header means a level came from
        /// somewhere the mod cannot see; that reads "api-token (unnamed)"
        /// rather than a blank, so the gap is visible instead of looking like
        /// an unattributed local call. Every access is guarded: an audit
        /// lookup that throws must not fail the request it describes.</summary>
        static string CallerTag(RequestContext context)
        {
            try
            {
                var con = context.Connection;
                if (con != null)
                {
                    string who = LogSanitizer.Clean(con.Username);
                    if (string.IsNullOrEmpty(who)) who = "(unnamed)";
                    string host = con.Endpoint != null ? con.Endpoint.ToString() : "?";
                    return who + "@" + host;
                }
                string token = null;
                var headers = context.Request != null ? context.Request.Headers : null;
                if (headers != null) token = headers["X-SDTD-API-TOKENNAME"];
                token = LogSanitizer.Clean(token);
                return "api-token " + (string.IsNullOrEmpty(token) ? "(unnamed)" : token);
            }
            catch (Exception) { return "unknown"; }
        }

        /// <summary>Well-known scheme default ports, named because
        /// <c>Uri.DefaultPort</c> is not public on net48 (the target framework
        /// the mod compiles against).</summary>
        internal const int HttpDefaultPort = 80;
        internal const int HttpsDefaultPort = 443;

        /// <summary>Request header lookup that never throws: a header store
        /// that is null (a token-authenticated request) or a name the store
        /// rejects reads as absent, and the caller applies its own
        /// absent-means-allowed default.</summary>
        static string RequestHeader(RequestContext context, string name)
        {
            try
            {
                var headers = context.Request != null ? context.Request.Headers : null;
                return headers == null ? null : headers[name];
            }
            catch (Exception) { return null; }
        }

        /// <summary>Whether a request may drive a mutation. True when the
        /// browser did not label the call cross-origin (no Origin header: the
        /// API-token and scripted shapes, which have no ambient authority to
        /// abuse), or when the Origin's scheme+host+port names the host the
        /// request itself arrived on. Ports are compared, not dropped: an
        /// attacker page served from the same host on a different port is a
        /// different origin and does get to mutate an admin console if the
        /// check ignores it. Public so the CSRF contract is assertable without
        /// a live RequestContext, which only exists inside a running server.</summary>
        public static bool OriginAllowed(string origin, string host)
        {
            if (string.IsNullOrEmpty(origin)) return true;
            if (string.IsNullOrEmpty(host)) return false;
            string o = origin.Trim();
            // Parse rather than compare strings: "https://good.example" and
            // "https://good.example.evil.test" share a prefix, and so does
            // "https://admin.example" against a host header of
            // "admin.example.attacker.test". A prefix test on either side turns
            // the check into a bypass, so the URL has to be decomposed and only
            // its scheme, host and port compared.
            if (!Uri.TryCreate(o, UriKind.Absolute, out Uri parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
            if (parsed.UserInfo.Length > 0) return false;
            string h = host.Trim();
            // Strip the port the Host header carries; it is compared separately.
            string hostName = h;
            string hostPort = null;
            int colon = h.LastIndexOf(':');
            int bracket = h.LastIndexOf(']');
            if (colon > bracket)
            {
                hostName = h.Substring(0, colon);
                hostPort = h.Substring(colon + 1);
            }
            if (!string.Equals(parsed.Host, hostName, StringComparison.OrdinalIgnoreCase)) return false;
            // Uri.DefaultPort is not public on net48, so the scheme's default
            // port is named here. The two schemes allowed above are the only
            // ones that reach this line.
            int defaultPort = parsed.Scheme == Uri.UriSchemeHttps ? HttpsDefaultPort : HttpDefaultPort;
            if (parsed.Port == defaultPort) return hostPort == null || hostPort.Length == 0 || hostPort == parsed.Port.ToString(CultureInfo.InvariantCulture);
            return string.Equals(hostPort, parsed.Port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        /// <summary>Host side of <see cref="OriginAllowed(string, string)"/>,
        /// taken from the request itself. The Host header is the only host the
        /// stock webserver surfaces on RequestContext, and it is what the
        /// browser used to reach the server, which is the comparison the Origin
        /// check is defined against.</summary>
        static bool OriginAllowed(RequestContext context)
        {
            string origin = null, host = null;
            try
            {
                origin = RequestHeader(context, "Origin");
                host = RequestHeader(context, "Host");
                // A reverse proxy in front of the server rewrites Host to the
                // backend address and forwards the client's in
                // X-Forwarded-Host. The browser's Origin matches the forwarded
                // one, so check that too rather than refusing every proxied
                // admin; the header is request-supplied, which costs nothing
                // here because a match still has to name the same origin the
                // browser claimed.
                if (!OriginAllowed(origin, host))
                {
                    string fwd = RequestHeader(context, "X-Forwarded-Host");
                    if (!string.IsNullOrEmpty(fwd))
                    {
                        // Comma-separated proxy chain; the first entry is the
                        // original client-facing host.
                        int comma = fwd.IndexOf(',');
                        string first = (comma < 0 ? fwd : fwd.Substring(0, comma)).Trim();
                        if (OriginAllowed(origin, first)) return true;
                    }
                    return false;
                }
                return true;
            }
            // A store that throws must not silently admit a cross-origin write.
            // Refusing a same-origin admin is a visible, recoverable failure;
            // admitting the attack is not.
            catch (Exception ex)
            {
                ModApi.Warn("origin check failed, refusing the mutation: " + ex.Message);
                return false;
            }
        }

        /// <summary>Run a world-touching action on the game's main thread and
        /// wait for it (the web server handler runs on a thread pool thread;
        /// Unity/world state must not be touched from there). <paramref name="op"/>
        /// names the operation so a dispatch timeout is attributable in the log.
        /// The wait-handle lifecycle lives in MainThreadDispatch (unit-tested).</summary>
        static T RunOnMain<T>(Func<T> fn, string op)
        {
            if (ThreadManager.IsMainThread()) return fn();
            return MainThreadDispatch.Execute(fn,
                task => ThreadManager.AddSingleTaskMainThread("bot-web-api", task),
                TimeSpan.FromSeconds(15), op);
        }

        /// <summary>Spawn-count field: absent means 1, present-but-malformed is
        /// false so the caller rejects with INVALID_COUNT. Range clamps
        /// through the console parser's ClampCount, so `bot spawn 99` and
        /// `{"action":"spawn","count":99}` cap at the same number.</summary>
        static bool OptCount(IDictionary<string, object> body, out int count)
        {
            FieldRead read = RequestFields.OptInt(body, "count", out count);
            if (read == FieldRead.Absent) { count = 1; return true; }
            if (read != FieldRead.Ok) { count = 0; return false; }
            count = BotMod.Commands.BotArgParser.ClampCount(count);
            return true;
        }

        /// <summary>Serialize the success payload. The caller sends it and, for
        /// keyed requests, records it in the idempotency ledger for replays.</summary>
        static string RespondJson(string key, object value, string key2 = null, object value2 = null, string key3 = null, object value3 = null)
        {
            var payload = new Dictionary<string, object> { [key] = value };
            if (key2 != null) payload[key2] = value2;
            if (key3 != null) payload[key3] = value3;
            return Newtonsoft.Json.JsonConvert.SerializeObject(payload);
        }

        /// <summary>A status build above this many milliseconds is worth a
        /// line. The dashboard polls this every few seconds per admin session,
        /// so the cost is proportional to how long the world makes the scan
        /// (bots x players), not to the poll rate: a healthy build stays
        /// silent however often it is read.</summary>
        const long SlowStatusBuildMs = 2000;

        /// <summary>Build the status body, warning when the build is slow.
        /// The timer sits inside the dispatch, so the warning is raised on the
        /// main thread and can go through the shared flood gate (which is
        /// main-thread only). Without it a degraded read is invisible until it
        /// trips the 15 s dispatch timeout and surfaces as a 500, so the window
        /// where the dashboard is merely slow leaves nothing in the log. The
        /// warning carries <paramref name="reqTag"/>, the same id the caller
        /// got back in the response header, so a slow line names the poll it
        /// came from. Read-only: the body is the same BuildStatus output
        /// either way.</summary>
        static string TimedStatus(string reqTag)
        {
            var build = System.Diagnostics.Stopwatch.StartNew();
            string body = BuildStatus();
            if (build.ElapsedMilliseconds > SlowStatusBuildMs)
            {
                int alive = BotManager.Instance.BotCount;
                ModApi.WarnRateLimited(() => "web api status req=" + reqTag + " build took " + build.ElapsedMilliseconds
                    + "ms with " + alive + " bots alive (dashboard polls this every few seconds)");
            }
            return body;
        }

        static string BuildStatus()
        {
            var cfg = ModApi.Config;
            var mgr = BotManager.Instance;
            var world = GameManager.Instance != null ? GameManager.Instance.World : null;
            var bots = new List<object>();
            var players = new List<object>();
            if (world != null)
            {
                // Real players only: world.Players holds connected EntityPlayer
                // clients, and bots are NPC bodies (zombieSoldier by default),
                // so bots never appear here. A spawned test client counts as a
                // player.
                // Filtered once, not per bot: the nearest-player scan below
                // re-reads this list for every bot, and re-running the
                // IsDead/IsSpawned test on every connected player inside that
                // loop made the status build bots x players instead of
                // bots + players.
                // BotManager.IsSelectablePlayer is the same predicate the
                // spawnNear/console player lookup applies, so a name or id this
                // roster omits is not one those surfaces will resolve either.
                var live = new List<EntityPlayer>();
                var plist = world.Players != null ? world.Players.list : null;
                if (plist != null)
                {
                    foreach (var p in plist)
                    {
                        if (!BotManager.IsSelectablePlayer(p)) continue;
                        live.Add(p);
                        players.Add(new
                        {
                            name = p.EntityName ?? p.PlayerDisplayName ?? ("#" + p.entityId),
                            entityId = p.entityId
                        });
                    }
                }
                foreach (var b in mgr.Bots)
                {
                    // Through the bot's own entity cache: the poll runs on the
                    // main thread right after the tick that just primed every
                    // bot's cache, so this resolves from that cache instead of
                    // repeating a world-dictionary lookup per bot per poll.
                    var ent = b.ResolveEntity(world);
                    // Nearest live player + distance in metres (blocks).
                    string nearName = null;
                    float nearDist = -1f;
                    if (ent != null && live.Count > 0)
                    {
                        float best = float.MaxValue;
                        foreach (var p in live)
                        {
                            float d = Vector3.Distance(ent.position, p.position);
                            if (d < best) { best = d; nearName = p.EntityName ?? p.PlayerDisplayName ?? ("#" + p.entityId); }
                        }
                        if (best < float.MaxValue) nearDist = best;
                    }
                    bots.Add(new
                    {
                        name = b.Name,
                        entityId = b.EntityId,
                        team = BotManager.Instance.GetTeamId(b.EntityId),
                        weapon = b.Weapon.GunId ?? "?",
                        status = b.Status(ent),
                        health = ent != null ? ToScoreInt(ent.Health) : 0,
                        deaths = ent != null ? ent.Died : 0,
                        zombies = ent != null ? ent.KilledZombies : 0,
                        players = ent != null ? ent.KilledPlayers : 0,
                        score = ent != null ? ToScoreInt(ent.Score) : 0,
                        level = ent != null && ent.Progression != null ? ent.Progression.GetLevel() : 1,
                        nearestPlayer = nearName,
                        nearestPlayerDist = ToScoreInt(nearDist)
                    });
                }
            }
            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                // Only what the dashboard panel reads: this is polled every
                // 5 s per admin session, so unused config echo is dead bytes.
                enabled = cfg.Enabled,
                targetBotCount = cfg.TargetBotCount,
                maxBots = cfg.MaxBots,
                alive = mgr.BotCount,
                difficulty = cfg.Difficulty,
                neural = cfg.UseNeuralBrain,
                neuralLoaded = BotMod.AI.BotNeuralBrain.Loaded,
                neuralPath = BotMod.AI.BotNeuralBrain.LoadedPath,
                visionRange = cfg.VisionRange,
                attackRange = cfg.AttackRange,
                spawnRadius = cfg.SpawnRadius,
                strafeChance = cfg.StrafeChance,
                dodgeOnHitChance = cfg.DodgeOnHitChance,
                botVsBot = cfg.BotVsBot,
                botVsZombie = cfg.BotVsZombie,
                botVsPlayer = cfg.BotVsPlayer,
                botTeam = cfg.BotTeam,
                teamCount = cfg.BotTeamCount,
                botHealth = cfg.BotHealth,
                players = players,
                bots = bots
            });
        }
    }
}
