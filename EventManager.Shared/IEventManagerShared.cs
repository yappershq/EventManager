using System;

namespace EventManager.Shared;

/// <summary>
/// The event-server gate. EventManager.Core publishes this in <c>PostInit</c>; fun-mode plugins
/// look it up in <c>OnAllModulesLoaded</c> via
/// <c>GetOptionalSharpModuleInterface&lt;IEventManagerShared&gt;(IEventManagerShared.Identity)</c>.
///
/// The gate is OPTIONAL by design:
///   - absent (dedicated server, e.g. prophunt): activate yourself exactly as today;
///   - present (event server): register and stay dormant until an operator enables you.
///
/// Ground state is always "no event active" — the server runs vanilla until <c>/events</c>
/// activates one. At most one event is active at a time.
/// </summary>
public interface IEventManagerShared
{
    public const string Identity = "EventManager.Shared";

    /// <summary>
    /// Register an event mode (call from YOUR OnAllModulesLoaded). The mode stays dormant until
    /// activated by an operator. Dispose the returned handle in Shutdown — disposing deactivates
    /// the mode first if it is currently active.
    /// </summary>
    /// <exception cref="ArgumentException">A mode with the same <see cref="IEventMode.Id"/> is already registered.</exception>
    IDisposable RegisterEvent(IEventMode mode);

    /// <summary>Id of the active event, or null when the server is vanilla.</summary>
    string? ActiveEventId { get; }

    /// <summary>True when <paramref name="eventId"/> is the currently active event.</summary>
    bool IsActive(string eventId);
}
