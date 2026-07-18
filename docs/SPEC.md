# EventManager — Spec & Requirements

Status: DRAFT — requirements captured 2026-07-18 from prefix (Discord, #general). Design filled in
after codebase recon. This doc is the source of truth we return to for the NuGet/integration phase.

## Problem

The Stream/Event server runs fun modes (1vsAll, MiniHumans, Prophunt, InvisibleMod, …). Today they
can't all be deployed at once: each takes over the server the moment it loads (hooks, listeners,
round flow). Switching modes = redeploying files. There is no centralized gate.

## Requirements (prefix, verbatim intent)

1. **Central gate** (`EventManager`): all event plugins deployed simultaneously; exactly one active
   at a time; switchable on the LIVE server.
2. **Gate is optional.** The same plugin build must run standalone on its dedicated server
   (e.g. Prophunt on the prophunt server) when EventManager is absent. Consumers check for the gate
   in `OnAllModulesLoaded`; absent → enable themselves exactly as today. Zero behavior change
   outside the event server.
3. **Default = no event.** With the gate present, ground state is vanilla CS2 (e.g. custom workshop
   maps). Events are opt-in via command; "off" is not a mode, it's the default.
4. **`/events` command** — select/activate/deactivate the event and view/edit the active event's
   settings. Events declare their OWN settings and share them with EventManager — no convar
   wrangling per plugin.
5. **Live toggling is a first-class use case** — streamers do intros: temporarily flip MiniHumans or
   InvisibleMod on/off mid-map, possibly mid-round. Activate/Deactivate must work at runtime, not
   just at map change.
6. **Stream toolkit** — extra tools streamers want (see below).
7. **Roadmap: role ownership.** Some event plugins ship role commands (`/streamer`, `/hunter`,
   `/seeker`). Later, EventManager takes ownership: events declare their roles, the manager owns
   assignment + commands, events consume "who is what". v1 shapes the contract so this slots in.
8. **Public repo** (yappershq) from day one. NuGet package later WITH prefix (manual step);
   then integrate gamemode-by-gamemode.

## Prior art in-house

- Warmup gates already exist in some gamemodes (prefix pointer) — recon to identify and reuse the idiom.
- FunRounds: config-driven round-mode engine with per-mode activate/teardown.
- Publisher/consumer lifecycle: publishers `RegisterSharpModuleInterface` in PostInit, consumers
  look up in OnAllModulesLoaded (ModSharp guarantees all PostInits before any OAM).

## Stream toolkit (candidates — v1 ships the cheap high-value subset)

- Intro mode: block round end + freeze timer (+ optional god) while setting up a shot
- Respawn all / respawn player
- Freeze / unfreeze all
- Countdown + center-HUD announce ("event starts in 5…")
- Quick HP / weapon presets for skits
- Teleport-all-to-me / swap teams (later)

## Recon findings that shaped the design (2026-07-18)

- **Prophunt already has a dormancy switch**: `ph_enabled` drives `RoundManager.IsReady`, which every
  takeover hook consults — hooks stay installed but pass through. Its EventMode adapter is nearly free:
  Activate = `ph_enabled 1`, Deactivate = `ph_enabled 0` (effective at the next round boundary).
- **InvisibleMod / MiniHumans boot hot** — zero dormancy; every module installs hooks and starts forcing
  teams/freeze/invisibility in `Init()`. Their gate seam is the `foreach m.Init()` loop in the plugin
  entry (defer to Activate). Deactivate needs *state restore* (unfreeze/teams/visibility), not just
  unhook. MiniHumans' native hull detours must be idempotent-guarded for re-activation.
- **1vsALL** exists (`the 1vsALL repo`, OneVsAll) — standalone plugin, `!streamer` command; same
  boot-hot shape.
- **FunRounds** is the in-house prior art for switchable modes (registry + apply/revert lifecycle) and
  the template for plugin skeleton, CommandCenter commands, admin manifest, and optional-interface use.
- ModSharp semantics: `GetOptionalSharpModuleInterface<T>` returns **null** when absent (the optional
  gate); publish in PostInit → consume in OnAllModulesLoaded is guaranteed ordered.

## Design (v1 — built 2026-07-18)

Three projects, FunRounds-style skeleton (DI + InterfaceBridge + IModule internal lifecycle):

- **EventManager.Shared** — the contract, future NuGet `YappersHQ.EventManager.Shared`. No 3rd-party types.
- **EventManager.Core** — the gate plugin (module `EventManager`): registry/coordinator, `/events`
  command + menu, stream tools, locales.
- **EventManager.LowGravity** — in-repo example event proving the contract end-to-end (sv_gravity
  capture/set/restore + one Int setting). Deployable for smoke tests; not required in production.

### Shared contract

```csharp
public interface IEventManagerShared  // Identity = "EventManager.Shared"
{
    IDisposable RegisterEvent(IEventMode mode); // call in your OAM; dispose in Shutdown
    string? ActiveEventId { get; }
    bool IsActive(string eventId);
}

public interface IEventMode
{
    string Id { get; }                 // "prophunt", "minihumans", …
    string DisplayName { get; }
    bool RequiresRoundRestart => true; // manager issues mp_restartgame 1 after Activate
    void Activate();
    void Deactivate();
    IReadOnlyList<EventSetting> GetSettings() => [];
    bool TrySetSetting(string key, string value) => false;
}

public sealed record EventSetting(string Key, string DisplayName, EventSettingType Type,
                                  string Value, IReadOnlyList<string>? Choices = null);
public enum EventSettingType { Bool, Int, Float, Text, Choice }
```

Settings are string-typed at the boundary on purpose: menu + chat + console all speak strings, events
parse/validate in `TrySetSetting` and remain the single owner of their state. No convars involved.
Default interface members keep the consumer diff minimal.

### Manager semantics

- Publish `IEventManagerShared` in **PostInit**; events register in **their** OAM (ordering guaranteed).
- Ground state = no active event, always — including after server restart (no persistence, deliberate).
- `Activate(id)`: Deactivate current (CallSafe) → Activate new (CallSafe; failure → event stays off) →
  `mp_restartgame 1` if `RequiresRoundRestart`.
- `Deactivate()`: symmetric; also fired when a registered event's plugin unloads (its IDisposable).
- Exactly one active event max; everything runs on the game thread (command/menu context).

### Consumer pattern (the optional gate)

```csharp
// in OnAllModulesLoaded:
var em = sharpModuleManager
    .GetOptionalSharpModuleInterface<IEventManagerShared>(IEventManagerShared.Identity)?.Instance;
if (em is null)
    ActivateStandalone();                  // dedicated server: exactly today's behaviour
else
    _reg = em.RegisterEvent(myEventMode);  // event server: dormant until /events enables us
// Shutdown: _reg?.Dispose();
```

### /events UX

Admin-gated (`eventmanager:admin` via MountAdminManifest; wildcard `*` admins resolve it).

- `!events` → menu: list of events (● marks active; "Disable current" on top when one is running) →
  per-event page: Enable / settings (Bool toggles + Choice cycles inline; Int/Float/Text via chat form).
  Plus a **Stream tools** page (below).
- Text forms (chat `!events …` and server console `events …`):
  `events list` · `events on <id>` · `events off` · `events set <id> <key> <value>`
  · `events intro on|off` · `events respawnall` · `events countdown [secs]`

### Stream toolkit v1

- **Intro mode** — captures + sets `mp_ignore_round_win_conditions 1` (round never ends while you set
  up a shot); off restores the captured value.
- **Respawn all** — respawns every dead T/CT.
- **Countdown** — 5..1 center-screen + chat countdown, "GO!" at zero (announce an event start on stream).

Roadmap (not v1): freeze-all, HP/weapon presets, teleport-all-to-me, AdminPanel submenu integration,
role ownership (events declare roles like streamer/hunter/seeker; manager owns assignment + commands).

## Integration guide per gamemode

See `docs/INTEGRATION.md` (kept in-repo; written with the v1 build). Short version per game:

| Game | Activate | Deactivate | Notes |
|---|---|---|---|
| Prophunt | `ph_enabled 1` | `ph_enabled 0` | dormancy already exists; effective next round |
| InvisibleMod | run deferred `m.Init()` loop | Shutdown loop + restore players | add restore (unfreeze/teams/visibility) |
| MiniHumans | same as InvisibleMod | same | guard native hull-detour re-install (idempotent) |
| 1vsALL | same seam | same | `!streamer` ownership moves to roles phase later |
| FunRounds | register itself as event `funrounds` | stop + unregister | optional — it's already round-scoped |

## NuGet phase (needs prefix)

- Publish `EventManager.Shared` to NuGet so gamemode repos can reference it without local feeds
  (never repeat the synthetic-high local-feed version trap).
- Then: integrate Prophunt, InvisibleMod, MiniHumans, FunRounds/1vsAll, … one repo at a time.
