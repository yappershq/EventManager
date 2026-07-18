using System;
using System.Collections.Generic;
using System.Linq;
using EventManager.Plugins;
using EventManager.Shared;
using Microsoft.Extensions.Logging;

namespace EventManager.Modules;

internal enum StartMode
{
    /// <summary>Activating an event arms it in a paused warmup lobby; the operator starts it explicitly.</summary>
    Warmup,

    /// <summary>Activating an event starts it immediately (round restart).</summary>
    Direct,
}

internal enum ActivateResult
{
    Started,
    Armed,
    Unknown,
    Already,
    Failed,
}

/// <summary>
/// The registry + activation state machine behind <see cref="IEventManagerShared"/>.
///
/// States: vanilla (nothing on) → armed (paused warmup lobby, event chosen but NOT running —
/// nothing of the mode touches players until the operator starts) → active (event running).
/// The coordinator owns ALL game transitions (warmup lobby / warmup end / round restart);
/// adapters only install/tear down their mode. Ground state after "off" is the warmup lobby.
/// At most one event is armed or active; everything runs on the game thread, so no locking.
/// </summary>
internal sealed class EventCoordinator : IModule, IEventManagerShared
{
    private readonly ILogger<EventCoordinator> _logger;
    private readonly InterfaceBridge           _bridge;

    private readonly Dictionary<string, IEventMode> _events = new(StringComparer.OrdinalIgnoreCase);

    private string? _activeId;
    private string? _armedId;
    private bool    _inLobby;

    public StartMode StartMode { get; set; } = StartMode.Warmup;

    public EventCoordinator(ILogger<EventCoordinator> logger, InterfaceBridge bridge)
    {
        _logger = logger;
        _bridge = bridge;
    }

    // ── IModule ────────────────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
    {
        // Plugin unload: tear down quietly — no game transitions (server may be shutting down).
        DeactivateCore();
        _armedId = null;
        _events.Clear();
    }

    // ── IEventManagerShared ───────────────────────────────────────────────

    public string? ActiveEventId => _activeId;

    public bool IsActive(string eventId) => _activeId is not null
        && string.Equals(_activeId, eventId, StringComparison.OrdinalIgnoreCase);

    public IDisposable RegisterEvent(IEventMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);

        if (string.IsNullOrWhiteSpace(mode.Id))
            throw new ArgumentException("IEventMode.Id must be non-empty.", nameof(mode));

        if (!_events.TryAdd(mode.Id, mode))
            throw new ArgumentException($"An event with id '{mode.Id}' is already registered.", nameof(mode));

        _logger.LogInformation("[EventManager] Registered event '{Id}' ({Name}). {Count} total.",
            mode.Id, mode.DisplayName, _events.Count);

        return new Registration(this, mode);
    }

    // ── Coordinator API (commands/menu) ───────────────────────────────────

    public string? ArmedEventId => _armedId;

    public IReadOnlyList<IEventMode> Registered => _events.Values.OrderBy(e => e.Id).ToList();

    public IEventMode? Find(string id) => _events.GetValueOrDefault(id);

    /// <summary>Select an event: Direct mode starts it now; Warmup mode arms it in the lobby.</summary>
    public ActivateResult TryActivate(string id, out IEventMode? mode)
    {
        if (!_events.TryGetValue(id, out mode))
            return ActivateResult.Unknown;

        if (IsActive(mode.Id) || string.Equals(_armedId, mode.Id, StringComparison.OrdinalIgnoreCase))
            return ActivateResult.Already;

        DeactivateCore();
        _armedId = null;

        if (StartMode == StartMode.Warmup)
        {
            // Arm only — the mode stays completely off until Start(); intros can't be disturbed
            // by mode logic (e.g. a match coordinator forcing round starts).
            _armedId = mode.Id;
            EnterLobby();
            _logger.LogInformation("[EventManager] Event '{Id}' armed in warmup lobby.", mode.Id);
            return ActivateResult.Armed;
        }

        if (!ActivateCore(mode))
            return ActivateResult.Failed;

        GoLive(mode.RequiresRoundRestart);
        return ActivateResult.Started;
    }

    /// <summary>Start the armed event: activate the mode and end the warmup lobby into round 1.</summary>
    public ActivateResult Start(out IEventMode? mode)
    {
        mode = _armedId is null ? null : _events.GetValueOrDefault(_armedId);

        if (mode is null)
        {
            _armedId = null;
            return ActivateResult.Unknown;
        }

        _armedId = null;

        if (!ActivateCore(mode))
            return ActivateResult.Failed; // stays in the lobby, nothing armed

        GoLive(mode.RequiresRoundRestart);
        return ActivateResult.Started;
    }

    /// <summary>Disarm without starting (only meaningful while armed). Returns the disarmed mode.</summary>
    public IEventMode? Disarm()
    {
        if (_armedId is null) return null;

        var mode = _events.GetValueOrDefault(_armedId);
        _armedId = null;
        _logger.LogInformation("[EventManager] Disarmed — lobby stays vanilla.");
        return mode;
    }

    /// <summary>Deactivate the running event and drop the server into the warmup lobby.</summary>
    public IEventMode? DeactivateCurrent()
    {
        var mode = DeactivateCore();
        if (mode is not null)
            EnterLobby();

        return mode;
    }

    // ── Internals ──────────────────────────────────────────────────────────

    private bool ActivateCore(IEventMode mode)
    {
        try
        {
            mode.Activate();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EventManager] Event '{Id}' threw in Activate — staying vanilla.", mode.Id);
            return false;
        }

        _activeId = mode.Id;
        _logger.LogInformation("[EventManager] Event '{Id}' activated.", mode.Id);
        return true;
    }

    /// <summary>Tear down the running event. No game transition — callers decide.</summary>
    private IEventMode? DeactivateCore()
    {
        if (_activeId is null || !_events.TryGetValue(_activeId, out var mode))
        {
            _activeId = null;
            return null;
        }

        // Clear BEFORE calling out: even if Deactivate throws, the manager must not consider a
        // half-torn-down event "active" — operators need /events to keep working.
        _activeId = null;

        try
        {
            mode.Deactivate();
            _logger.LogInformation("[EventManager] Event '{Id}' deactivated.", mode.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EventManager] Event '{Id}' threw in Deactivate — state may need a map change.", mode.Id);
        }

        return mode;
    }

    /// <summary>Paused warmup lobby — the between-events ground state.</summary>
    private void EnterLobby()
    {
        if (_inLobby) return;

        _bridge.ModSharp.ServerCommand("mp_warmup_start");
        _bridge.ModSharp.ServerCommand("mp_warmup_pausetimer 1");
        _inLobby = true;
    }

    /// <summary>Transition into live play: end any warmup, restart the round if the mode needs it.</summary>
    private void GoLive(bool requiresRoundRestart)
    {
        _bridge.ModSharp.ServerCommand("mp_warmup_pausetimer 0");
        _bridge.ModSharp.ServerCommand("mp_warmup_end"); // no-op when not in warmup

        if (!_inLobby && requiresRoundRestart)
            _bridge.ModSharp.ServerCommand("mp_restartgame 1");

        _inLobby = false;
    }

    private void Unregister(IEventMode mode)
    {
        if (string.Equals(_armedId, mode.Id, StringComparison.OrdinalIgnoreCase))
            _armedId = null;

        if (IsActive(mode.Id))
            DeactivateCore(); // no transition — unregistration usually means plugin/server shutdown

        if (_events.Remove(mode.Id))
            _logger.LogInformation("[EventManager] Unregistered event '{Id}'.", mode.Id);
    }

    private sealed class Registration(EventCoordinator owner, IEventMode mode) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            owner.Unregister(mode);
        }
    }
}
