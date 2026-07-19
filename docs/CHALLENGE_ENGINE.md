# ChallengeEngine — Full Spec (spec-driven development)

Status: **SPEC rev.3 (build-ready)** — 2026-07-19. Requirements from prefix (Discord #general).
This document is the source of truth; the build follows it phase by phase. Rev.3 completes rev.2 (the
3-agent adversarial review) into a development-ready spec.

Architecture in one line: **ChallengeEngine is a satellite `IEventMode` gamemode living inside the
EventManager solution** — its own assembly/ALC (isolation + purely additive), but a project-reference
to `EventManager.Shared` (no NuGet version-skew), reusing EventManager's DB lane + web bridge +
convar-capture. It hosts thin `IChallenge` plug-ins; the engine owns rounds, scoring, standings,
escalation, finale, crown, and persistence.

> Repo disposition (pending prefix ack): build inside `yappersHQ/EventManager`, archive the standalone
> `yappersHQ/ChallengeEngine` repo. The spec content is identical either way; only the project layout
> and the `EventManager.Shared` reference kind (project vs package) differ.

---

## 1. Goal & requirements

A reusable **session engine** that turns short competitive CS2 challenges into full stream events:
each event is a **30 min – 3 h session** of rapid rounds that accumulate points, show a **live
standings overlay** (in-game + website/OBS), **escalate** over time, end on a **grand finale**, and
crown a **champion** who feeds a **season leaderboard**. A "challenge" (King of the Hill, Bomb Tag, …)
is a thin plug-in implementing `IChallenge`; the engine drives everything around it.

Requirements (prefix, this thread):

1. **Challenges → every session crowns a winner.** Not a toggle-mode; a competition with a champion.
2. **30 min – 3 h**, operator-set → a multi-round session, not one heat.
3. **Engaging, novel, map-agnostic** — works on the stock rotation via spawned entities/transforms,
   never custom map geometry.
4. **Additive** — no forced rework of existing EventManager events (Prop Hunt, 1vsAll, …). Existing
   modes may OPTIONALLY be wrapped as challenges later via thin adapters.
5. **Live dynamics** — swap challenge, ramp escalation, inject modifiers, reassign roles, adjust
   duration, all live from the control room; applied at the next round boundary (or instantly for
   global modifiers).
6. **Resilient** — disconnects self-heal (points SteamID-keyed; role-holder DC → challenge
   reassigns/voids); a server crash reloads accrued points and returns to a clean lobby (no half-round
   replay).

---

## 2. Where it lives (project layout)

Inside the EventManager solution:

```
EventManager/
  EventManager.Shared/        (existing) + IEventMode.GetLiveStateJson()  ← one additive method
  EventManager.Core/          (existing) WebBridgeModule writes live_json into em_state
  ChallengeEngine/            (NEW satellite — own assembly → .build/modules/ChallengeEngine.dll)
    ChallengeEnginePlugin.cs      IModSharpModule + IEventMode (registers with the gate)
    ModuleDependencyInjection.cs  DI wiring (house pattern)
    InterfaceBridge.cs            cached managers (Entity/Client/ConVar/ModSharp/Localizer/Menu + the gate)
    Session/
      SessionEngine.cs            the FSM (Lobby→Round→Intermission→…→Finale→Crowned)
      RoundContext.cs             engine-side IRoundContext
      Ledger.cs                   SteamID→totals (lifted from Prophunt.Ranks)
      LiveState.cs                builds the GetLiveStateJson() snapshot
    Challenges/
      NullChallenge.cs            smoke-test challenge (Phase 1)
      KingOfTheHill.cs            first real challenge (Phase 2)
    Persistence/
      ChallengeStore.cs           ce_* via EM's shared PlayerAnalytics provider (SqlSugar-free surface)
    .assets/locales/challengeengine.json
  ChallengeEngine.Shared/     (NEW — own assembly → .build/shared/, NOT NuGet yet)
    IChallenge.cs, IRoundContext.cs, ChallengeTypes.cs   (ORM/3rd-party-free)
```

`ChallengeEngine.csproj` references `EventManager.Shared` **Private="false"** (EventManager owns and
ships it to the server's `shared/`; ChallengeEngine must not ship a duplicate copy → single ALC,
no `TypeLoad`). `ChallengeEngine.Shared` builds to `.build/shared/`.

---

## 3. Integration with EventManager (the `IEventMode` surface)

Registered in `OnAllModulesLoaded`:

```csharp
sharedSystem.GetSharpModuleManager()
    .GetOptionalSharpModuleInterface<IEventManagerShared>(IEventManagerShared.Identity)
    ?.Instance.RegisterEvent(this);   // this = ChallengeEnginePlugin : IEventMode
```

(The rev.1 skeleton wrongly grabbed `Sharp.Shared.IEventManager` — the CS2 game-event bus. That is
**not** the gate. Use `EventManager.Shared.IEventManagerShared`.)

| `IEventMode` member | ChallengeEngine value |
|---|---|
| `Id` | `"challengenight"` |
| `DisplayName` | `"Challenge Night"` |
| `RequiresRoundRestart` | `true` (needs a clean `mp_restartgame` to apply the neutralizing convars) |
| `Activate()` | start the session → **Lobby** (no args) |
| `Deactivate()` | stop the session + full teardown (remove zones, unfreeze, restore any changed live-player state) |
| `GetSettings()` | the pickers (below) |
| `TrySetSetting(key, value)` | validate + store (strings); applied at next session start, or live where safe |
| `GetActions()` | the live operator verbs (below) |
| `TryInvokeAction(id, arg)` | marshalled to the game thread → `SessionEngine` |
| `GetActivePlayerRoles()` | per-heat status for the control room (leader / in-play / eliminated) |
| `GameConVars` | the win-condition neutralizer set (below) — captured/restored by the coordinator |
| `SupportedMaps` | `null` (map-agnostic — runs anywhere) |
| `GetLiveStateJson()` | **new optional method** — standings snapshot for website/OBS (§7) |

### 3.1 Settings (`GetSettings` / `TrySetSetting`)

| key | type / range | default | meaning |
|---|---|---|---|
| `challenge` | enum: `koth` \| `playlist` | `koth` | which challenge, or rotate a playlist |
| `duration_min` | int 30–180 | `60` | session length |
| `escalation_min` | int 5–60 | `15` | phase cadence |
| `finale_size` | int 2–10 | `6` | top-N seeded into the finale |
| `autostart` | bool | `true` | auto-begin heats vs operator `start_round` |

### 3.2 Actions (`GetActions` / `TryInvokeAction`) — live operator verbs

`start_round`, `skip_round`, `force_finale`, `extend` (+arg minutes), `swap_challenge` (arg id),
`inject_modifier` (arg id — instant for globals, else next boundary), `set_multiplier` (arg),
`pause`, `resume`, `end_session`. Role (re)assignment reuses the control room's existing player-picker
via `GetActivePlayerRoles`.

### 3.3 `GameConVars` — neutralize CS2's native round so the engine's heats aren't cut short

The engine drives its own heats with `PushTimer`; CS2's native round/win system runs underneath and
would end heats early / reshuffle teams / end the match. Declared here so the coordinator captures the
originals on start and restores them on deactivate:

```
mp_ignore_round_win_conditions 1     // heats end on the engine's clock, not a team wipe
mp_roundtime  60 ; mp_roundtime_defuse 60 ; mp_roundtime_hostage 60   // (max) — engine owns timing
mp_freezetime 0
mp_maxrounds 0 ; mp_timelimit 0      // never native match-end mid-session
mp_team_intro_time 0
mp_respawn_on_death_ct 0 ; mp_respawn_on_death_t 0  // engine controls elimination/respawn
mp_warmup_pausetimer 1               // hold warmup; engine goes live deliberately
```

Never `ConVarManager.SetString` a pinned convar imperatively, and never a convar change-hook
(the non-re-entrant Source dispatch crash). Everything convar goes through `GameConVars`.

---

## 4. The `IChallenge` contract (`ChallengeEngine.Shared`)

A challenge implements round logic only; the engine drives timing/scoring/standings.

```csharp
public interface IChallenge
{
    string Id { get; }                       // "koth"
    string DisplayName { get; }              // localized key handled by the engine
    int MinPlayers { get; }                  // engine won't start a heat below this
    int RoundSeconds { get; }                // engine's hard timeout for a heat
    ChallengeWinRule WinRule { get; }        // LastAlive | FirstToScore | MostObjective | Timed

    void StartRound(IRoundContext ctx);      // set up ONE heat: roles, hazards, modifiers
    RoundResult ForceEnd(IRoundContext ctx); // engine timer fired → collect the final result
    LeaveReaction OnPlayerLeft(IRoundContext ctx, ulong steamId);  // a player left mid-heat
    void Tick(IRoundContext ctx) { }         // optional per-tick (KotH progress, etc.)
    void Cleanup(IRoundContext ctx) { }      // remove entities/effects between heats
    void Precache(IResourcePrecacher p) { }  // NEW: precache models/particles/sounds (or engine no-op)
}

public enum ChallengeWinRule { LastAlive, FirstToScore, MostObjective, Timed }
public enum LeaveReaction    { Continue, ReassignRole, VoidRound, EndRound }

public sealed record RoundResult(IReadOnlyList<PlayerScore> Scores, ulong? RoundWinnerSteamId);
public sealed record PlayerScore(ulong SteamId, int Points, string? Note = null);
```

`IRoundContext` (engine-provided) — the ONLY surface a challenge touches, so no challenge grabs raw
engine APIs and the safety fixes live in one place:

```csharp
public interface IRoundContext
{
    int RoundNumber { get; }
    int Phase { get; }
    IReadOnlyCollection<string> Modifiers { get; }
    IReadOnlyList<IGameClient> AlivePlayers { get; }     // humans, minus eliminated
    IGameClient? GetPlayer(ulong steamId);
    IDictionary<string, object> Scratch { get; }         // per-heat state bag

    void EndRound(RoundResult result);                   // challenge signals the heat is over
    void Eliminate(ulong steamId, string? reason = null);
    void AwardPoints(ulong steamId, int points, string? note = null);  // instant, folded into result

    // Safe helpers — implemented ONCE, correctly, in RoundContext:
    void CenterAll(string localizedHtml);                // HTML center + flash-fix keep-alive
    void Center(IGameClient c, string localizedHtml);
    void PlaySoundAll(string soundEvent);                // EmitSoundClient per client (precached)
    bool TeleportSafe(IGameClient c, Vector pos, Vector? ang = null);  // EntityPlacementTest + DropToGround
    EntityToken SpawnMarker(string classname, Vector pos, IReadOnlyDictionary<string,string>? kv = null);
    void RemoveEntity(EntityToken token);                // handle-validated, not raw-index
}
```

`EntityToken` wraps a `CEntityHandle` (serial-versioned) — indices get recycled across heats/maps, so
markers are tracked by handle and validated before `Kill()`. No `IGameClient`/pawn/entity is ever
stored across a callback; everything re-resolves by SteamID.

---

## 5. Session FSM (`SessionEngine`)

States: `Idle → Lobby → Round → Intermission → (loop, escalating) → Finale → Crowned → Idle`.
All on the game thread. **Session/phase clock in game-time** (`GetGlobals().CurTime`), never
`DateTime.UtcNow` — survives hibernation and `pause`/`resume`.

1. **Lobby** — `Activate()` entered here. Warmup held. `TryBeginRound` re-arms every 5 s until
   `humans ≥ MinPlayers` (bots allowed in the smoke path). If `autostart` off, waits for `start_round`.
2. **Round** — `StartRound(ctx)`; run until the challenge calls `EndRound`, or the `RoundSeconds`
   timer fires → `ForceEnd(ctx)`. Collect `RoundResult`. `StartRound`/`ForceEnd` throwing → void the
   heat, log, continue.
3. **Scoring** — apply the phase multiplier; fold `PlayerScore`s + instant awards into the ledger
   (in-memory + a per-round DB write). Update standings.
4. **Intermission** (~12 s) — standings flash (top-8, HTML center), next-heat teaser, MVP callout.
   Loop to Round.
5. **Escalation** — at each `escalation_min` boundary: `phase++`, bump the global points multiplier
   (`×(1 + 0.5·phase)`), optionally inject a modifier, announce an act-break (center + sound).
6. **Finale gate** — once `CurTime ≥ sessionEnd` (or `force_finale`), the next heat is the finale.
7. **Finale** — freeze normal rounds; seed the **top-`finale_size`** by total points into a
   high-stakes heat (large placement-scaled points). Others spectate.
8. **Crowned** — champion = highest total → winner screen (`WinPanel`), leaderboard write (idempotent,
   §8), `EndSession`. EM `Deactivate` returns the server to the lobby.

**Map change is a first-class FSM event.** All timers are `StopOnMapEnd`; on map end they're nuked, so
`OnGameActivate`/`OnRoundRestarted` reconcile and re-arm the loop from persisted state — never rely on
a surviving timer to carry the FSM across a map. A 30 min–3 h session *will* cross maps.

Tiebreak order everywhere: `points` → `round_wins` → earliest to reach the tied total.

---

## 6. Scoring model

| event | points |
|---|---|
| round win (generic) | `100 × mult` |
| KotH hold | `holdSeconds × mult` per player; round winner (most hold) `+50 × mult` |
| finale placement | 1st `500 × mult`, then `500·(N−rank+1)/N × mult` |
| participation floor | played a heat but scored 0 → `5 × mult` (keeps the board lively) |

`mult = 1 + 0.5 × phase` (×1, ×1.5, ×2, …). Points are SteamID-keyed → survive DC/reconnect.

---

## 7. Standings & live overlay

- **Ledger**: lift **`Prophunt.Ranks`** — SteamID-keyed in-memory cache + async off-thread persist +
  `GetTop`/`GetPosition` (= standings/leaderboard) + sum-merge-on-late-load (anti-lost-write). Swap
  `RankEntity` → `PlayerTotals { SteamId, Name, Points, RoundWins }`.
- **In-game overlay**: top-8 as an **HTML center message**, refreshed each round (and per-tick for
  KotH's progress bar). Plain `Print(HudPrintChannel.Center)` fades/flickers, so pair the refresh with
  the **`MS-FixHtmlFlashing`** per-frame keep-alive (patch `gameRules.IsGameRestart` in
  `OnGameFramePost`) or a dedicated HUD.
- **Website / OBS overlay**: EventManager gets ONE additive contract method —

  ```csharp
  // IEventMode (default null → existing modes unaffected)
  string? GetLiveStateJson() => null;
  ```

  `WebBridgeModule.CaptureSnapshot` writes `activeMode?.GetLiveStateJson()` into a new
  `em_state.live_json TEXT` column on its existing heartbeat. ChallengeEngine returns:

  ```json
  { "state":"Round", "challenge":"koth", "challengeName":"King of the Hill",
    "round":7, "phase":2, "secondsLeft":1840, "multiplier":2.0,
    "standings":[ { "steamid":"7656...", "name":"Player", "points":420, "roundWins":3 } ] }
  ```

  No second web bridge, no new DB lane — the site already reads this DB.
- **Season leaderboard**: `ce_leaderboard` cumulative across all Challenge Nights → "best all-round".

---

## 8. Persistence & resilience (EM's PlayerAnalytics lane, `ce_*`)

Through EM's shared provider (`Max Pool Size=4`; `InsertReturnIdentityAsync` for `ce_session.id`;
**no `ce_*` ORM type leaks into any `.Shared`** — `ChallengeStore` exposes a SqlSugar-free surface,
Prophunt-provider pattern). `ulong` SteamID stored via the provider's `long`/`BIGINT UNSIGNED` mapping,
cast at the CLR boundary.

```
ce_session        (id PK AI, challenge, status, phase, round, started_at, ends_at, champion NULL)
ce_session_scores (session_id, steamid, name, points, round_wins,  PK(session_id, steamid))
ce_leaderboard    (steamid PK, name, total_pts, sessions, wins, updated_at)
```

- **Disconnect self-heal** — points SteamID-keyed → standings never break on DC; reconnect restores
  totals (sum-merge). Role-holder DC → engine calls `OnPlayerLeft`; challenge returns
  `Continue`/`ReassignRole`/`VoidRound`/`EndRound`. Wire it via **`IClientListener.OnClientDisconnecting`**
  (the rev.1 `OnPlayerLeft` was called by nobody).
- **Crash → boot to GROUND STATE, do NOT auto-resume the FSM.** (EM's coordinator boots to a known
  state — the *opposite* of replaying a half-finished session; rev.1 conflated them.) On the next
  `Activate` (or plugin load), if an unfinished `ce_session` exists: reload `ce_session_scores` into
  the ledger, mark the old session `Interrupted`, and enter **Lobby**. Operator restarts heats. The
  one in-flight heat is gone; all completed heats' points persist. **Never** re-materialize in-flight
  round entities/teleports/roster (that state-restoration gap already bit InvisibleMod/MiniHumans), and
  **never** assume players auto-reconnect after a crash. Do not touch the roster at boot (`Init`/
  `PostInit`/`OAM` hit a null `sv` → native exit).
- **Idempotent season write** — crown applies the season delta once via an atomic status flip:
  `UPDATE ce_session SET status='Crowned' WHERE id=@id AND status<>'Crowned'`; only if `affected==1`,
  `UPDATE ce_leaderboard SET total_pts = total_pts + @delta …` (atomic add, not read-modify-write).
  Else a resume/retry double-counts (the "Ref-isn't-an-idempotency-key" lesson).

---

## 9. First challenge — King of the Hill (in-repo)

Chosen first: cleanest per-round scoring, zero map dependency, obvious standings.

- **Precache** — the beam material + any capture SFX in `Precache`.
- **StartRound** — pick a placement-validated open point (`EntityPlacementTest` + `DropToGround`, not
  "just a point"), mark a **capture cube** (≈160 u) with **JB's `BeamBox`** (12-edge `env_beam` AABB,
  `EntityIndex`-tracked, pointer-safe — paste-ready, no RE). `TeleportSafe` the roster onto a ring
  (radius ≈300 u, `i/N·360°`) around it. Heat timer 120 s (`RoundSeconds`).
- **Tick** — count players whose origin is inside the cube AABB. **Sole** occupant accrues hold-time;
  **contested** (≥2 inside) → nobody scores that tick (forces a fight for sole control). HTML center
  shows the current leader + a hold progress bar; `BeamBox.SetColor` tints to the leader's team color.
- **Win rule** — `MostObjective`: most hold-seconds at timeout wins the heat; hold-seconds = points
  (so non-winners still score for time held → smooth standings).
- **OnPlayerLeft** — `Continue` (no single role); just drop their hold.
- **Crown / winner screen** — SuperPowers **`WinPanel.ShowTimed`** (self-contained), not a raw
  particle dispatch.

Map-agnostic: the zone is spawned, never map geometry; works on any stock map.

---

## 10. Reuse map (lift, don't write)

| need | source |
|---|---|
| ledger + top-N standings + async persist | `prophunt/Prophunt/Prophunt.Ranks/` |
| KotH capture zone (beam AABB) | `jailbreak-modsharp/jailbreak/JB.Shared/Draw/BeamBox.cs` |
| pulsing ring (optional) | `SuperPowers` `IBeamManager` / `BeamAnimations.Pulse` |
| crown / winner screen | `SuperPowers.CustomRounds.Core/WinPanel.cs` |
| finale **seating** (not ranking) | `Arenas.Core/Queue/QueueManager.cs` |
| persistent center HUD (anti-flicker) | `MS-FixHtmlFlashing` |
| DB + website mirror | EventManager `WebBridgeModule` (+ `GetLiveStateJson`) |
| registry / weighted playlist / convar-capture pattern | port from `FunRounds` (don't re-derive a 3rd copy) |
| safe teleport | `SuperPowers` `TryFindSafeTeleport` / `TeleportSafe` (EntityPlacementTest + DropToGround) |
| wave-survival (later) | new, on top of `MonsterMod SpawnDirector` |

All user-facing text via **`ILocalizerManager`** from commit 1 (key-first per-culture JSON,
`{{double-brace}}` colors) — no hardcoded English, no hand-rolled `\x04` color bytes.

---

## 11. Delivery plan (phased, each with a done-criterion)

- **Phase 0 — relocate + contract skeleton.** Move ChallengeEngine into the EventManager solution;
  `ChallengeEngine.Shared` (IChallenge/IRoundContext/types) + `ChallengeEngine` project-ref
  `EventManager.Shared` Private=false. *Done:* `dotnet build` of the whole EventManager solution is
  green.
- **Phase 1 — host wiring + engine skeleton.** Implement `IEventMode` on the plugin, register via
  `IEventManagerShared`; fix `SpawnMarker` to `DispatchSpawn(dict)`/`SpawnEntitySync`; SessionEngine
  FSM with in-memory ledger + round loop + intermission; `NullChallenge` smoke test; declare
  `GameConVars`; wire `IClientListener → OnPlayerLeft`; game-time clock; map-change reconcile.
  *Done:* on the event server, `!events` → Challenge Night activates, a session runs with bots, loops
  heats, and crowns a winner; deactivate tears everything down clean.
- **Phase 2 — King of the Hill.** BeamBox zone + safe-teleport ring + hold/scoring + Precache.
  *Done:* a real 30-min KotH session end-to-end, standings coherent.
- **Phase 3 — standings + control room.** HTML center overlay (+ flash-fix); add `GetLiveStateJson()`
  to `EventManager.Shared` + `em_state.live_json`; operator actions (skip/force-finale/swap/extend/…);
  settings pickers; escalation phases; finale (Arenas seating); locale JSON.
  *Done:* website/OBS shows the live board; operator can drive a session entirely from the phone.
- **Phase 4 — persistence + resilience.** `ce_*` via EM's lane; per-round writes; boot-to-ground-state
  reload; idempotent season write; disconnect self-heal; season leaderboard.
  *Done:* kill -9 mid-session → reboot → accrued points intact, clean lobby, operator restarts.
- **Phase 5 — more challenges.** Bomb Tag, The Purge, wave-survival — each a thin `IChallenge`.
- **Phase 6 — polish.** Betting windows, MVP callouts, WinPanel winner screen, stream overlay graphic.
- **Later (YAGNI)** — publish `ChallengeEngine.Shared` to NuGet only when a genuinely external
  challenge plugin exists (each external challenge adds a 2nd foreign-`.Shared` ALC hop — defer it).

---

## 12b. Relationship to existing modes — three layers, no rewrite

The existing gamemodes are NOT rewritten. There are three distinct shapes; each existing thing keeps
the shape that fits it (requirement #4 stays intact):

| layer | what it is | lifetime | examples | ChallengeEngine relationship |
|---|---|---|---|---|
| **EventMode** | a whole mode the server switches into, owns its full loop | toggled, one at a time | Prophunt, 1vsAll | Challenge Night is *one more* of these; the others are untouched |
| **Challenge** | a short *scored heat* producing per-player points, map-agnostic | looped, dozens per session | KotH, Bomb Tag, Purge | authored **for** ChallengeEngine (`IChallenge`) |
| **Modifier** | an overlay that isn't a competition on its own | injected mid-session | Invisible, LowGravity, MiniHumans | toggled by the engine during **escalation** (§14) |

Concrete mapping of what exists today:
- **Prophunt** → stays its own EventMode. Map-dependent hiding-spot configs don't fit a 2-min
  map-agnostic heat; wrapping it is a big lift for low value. Leave it.
- **1vsAll** → genuinely *could* become a challenge (1+ seekers, points for survival-time/kills,
  short heats). A thin adapter later — optional, additive.
- **Invisible / LowGravity / MiniHumans** → expose a tiny on/off hook so ChallengeEngine can toggle
  them as **modifiers** during escalation (≈10 lines each — an `enable()/disable()`, not a rewrite).

Recommendation: ship ChallengeEngine with its **own** challenges first (KotH). Adapt 1vsAll and expose
the modifier hooks later, only where they add value. Zero rewrite of existing modes at any point.

## 12c. Expansion opportunities (design for these; build later)

Baked-in extension points so the engine grows without re-architecture — prefix asked to seek these:

- **Modifier system (`IEventModifier`)** — the big one. A tiny contract (`Id`, `Enable()`,
  `Disable()`, optional `GameConVars`) that Invisible/LowGrav/MiniHumans implement. The engine's
  escalation phases and `inject_modifier` action toggle them, so *any* challenge inherits
  "phase 2 = everyone invisible / low-grav / tiny" for free. Turns 3 existing plugins into content.
- **Playlist mode** — `challenge=playlist` rotates a weighted set of challenges across the session
  (borrow FunRounds' weighted `PickRandom`) so a Challenge Night is a variety show, not one game.
- **Team challenges vs FFA** — a challenge declares `TeamScoped`; the ledger sums per-team, standings
  show teams. Opens capture-the-flag / team-KotH / relay formats.
- **Challenge library** — each is a thin `IChallenge`: Bomb Tag (hot-potato C4), The Purge (timed
  free-for-all with escalating weapons), Wave Survival (MonsterMod waves, last-team-standing),
  Gun-Game Ladder (kill to advance, first to knife wins), Zone Shrink (battle-royale-lite ring),
  Red-Light/Green-Light (movement-gated dash), Melee Royale. All map-agnostic via spawned entities.
- **Engagement hooks** — chat betting on the next heat's winner, MVP-of-the-round callout, kill/hold
  streak combos with bonus multipliers, "underdog" catch-up bonus. All ride the existing ledger.
- **Champion payoff** — season leaderboard → a cs-tema website page; Discord webhook announces the
  crowned champion + top-3; optional cosmetic (crown model / name tag) for the reigning champ.
- **Twitch/stream control** — chat votes the next injected modifier (the `inject_modifier` action is
  already the hook); the OBS overlay (`GetLiveStateJson`) shows the live board + countdown.
- **Adapters** — wrap an existing EventMode (1vsAll) as a challenge via a `ModeChallenge` shim so the
  session can mix full modes and micro-heats.

These need no new architecture — the `IChallenge` contract, escalation, `inject_modifier`,
`GetLiveStateJson`, and the ledger already carry them. Phase 5/6 territory; the design won't fight
them.

## 12. Open decisions — defaults chosen, override anytime

1. Points curve → §6 defaults (win 100, KotH hold=pts, participation floor 5, finale placement-scaled).
2. Escalation cadence → every 15 min.
3. Finale size → top 6.
4. Payout → season-ladder hook only for now; real reward later.
5. DB → EM's PlayerAnalytics lane now (zero new infra), `ce_*` prefix for a clean future move.
6. Repo → build inside EventManager, archive standalone repo (pending prefix ack).
```
