using System.Collections.Generic;

namespace EventManager.Shared;

/// <summary>
/// One switchable event/fun mode (Prophunt, MiniHumans, InvisibleMod, …). Implemented by the
/// fun-mode plugin and handed to <see cref="IEventManagerShared.RegisterEvent"/>.
///
/// Activate/Deactivate run on the game thread and MUST be safe to call repeatedly over the
/// server's lifetime (streamers toggle modes mid-session for intros): Activate installs/enables
/// your takeover, Deactivate reverses it AND restores any live-player state you changed
/// (teams, freeze, visibility, …) — not just your hooks.
/// </summary>
public interface IEventMode
{
    /// <summary>Stable machine id used in commands, e.g. "prophunt". Lowercase, no spaces.</summary>
    string Id { get; }

    /// <summary>Human-readable name for menus/chat, e.g. "Prop Hunt".</summary>
    string DisplayName { get; }

    /// <summary>
    /// When true the manager issues <c>mp_restartgame 1</c> right after Activate so the mode
    /// starts from a clean round. Default true; return false for modes that apply live.
    /// </summary>
    bool RequiresRoundRestart => true;

    void Activate();

    void Deactivate();

    /// <summary>
    /// The mode's live settings, owned and validated by the mode itself (no convars involved).
    /// Re-queried on every render, so return current values.
    /// </summary>
    IReadOnlyList<EventSetting> GetSettings() => [];

    /// <summary>
    /// Apply a setting. Values arrive as strings (menu/chat/console all speak strings);
    /// parse + validate here and return false to reject. Applies live when the mode is active.
    /// </summary>
    bool TrySetSetting(string key, string value) => false;

    /// <summary>
    /// Game convars this event needs while active (e.g. <c>mp_playerid 2</c> for hide-and-seek
    /// modes). The MANAGER owns them: current values are captured when the event starts, the
    /// event's values applied, and the originals restored on deactivate/switch — even when the
    /// event's own teardown fails. Do not set these yourself in Activate/Deactivate.
    /// </summary>
    IReadOnlyDictionary<string, string> GameConVars => EmptyConVars;

    private static readonly Dictionary<string, string> EmptyConVars = new();
}
