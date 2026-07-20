namespace ChallengeEngine.Modifiers;

/// <summary>
/// An escalation overlay the engine can toggle mid-session (low gravity, bunny-hop, …). Enable applies
/// it, Disable fully reverses it. Internal for now (built-in convar modifiers); promote to a shared
/// contract when external plugins (Invisible/MiniHumans) want to register their own.
/// </summary>
internal interface IModifier
{
    string Id { get; }
    string DisplayNameKey { get; }

    void Enable();
    void Disable();
}
