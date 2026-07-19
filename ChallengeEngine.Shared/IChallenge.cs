namespace ChallengeEngine.Shared;

/// <summary>
/// A single challenge (King of the Hill, Bomb Tag, …). It implements ONLY round logic — the engine
/// drives timing, scoring, standings, escalation, finale, and persistence around it. Keep it thin;
/// touch the game exclusively through <see cref="IRoundContext"/>.
/// </summary>
public interface IChallenge
{
    /// <summary>Stable machine id, e.g. "koth". Lowercase, no spaces.</summary>
    string Id { get; }

    /// <summary>Localization KEY for the display name (the engine renders it), e.g. "challenge.koth.name".</summary>
    string DisplayNameKey { get; }

    /// <summary>The engine won't start a heat below this many human players.</summary>
    int MinPlayers { get; }

    /// <summary>Hard timeout for a heat; the engine calls <see cref="ForceEnd"/> when it elapses.</summary>
    int RoundSeconds { get; }

    ChallengeWinRule WinRule { get; }

    /// <summary>Set up ONE heat: assign roles, spawn hazards/zones, apply modifiers.</summary>
    void StartRound(IRoundContext ctx);

    /// <summary>The engine's round timer fired — collect and return the final result now.</summary>
    RoundResult ForceEnd(IRoundContext ctx);

    /// <summary>A player left mid-heat — tell the engine how the heat should react.</summary>
    LeaveReaction OnPlayerLeft(IRoundContext ctx, ulong steamId);

    /// <summary>Optional per-tick logic (progress bars, zone checks, …).</summary>
    void Tick(IRoundContext ctx) { }

    /// <summary>Remove entities/effects between heats. Always called after a heat ends.</summary>
    void Cleanup(IRoundContext ctx) { }
}
