using System;
using System.Collections.Generic;
using System.Linq;
using EventManager.Plugins;
using EventManager.Shared;
using Microsoft.Extensions.Logging;

namespace EventManager.Modules;

/// <summary>
/// The registry + activation state machine behind <see cref="IEventManagerShared"/>.
/// Ground state is "no event active"; at most one event is active; everything runs on the
/// game thread (command/menu context), so no locking.
/// </summary>
internal sealed class EventCoordinator : IModule, IEventManagerShared
{
    private readonly ILogger<EventCoordinator> _logger;
    private readonly InterfaceBridge           _bridge;

    private readonly Dictionary<string, IEventMode> _events = new(StringComparer.OrdinalIgnoreCase);

    private string? _activeId;

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
        // Plugin unload: leave the server vanilla.
        DeactivateCurrent();
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

    public IReadOnlyList<IEventMode> Registered => _events.Values.OrderBy(e => e.Id).ToList();

    public IEventMode? Find(string id) => _events.GetValueOrDefault(id);

    /// <summary>Activate an event by id, deactivating the current one first.</summary>
    public bool TryActivate(string id, out IEventMode? mode, out string reason)
    {
        mode   = null;
        reason = "";

        if (!_events.TryGetValue(id, out mode))
        {
            reason = "unknown";
            return false;
        }

        if (IsActive(mode.Id))
        {
            reason = "already";
            return false;
        }

        DeactivateCurrent();

        try
        {
            mode.Activate();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EventManager] Event '{Id}' threw in Activate — staying vanilla.", mode.Id);
            reason = "failed";
            return false;
        }

        _activeId = mode.Id;
        _logger.LogInformation("[EventManager] Event '{Id}' activated.", mode.Id);

        if (mode.RequiresRoundRestart)
            _bridge.ModSharp.ServerCommand("mp_restartgame 1");

        return true;
    }

    /// <summary>Deactivate the active event, if any. Returns the mode that was deactivated.</summary>
    public IEventMode? DeactivateCurrent()
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
            _logger.LogInformation("[EventManager] Event '{Id}' deactivated — server is vanilla.", mode.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EventManager] Event '{Id}' threw in Deactivate — state may need a map change.", mode.Id);
        }

        return mode;
    }

    private void Unregister(IEventMode mode)
    {
        if (IsActive(mode.Id))
            DeactivateCurrent();

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
