namespace EventManager.Shared;

/// <summary>Argument kind an <see cref="EventAction"/> expects.</summary>
public enum EventActionArg
{
    None,

    /// <summary>A player — invoked with the target's SteamID64 as the argument string.</summary>
    Player,
}

/// <summary>
/// A mode-specific verb the event exposes to operators (in-game or website) — e.g. "set_seeker"
/// for hide-and-seek modes, "set_solo" for 1vsAll. Invoked via
/// <see cref="IEventMode.TryInvokeAction"/>; works while the event is active OR armed (roles are
/// typically assigned during the intro lobby).
/// </summary>
public sealed record EventAction(
    string Key,
    string DisplayName,
    EventActionArg Arg = EventActionArg.None);
