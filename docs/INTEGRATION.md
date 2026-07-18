# Integrating a gamemode with EventManager

Goal: the SAME plugin build runs standalone on its dedicated server AND dormant-until-enabled on
the event server. The gate is optional — your plugin decides at load time which world it is in.

## The three-step pattern

**1. Reference the contract** (`EventManager.Shared` — NuGet `YappersHQ.EventManager.Shared` once
published; until then a local ProjectReference/feed):

```xml
<PackageReference Include="YappersHQ.EventManager.Shared" Version="*" ExcludeAssets="runtime" />
```

**2. Split your bootstrap into dormant vs active.** Everything that *takes over* the server
(round-flow hooks, forced teams, model changes, per-tick work) moves behind `Activate()`;
everything passive (config load, interface publish, DB init) stays in the normal lifecycle.

**3. Gate in OnAllModulesLoaded:**

```csharp
private IDisposable? _eventRegistration;

public void OnAllModulesLoaded()
{
    var gate = sharedSystem.GetSharpModuleManager()
        .GetOptionalSharpModuleInterface<IEventManagerShared>(IEventManagerShared.Identity)?.Instance;

    if (gate is null)
        ActivateGameplay();                       // dedicated server — behave exactly as today
    else
        _eventRegistration = gate.RegisterEvent(_myEventMode);  // event server — wait for /events
}

public void Shutdown() => _eventRegistration?.Dispose();  // deactivates first if active
```

`RegisterEvent` throws `ArgumentException` on a duplicate `Id` — catch + log it (see the
LowGravity reference implementation) so a copy-pasted id can't take your plugin down.

**Known limitation — hot reload:** hot-reloading EventManager.Core replaces the coordinator, and
already-loaded consumers hold registrations into the OLD one — the event list comes back empty.
After hot-reloading Core, reload the consumer plugins too (or change map). Full server restart is
always safe.

## Contract rules

- `Activate()` / `Deactivate()` are called on the game thread and MUST be repeatable — streamers
  toggle modes mid-session. Guard native detours / buffers for re-install (idempotent).
- `Deactivate()` restores live-player state you changed (teams, freeze, visibility, scale…),
  not just your hooks. "Dormant" means indistinguishable from not installed.
- **Teardown only — never issue game transitions** (`mp_restartgame`, warmup commands) from your
  adapter. The MANAGER owns transitions: `events off` drops into a paused warmup lobby; with
  start mode *Warmup* (default) `events on` ARMS your mode (Activate not yet called!) and
  `events start` activates + ends warmup into round 1; with *Direct* it activates + restarts
  immediately.
- `RequiresRoundRestart => true` (default) makes the manager restart the round when starting
  outside a warmup. Return false only if your mode applies cleanly mid-round.
- **Game convars belong in `GameConVars`** (v1.1.0+), not in Activate/Deactivate: declare e.g.
  `["mp_playerid"] = "2"` and the manager captures current values on start, applies yours, and
  restores the originals on deactivate/switch — even if your teardown throws.
- **Deactivate must restore ENTITY-LEVEL player state explicitly** (transmit blocks, model
  scale, render color): controller/pawn entities are REUSED across respawns and warmup starts,
  so "the next round will fix it" is false. Sweep in-game players in your teardown
  (see InvisibleMod's UnhidePlayer / MiniHumans' RestoreSize sweeps).
- **Operational rule: never hot-reload EventManager.Core alone.** Consumers hold registrations
  into the old coordinator; after a Core-only reload the event registry is empty until every
  mode plugin reloads too. A Core redeploy on a live event server = full server restart.
- Settings: expose via `GetSettings()` (re-queried per render, return live values), validate in
  `TrySetSetting(key, value)` — strings at the boundary, you own parsing and state. No convars.

**Convar-flip adapters: own the convar across cfg re-syncs.** If your gamemode re-applies its
autoexec .cfg per map (e.g. Prophunt's `OnServerSpawn → SyncConVars`), the file value will
overwrite the gate's dormant `x_enabled 0` on every map change. The adapter must re-assert the
coordinator's state AFTER the sync — ModSharp game listeners run in DESCENDING priority, so give
the gate listener a lower priority than the config module's and set the convar in `OnServerSpawn`
(see Prophunt's `EventGateModule`).

## Per-gamemode notes (recon 2026-07-18, see SPEC.md)

- **Prophunt** — dormancy exists: `ph_enabled` gates `RoundManager.IsReady`, all takeover hooks
  no-op when off. Adapter: Activate → set `ph_enabled 1`; Deactivate → `ph_enabled 0`. Both
  effective at the next round boundary; keep `RequiresRoundRestart = true`.
- **InvisibleMod / MiniHumans** — boot hot. Defer the `foreach (m in modules) m.Init()` loop in
  the plugin entry to Activate; run the existing `Shutdown()` loop on Deactivate PLUS restore
  player state (unfreeze, team choice, visibility). MiniHumans: guard the 3 native hull detours
  (`InstallHullDetour`) against double-install and re-alloc buffers on re-activation.
- **1vsALL** — same defer-the-Init-loop seam. Its `!streamer` command stays for now; moves to
  the manager when the roles phase lands.
- **FunRounds** — optional: register itself as event id `funrounds` (Activate = allow rounds,
  Deactivate = `StopRound` + suppress selection).

## Testing an integration

1. Deploy your plugin + EventManager to a test server → boot → your mode must NOT affect
   gameplay (vanilla check: teams free, no forced models, no HUD).
2. `events on <id>` (console) → full gameplay takeover, as standalone.
3. `events off` → verify vanilla again INCLUDING live players (were frozen? scaled? teamed?).
4. Toggle twice more (re-activation path — the intro-toggling use case).
5. Remove EventManager from the server → boot → your mode must take over immediately
   (standalone regression check).
