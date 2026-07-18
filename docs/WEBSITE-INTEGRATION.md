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
