using System.Collections.Generic;

namespace ChallengeEngine.Shared;

/// <summary>How a challenge decides its per-heat winner.</summary>
public enum ChallengeWinRule { LastAlive, FirstToScore, MostObjective, Timed }

/// <summary>How the engine should react when a player leaves mid-heat.</summary>
public enum LeaveReaction { Continue, ReassignRole, VoidRound, EndRound }

/// <summary>Points a player earned in one heat (+ an optional note for the log/overlay).</summary>
public sealed record PlayerScore(ulong SteamId, int Points, string? Note = null);

/// <summary>The outcome of one heat: per-player points + the heat winner (null = void).</summary>
public sealed record RoundResult(IReadOnlyList<PlayerScore> Scores, ulong? RoundWinnerSteamId);

/// <summary>
/// Opaque, pointer-safe handle to an engine-spawned entity. Backed by a serial-versioned entity
/// handle inside the engine (indices get recycled across heats/maps), so a challenge holds this and
/// passes it back to <see cref="IRoundContext.RemoveEntity"/> instead of a raw index or pointer.
/// </summary>
public readonly record struct EntityToken(int Id)
{
    public static readonly EntityToken None = new(0);
    public bool IsValid => Id != 0;
}
