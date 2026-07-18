# Design: cs-tema ↔ EventManager — streamer event requests (DRAFT for review)

Status: DESIGN ONLY (2026-07-18). Nothing built. Decision points at the bottom.

## User story

A streamer (not a server admin) asks: *"I want to run <event> on <workshop map>, around <time>"*.
Today that's a manual chain through an operator. Wanted:

1. Streamer submits the request on cs-tema.lt (event picker + workshop URL/id + note + optional time).
2. Admin approves it on the site.
3. The event server **background-downloads** the workshop map (no interruption).
4. The `!events` menu shows it under a **Queue** page: *"Prop Hunt @ de_somemap — ready — Apply"*.
5. Operator clicks Apply → map change → server comes up in the **warmup lobby with the event
   already ARMED** → streamer does the intro → ▶ Start.
6. The site shows live status (downloading → ready → live → done) the whole way.

## Architecture — the house DB-sync pattern (no inbound HTTP, no DLL API needed)

Same shape as HexTags rules / VIP / maplist sync: the website owns a table, the server polls it
and writes status back. Nothing new to invent:

```
cs-tema (Next/Express + drizzle)          shared MySQL              event server (EventManager)
  streamer form  ──────────────▶  event_requests  ◀──────────────  RequestsModule (poll ~30s)
  admin approve/reject           (site: CRUD, server: status)       ├─ ISteamApi.DownloadItem (bg)
  status timeline UI                                                ├─ !events Queue page → Apply
                                                                    └─ auto-arm after map load
```

**Table `event_requests`** (site DB): `id, event_id, workshop_id, map_name, requested_by_steamid,
requested_by_name, note, scheduled_at NULL, status, server_note, created_at, updated_at`.
`status`: `pending → approved → downloading → ready → applied → live → done` (+ `rejected`,
`failed`). Site writes through `approved`; server owns everything after.

**Server side — one new module in EventManager.Core (`RequestsModule`):**
- Inert unless `configs/eventmanager.database.jsonc` exists → dedicated/standalone servers and
  the public repo stay zero-config.
- Polls approved rows async (re-resolve state on the game thread in the callback — the async
  preload race rule).
- Workshop staging = the WSMaps recipe, already prod-verified on TTT: `ISteamApi.DownloadItem`
  (background, no map change) + `OnItemInstalled` → status `ready`. One-off events don't touch
  the map rotation, so we call ISteamApi directly rather than appending to WSMaps' maplist.
- **Apply** (menu, admin-gated): `changelevel`/`host_workshop_map` to the staged map, remember
  the pending `event_id` in memory → on the new map's `OnServerSpawn`, arm it via the
  coordinator (Warmup mode) → the operator lands exactly in the existing armed-lobby flow.
- Status writebacks after each transition so the site timeline is honest.

**Website side:** request form (role-gated), admin approve/reject, status timeline. Existing
admin-panel infra + pool-per-external-DB pattern; ~a day of work.

## Workshop-map convar protection

Threat: workshop maps shipping cfg/vscript junk (`sv_cheats 1`, movement cvars, …).
Already covered today: CvarGuard blocks `point_servercommand` and re-execs our gamemode cfg
with a delay so our values win. The gap is *event-specific* values and a hard safety net.

**v1 (recommended): EventManager-internal, no new cross-repo dependency.**
A small `ConVarPinsModule` in Core:
- a global pin list from config (`{"sv_cheats":"0", ...}` — ships as .example),
- plus the ACTIVE event's `GameConVars` (already contract-owned since 1.1.0),
- re-asserted on `OnServerSpawn` at low listener priority (after every cfg exec — the exact
  pattern proven by Prophunt's gate listener) and once more a few seconds after map start
  (CvarGuard's own delayed-exec trick).

**v2 (only if guard lists should be site-managed): extend CvarGuard, don't import it.**
Extract `CvarGuard.Shared` with a `RegisterPins(owner, dict)` API and have EventManager feed
it. More surface, one more shared contract — worth it only when the website starts managing
guard lists centrally. Importing CvarGuard's code into EventManager is the one option we
rule out: two drifting copies of the same defense.

## Full remote control — running events from the website ALONE

The request flow above still assumes an in-game operator for Apply/Start. To control, manage,
prepare and execute everything from cs-tema with nobody in-game, split the integration into
three lanes — all still the DB-sync trust model (server ↔ shared MySQL only):

**1. Observe lane (server → DB, continuous).** The server mirrors its live state into
`event_state` / `event_catalog` rows: registered events **including their settings schema and
current values** (the same `IEventMode.GetSettings()` the in-game menu renders), active/armed
event, start mode, current map, staged-download progress, player count. Written on change +
heartbeat. The site renders its control page FROM this — the website UI and the in-game menu
become two skins over the same contract, and buttons enable/disable off real state, never
assumption.

**2. Prepare lane (site → DB, slow poll ~30s).** Requests, approvals, workshop staging,
schedules — as designed above. Maps are downloaded in the background at approve time, so by
event time everything is ready.

**3. Execute lane (site → DB command queue, fast poll 1–2s).** A tiny `event_commands` table:
`id, command, args, issued_by, issued_at, status, result`. The site inserts rows
(`apply <request>`, `arm <id>`, `start`, `off`, `set <id> <key> <value>`, `intro on`,
`respawnall`, `countdown 5`, `startmode …`); the server polls every 1–2 s (one indexed SELECT —
negligible; respect the shared-pool cap), executes on the game thread through the SAME
coordinator paths as the menu, and writes back `done/failed + result`. 1–2 s button latency is
imperceptible for event control, and the lane needs no inbound network, no RCON, no panel keys.

Why a DB queue instead of the website calling the Pterodactyl console API: one trust boundary
(the DB) instead of two, a free audit trail (`issued_by` + result per row), it keeps working
when the panel/Cloudflare is having a day, and it doesn't depend on the console command path.
The panel-API route stays available as an optional "turbo" later.

**Security/audit:** site roles per lane (streamer = request only; admin = execute), every
command row carries who-clicked, the server logs execution. **Failure honesty:** the UI shows
a command as done only after the observe lane confirms the state change; a row not picked up
within N seconds surfaces as "server not responding".

With the three lanes, a full stream night — pick map, stage, apply, arm, intro tools,
countdown, start, switch, shut down to lobby — is drivable entirely from a cs-tema page, with
the in-game menu as an equal fallback.

## Roadmap (explicitly not v1)

- Public event calendar on cs-tema + signups; Discord announce webhook when an event goes live.
- `scheduled_at` auto-apply (map change at the scheduled minute) — v1 keeps a human on Apply.
- Site-managed convar guard lists (the v2 CvarGuard API above).
- Role ownership (/streamer etc.) — already on the main SPEC roadmap.

## Decision points (prefix)

1. **Apply semantics:** operator clicks Apply in `!events` (recommended — never yank the map
   out from under a live session) vs. auto map-change at `scheduled_at`?
2. **Convar protection:** v1 internal pins (recommended to start) vs. going straight to the
   CvarGuard.Shared API?
3. **Who may submit:** site role for streamers? VIP flag? Steam-group check?
4. Anything the request form needs beyond event + map + note + time?

## Estimate

Site ~1 day · server module + Queue page + staging ~1 day · pins module ~half day.

## v2: Website control room — automations, templates, event-night workflows, UI/UX

Extends the accepted three-lane design. Trust model unchanged: **server ↔ shared MySQL only**; the single pre-existing side channel is **outbound Discord webhooks** (house standard, fired by site or server, flagged where used). Every game-facing effect below is an existing verb (stage / apply / arm / set / countdown / start / off / stream tools) reached through the execute lane or the coordinator — v2 adds *ordering, scheduling, templates, persistence, and failure policy*, zero new game capabilities.

### 0. Architecture resolutions (where the four lenses disagreed)

1. **One executor, on the server.** The automations lens proposed a server `AutomationModule`, the templates lens a site-side night-run worker, the workflows lens a server `WorkflowModule`. Resolution: **a single server-side sequencer (`WorkflowModule` in EventManager.Core)** executes everything — scheduled one-shots, recurring nights, multi-event workflows. Rationale: nights must survive a site outage (server-native end rules and transitions), the server already owns the 1–2s execute poll and game-thread context, and one engine means one FSM to debug. The **site cron is only the alarm clock and the announcer**: it materializes runs from schedules (server never parses cron/timezones), fires Discord webhooks, and runs the watchdog. The templates lens's "site worker advances the night" is dropped; its v3 "server-native night execution" is promoted to the v1.5 baseline.
2. **Occurrences ≡ runs.** The automations lens's `event_occurrences` and the workflows lens's `event_workflow_runs` are the same object. Merged into one table, `event_night_runs`, one state machine. A scheduled single event is a night of one block.
3. **Nights ≡ workflows.** The templates lens's `event_nights`+`event_night_items` and the workflows lens's step-list JSON are two authoring views of the same thing. Canonical storage: a night row whose `definition_json` is the workflows lens's flat step array; the site UI renders/edits it as item blocks built from templates. No separate workflow tables.
4. **End rules are server-side from day one** (workflows/automations position wins over templates' site-worker interim) — `run_until` with rounds/minutes is a v1.5 step type; a site-fired `off` remains available as a manual button only.
5. **Auto map-change** (SPEC decision point #1): the design supports both. Default is `gate: manual` on `apply_map` (human on Apply, current SPEC stance); a per-schedule `auto` flag with the 5-min in-game warning + abort + empty-precondition safeguards unlocks unattended nights. Prefix decides the default (§ decisions).
6. **Templates: flat first, versioned second.** UI/UX's single-table template ships in v1.5; the templates lens's copy-on-write versioning + approval graduates it in v2 (`event_templates` grows `head_version_id`; existing rows become v1). Runs always freeze a **resolved snapshot**, so v1.5 history is already immutable.
7. **UI honesty is universal.** The UI/UX command ladder (§4.1) governs every button in every view, including automation-issued actions (rendered from the same rows).

### 1. Canonical data model

Existing (unchanged): `event_requests`, `event_commands`. Two column additions: `event_commands.run_id FK NULL` (a run's execute-lane rows *are* its audit log; automated actions write rows with `issued_by='auto:night:<run_id>'`) and `event_commands.expect JSON NULL` (server re-checks preconditions at execution, e.g. `{"map":"de_x","max_players":0}`; mismatch → `failed: precondition` — the server-side seatbelt behind every dangerous-action modal).

**Observe lane (server writes; pins the accepted design's columns):**

```
event_state (one row per server; heartbeat ~10s + on change)
  server_id · heartbeat_at · map · player_count · players_json
  active_event_id · armed_event_id · start_mode · intro_active · countdown_ends_at
  downloads_json            -- [{workshop_id, map_name, pct, status}]
  rounds_played_current     -- for run_until + "ends in ~N rounds"
  night_run_id NULL · night_step_index · night_step_id · night_step_status
  night_gate_pending NULL   -- question text when a manual gate waits
  updated_at

event_catalog (one row per registered event)
  server_id · event_id · display_name
  settings_json   -- verbatim GetSettings(): [{key, displayName, type, value, choices}]
  convars_json    -- GameConVars, read-only display
  schema_hash     -- hash over sorted [{key,type,choices}] only (renames/values ≠ drift)
  registered BOOL -- false when plugin unloads; row kept so template drift checks still work
  updated_at
```

**Templates (site owns):**

```
event_templates                       -- v1.5 shape; v2 adds versioning columns
  id · slug · name · event_id · owner_user_id
  visibility ENUM(private, shared)    -- streamer drafts vs admin-published
  settings_json   -- SPARSE {key:"stringvalue"}; omitted key = live server value
  map_json        -- {kind: workshop|stock, workshop_id?, map_name?} NULL = current map
  convar_pins     -- {cvar:value}, admin-only edit, server allowlist (§3.4)
  start_mode · end_rule JSON          -- {kind: manual|after_minutes|after_rounds, n?}
  announce_json   -- discord copy templates, {event}{map}{time}{streamer} vars
  schema_hash     -- catalog hash at save time (drift anchor)
  last_used_at · archived_at NULL · created_at · updated_at
  -- v2: head_version_id, approved_version_id, parent_template_id (clone lineage)
  --     + event_template_versions (append-only content rows; nights pin versions)
```

**Schedules + nights + runs (site authors; server executes):**

```
event_schedules                       -- declarative "when"; site CRUDs, server never reads cron
  id · name · night_id FK             -- what to run (a night; single event = 1-block night)
  recurrence NULL                     -- cron, site-evaluated · scheduled_at NULL (one-shot)
  timezone · enabled
  min_players INT DEFAULT 0 · arm_on_players INT NULL · active_window NULL  -- "18:00-23:59"
  announce_offsets JSON               -- e.g. [-30,-10,-1] minutes, site cron fires webhooks
  auto_apply BOOL DEFAULT false       -- decision #1: unattended map change allowed?
  created_by · timestamps

event_nights                          -- authoring object; site owns, server read-only
  id · name · definition_json · revision · created_by · timestamps · deleted_at NULL
  -- definition_json = flat ordered step array (§2), items reference template ids +
  --   sparse per-night overrides; site UI renders it as blocks, dupes blocks for multi-event

event_night_runs                      -- ONE row per concrete firing; the shared FSM (§2)
  id · schedule_id FK NULL · night_id FK · request_id FK NULL  -- ad-hoc launches too
  definition_snapshot_json            -- frozen at materialization; edits never apply mid-run
  resolved_json                       -- fully merged recipe per item (template ⊕ overrides)
  fire_at · status · status_detail · current_step_index · step_state_json
  resume_policy ENUM(gate, abort) DEFAULT gate
  started_by · started_at · ended_at
  peak_players · rounds_played · summary_json NULL
  gotv_status ENUM(NULL,recording,uploading,done,failed) · gotv_url NULL
  discord_message_id NULL             -- site edits the announce/summary embed later
  updated_at

event_night_run_log                   -- append-only, server writes, site renders timeline
  id · run_id · step_index · step_id
  event ENUM(entered, gate_open, gate_approved, condition_met, completed,
             failed, retried, skipped, timeout, note) · detail · at
```

**Column ownership (hard rule):** site owns run states `planned / announced / cancelled` + `discord_message_id`; server owns everything from pickup onward (`pending → running → … `) plus stats/gotv. The site never writes a server-owned state and never shows a transition until the observe lane confirms it. One active run per server (enforced at pickup); no run queueing.

### 2. The sequencer (server `WorkflowModule`)

**Step format** — flat ordered array, no branching/loops/expressions; one escape hatch `on_fail: "skip_to:<step_id>"` (forward-only) so "event 1 broke → jump to event 2's block". Closed verb set — deliberately **no console/rcon step** (that would be a genuine new trust boundary; if ever wanted it must be flagged and gated separately):

| type | effect (existing verb) | done-condition | tier |
|---|---|---|---|
| `stage_map` | `ISteamApi.DownloadItem` (WSMaps recipe, background) | `OnItemInstalled` / present; `alt_maps` failover v2 | v1.5 |
| `announce` | chat/center; `discord:true` → webhook | immediate | chat v1.5 · discord v2 |
| `wait` | nothing | wall-clock `until` / `for_s` | v1.5 |
| `apply_map` | changelevel/host_workshop_map | `OnServerSpawn` on expected map | v1.5 |
| `arm_event` | coordinator arm (armed warmup lobby) + `TrySetSetting` per resolved key | armed confirmed | v1.5 |
| `countdown` | existing stream tool | elapsed | v1.5 |
| `start_event` | coordinator start (+ `mp_restartgame` per contract) | active confirmed | v1.5 |
| `run_until` | event plays | `rounds N` \| `minutes N` \| `manual`; explicit safety timeout required | v1.5 |
| `end_event` | coordinator off + GameConVars restore | ground state confirmed | v1.5 |
| `results` | generic summary → chat + webhook | immediate | v2 (per-mode stats v3) |

**Run FSM:** `planned → announced (site) → pending → [pickup + preflight] → running → completed`, with `paused(reason: operator | step_failed | server_restart | divergence)`, `aborting → aborted` (human choice), `failed` (policy), `skipped`/`cancelled` (site-owned pre-pickup). **Step FSM:** `pending → [awaiting_gate → approved] → running → awaiting_condition → done`; timeout → `failed` → `on_fail ∈ halt (default) | retry:N | skip | skip_to | abort`.

**Gates:** `auto` (default) or `manual` — engine halts *before* the effect, surfaces via observe lane + in-game `!events` menu ("Workflow waiting: Apply de_x? [GO]"); approval is an `event_commands: night_gate_ok <run_id> <step_id>` row from either skin (site button or menu — equal skins). Default `manual` on `apply_map` unless the schedule's `auto_apply` is set (§0.5), in which case the 5-min in-game warning sequence runs and `!events cancel` / site Cancel aborts it.

**Preflight (at pickup, and as a standalone dry-run command in v2):** step types known, `skip_to` targets forward, events registered in catalog (hard fail), setting keys/values shape-checked against mirrored schema, maps staged-or-stageable, every `run_until` has an end rule + safety timeout, `min_players` met, no other run live (conflict → `paused(detail: conflict)` + admin webhook — hold, never stomp).

**Safeguards (universal):**
- *Never yank a live session*: auto `apply_map` requires no other live run + warning sequence; `expect` preconditions re-checked server-side at execution.
- *Pause freezes the sequencer only* — a live event inside `run_until` keeps playing; pause never touches game state (document loudly, #1 operator surprise).
- *Operator divergence*: manual `!events off` / map change mid-run → engine auto-pauses `(divergence)` and never fights or reverts a human (detection v2; in v1.5 the mismatch surfaces as a step timeout → halt gate).
- *Crash/restart*: game state never persists (SPEC ground-state rule stands); the **run row** persists. On boot: stuck run → `paused(server_restart)` + gate "resume at step X / abort" (`resume_policy: abort` for unattended nights). No auto-resume in any tier — an interrupted `run_until` can't resume faithfully. `apply_map` persists "expecting map X for run R" before changelevel, so map-change survival and crash recovery share one reconcile-on-`OnServerSpawn` path.
- *Settings rejects*: `TrySetSetting` false → per-key log note, non-fatal by default (`settings_strict` opts in); UI shows "hunters=99 → rejected by server".
- *`run_until` safety timeout* → treated as end-rule met, proceed to `end_event` — never strand a stream night.
- *Abort/fail cleanup* = coordinator off path only (deactivate + restore captured GameConVars + cancel intro). **No map rollback.**
- *Empty-server fallback* (global config): active event + 0 humans for M min (default 10) → coordinator off, optional changelevel to `lobby_map`, run `completed (detail: emptied)`. This is what makes unattended automation trustworthy.

**Watchdog (site cron, pure observe-lane read):** run in a server-owned state + heartbeat stale >60s → page banner + admin webhook; stale >10min past `fire_at` → `skipped (server unreachable)` + apologetic edit of the announce message.

### 3. Scheduling, templates, and resolution

**3.1 Division of labor.** Site cron (~1 min tick): materialize `event_night_runs` from `event_schedules` ≤24h out (exactly one unfinished run per schedule; a skipped run never blocks the next; 2 consecutive skips → "needs attention" webhook), fire announce webhooks at offsets (suppressing T-1 and posting "running late" if the run isn't armed yet — observe check before every announce), watchdog. At materialization the site also fans out **one `event_requests` row per unique workshop map** in the night → everything downloads days early; failed download → `paused(map not ready)` webhook *immediately*, not at T-10. Server: everything else, tightening its poll to the 1–2s execute cadence when a run is within T-5min or active.

**3.2 Player-count trigger** (`arm_on_players` + `active_window`, no `fire_at`): evaluated server-side per second from live count (never the mirrored value), threshold sustained 60s, max one firing per window/day; fires a run at `now` on the current map ("MiniHumans starting in 2 min — enough players online!"); count drops before start → `skipped (players left)`, trigger re-arms.

**3.3 Override precedence** (merge once, at materialization, into `resolved_json`; sparse everywhere): 1 live server value ← 2 template `settings_json` ← 3 night-item overrides ← 4 launch-time ad-hoc overrides ← 5 live `set` commands during the run (recorded via `run_id`, **never** written back to the template; explicit "Save current as new template/version" promotes live state).

**3.4 Convar pins — flagged hardening.** Template pins ride the accepted ConVarPinsModule as a third source, delivered in the arm command args. Arbitrary cvar pairs from a DB row are console-adjacent power, so the server keeps a **local allowlist config** (shipped .example; movement/gravity/roundtime families) and rejects non-listed pins (`result=rejected:not-allowlisted`); site mirrors the allowlist read-only for autocomplete; pin editing admin-only. Not a new trust boundary — a hardening of the existing one.

**3.5 Drift.** Templates store `schema_hash`; mismatch vs live catalog → v1.5: banner + strict block until re-saved; v2: per-key classification (new key = INFO pass-through · removed key = WARN, strict blocks / lenient drops+logs · type change or dead choice = BLOCK, re-enter) with a resolution UI, plus T-24h/T-1h scheduled pre-checks across every scheduled night with Discord alerts — Friday night doesn't discover Tuesday's plugin update on stream. Stale catalog (`updated_at` old) downgrades confidence (banner) but never hard-blocks. Two-stage validation stays honest: site checks are advisory shape checks rendered *from* the schema (invalid input mostly unrepresentable); `TrySetSetting` is authoritative and its rejects surface per-key.

### 4. UI/UX

**4.1 Command ladder (every button, every view; nothing is ever marked done from the click):**

```
IDLE →click→ QUEUED (row inserted) → ACKED (server executing/done) → CONFIRMED (observe lane
shows expected state) | FAILED (result text, toast, re-enable) | NO_PICKUP 8s (inline warn)
| UNCONFIRMED 15s (amber "executed but state unconfirmed")
heartbeat >25s stale → page-wide red banner, ALL execute controls disabled (absolute — no
modal overrides; you may not queue destructive commands at a server you can't observe)
```

Site backend polls `event_state`/`event_commands` every 2s, pushes over existing websocket/SSE (site-internal). Optimistic rendering only for settings edits (stripe "pending", revert on reject) and fire-and-ack for stream tools; state-changing primaries always run the full ladder. Dangerous actions tier by blast radius: Tier 1 soft confirm (`off` while live) · Tier 2 informed confirm (`apply` with players online: live facts in the modal, type-map-name / 1.5s hold on mobile, `expect` carried on the command) · Tier 3 blocked (countdown running, stale heartbeat).

**4.2 Control room** `/events/control` (flagship, v1.5): state header (freshness chip · map · players · armed/live · downloads) + **one primary-action button** that is a pure function of `event_state` (stale→disabled red / staging→progress / ready→APPLY / armed→START / live→END / idle→ARM ▾), event cards with settings drawers rendered 100% from `event_catalog.settings_json` (Bool toggle, Int/Float stepper, Text field, Choice select — the same schema the in-game menu renders: two skins, one contract), stream-tools strip, queue/timeline column with inline approve/reject + Apply on ready requests, merged audit feed (`event_commands` + request transitions + observe deltas). v2: approval diff view, audit history table + filters, multi-server tabs (schema already keyed by `server_id`).

**4.3 Streamer request wizard** `/events/request` (v1.5): 3 steps — event card grid from catalog (unregistered greyed "not deployed right now") → workshop URL/ID with Steam `GetPublishedFileDetails` lookup via site backend proxy (thumbnail/size/updated; ≥150 MB download-time hint; site→Steam only, server trust untouched) + recently-used chips → time (Europe/Vilnius default) + note + "what happens next" strip. Post-submit status timeline maps 1:1 to `event_requests.status` with download pct from `downloads_json`; withdraw allowed pre-`approved`. A streamer "launch from template" is just a pre-filled request row carrying `template_id` — the accepted approve flow doubles as the use-permission gate, zero new moving parts. Discord webhooks on approved/ready/live/rejected.

**4.4 Night runboard** `/events/nights/:run` (v1.5 with manual **[Next]** advancing the sequence; auto-advance is just the server engine): step timeline from the run log, current-step card with gate button, live server panel from observe lane, `[Pause] [Abort] [End now] [Skip →] [Extend +30m]` — all `event_commands` rows, greyed until observe confirms. Builder page: step cards from templates, add-step limited to known types, per-card gate/on_fail/timeout, [Validate] → preflight checklist; lint warnings (bare `skip` on `arm_event`, missing end rule).

**4.5 Calendar** `/events/calendar`: v1.5 agenda list grouped by day from requests+runs (status dots; viewers see event+map+time only — doubles as the future public page's data shape). v2: month grid, drag-reschedule (admin, Tier-1 confirm + requester webhook ping), ±90min conflict hint, ICS feed. v3: public calendar + signups; signup-count gates on schedules.

**4.6 Companion mode** `/events/live` (v1.5 admin, v2 streamer-scoped): one-line state, giant primary-action button (same ladder), 2×2 thumb-sized stream-tool grid, "last command + confirm state" line, hold-to-confirm instead of modals, ≥56px targets, dark only. v3: read-only OBS browser-source overlay of countdown/state.

### 5. Post-event

- **Auto-GOTV** (v1.5): run → step past `start_event` starts recording via Gotv's shared interface (optional-gate; absent → `gotv_status NULL`); `end_event` stops; uploader posts, server writes `gotv_url`. Failure → `gotv_status=failed` + webhook, **never blocks the event**.
- **Summary** (v2): manager tracks generic stats during the run (peak players, rounds, duration, map) → `summary_json`; site cron sees `completed` → Discord embed + permanent event page; when `gotv_url` lands late, **edits** the embed via `discord_message_id` (or threads "📼 GOTV: <url>"). No summary (crash) → minimal "event ended" from timestamps. v3: `IEventMode.GetSummary()` default-member contract extension for per-mode lines ("Best hider: X, 4:31 survived") — public Shared contract touch, NuGet minor bump.
- **Auto-extend** (v3): `extend_policy {if_players_gte, extend_minutes, max_extends}` at the end condition, optional player vote; the manual "Extend +30m" button ships in v2.

### 6. Deliberate exclusions

No branching/loops/expressions in nights (flat list + forward `skip_to`); no console/rcon step; no mid-run definition edits (frozen snapshots); no cross-server orchestration or run queueing; no game-state persistence across restart; no map rollback on abort; no pluggable step types; recurrence/cron never lives on the server.

---

### (a) Phased delivery plan

| Phase | Scope | Effort |
|---|---|---|
| **v1** (accepted) | `event_requests` + RequestsModule staging + `!events` Queue/Apply + ConVarPinsModule + basic site form/approve/timeline | ~2.5 days (per existing estimate) |
| **v1.5** | Observe-lane columns (§1) + `expect`/`run_id` on commands; `WorkflowModule` sequencer with the v1.5 step set, gates, on_fail, pause/resume/abort, crash-resume gate, preflight; flat `event_templates` + create-from-live + load-with-diff; `event_nights`/`runs`/`log` with manual-advance runboard; control room (ladder, primary button, schema drawers, stream strip, queue, audit feed); request wizard + timeline; agenda calendar; companion (admin); scheduled one-shots (site cron materialization, announce webhooks T-30/10/1 + in-game, staging fan-out); auto-GOTV; empty-server fallback; watchdog; hash-level drift block | ~4–6 days (server sequencer ~2, site ~2–3) |
| **v2** | Recurrence engine + occurrence timeline UI; template versioning/clone/approval + full drift engine + T-24h/T-1h pre-checks; auto-advance polish (alt_maps failover, divergence auto-pause, dry-run report page, batch `preset` command); convar-pin allowlist; arm-on-N-players; hold-for-operator gate UI + manual Extend; generic `results` + Discord summary with GOTV follow-up edit; month calendar + ICS; streamer-scoped execute (if approved); audit history + filters; multi-server tabs | ~1.5–2 weeks |
| **v3** | Auto-extend policies/votes; `GetSummary()` + `TryValidateSetting` contract extensions; public calendar + signups + signup-gated schedules; site-managed cvar-guard lists (CvarGuard.Shared); OBS overlay; template gallery/analytics; GOTV highlight clipping | later, as demand proves |

Launch demo: **weekly Prop Hunt night** — pure composition of shipped pieces (one schedule row + one 1-block night) once v1.5 lands.

### (b) 5 decisions prefix must make

1. **Auto map-change** (SPEC decision #1): keep human-on-Apply as the default and allow per-schedule `auto_apply` opt-in (recommended), or default unattended? This gates the whole "nobody at the keyboard" story.
2. **Streamer execute scope**: expand from request-only to *stream tools + start, only while their own request is applied/live* (v2, still audited `event_commands` rows) — contradicts the current "streamer = request only" line, needs sign-off.
3. **Convar-pin allowlist**: accept the allowlist hardening and seed it (proposed: gravity/movement/roundtime families). Also: pins admin-only in the editor — OK?
4. **Template governance**: do streamer templates need admin approval per *version* (pre-clearance, nights of approved versions skip per-run approval) or only at launch-request time? And is `strict` drift the default for admin launches too (recommended)?
5. **Unattended defaults**: empty-server fallback minutes (10?), hold-for-operator timeout before auto-skip (30 min?), `resume_policy` for scheduled nights (`abort` recommended), and who may submit requests (site role vs VIP flag vs Steam group — carried over from v1 decision #3).

### (c) Top-5 wow-per-effort (build first)

1. **Announce sequence** — site cron + Discord T-30/T-10/T-1 + in-game chat/center from the same run row. Near-zero risk; turns "a setting changed" into "an *event* is happening".
2. **Auto-GOTV + link** — Gotv plugin already exists with uploaders; pure wiring (optional interface + two calls + two columns). Every event auto-produces a VOD.
3. **Control-room primary-action button + schema-driven settings drawers** — the flagship page's core; renders entirely from observe-lane data that v1 already mirrors, and makes every later feature legible.
4. **Scheduled auto-apply + arm (with empty-server fallback)** — removes the human from the critical path; the load-bearing automation every night composes with, made trustworthy by the fallback.
5. **Post-event Discord summary + create-template-from-live** — closes the loop publicly after every event (advertising the next one) and makes "run it again exactly like last Friday" one click.