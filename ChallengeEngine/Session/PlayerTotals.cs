namespace ChallengeEngine.Session;

/// <summary>Running per-player session totals (keyed by SteamID → survives DC/reconnect).</summary>
internal sealed class PlayerTotals
{
    public required ulong  SteamId { get; init; }
    public required string Name    { get; set; }

    public int Points    { get; set; }
    public int RoundWins { get; set; }
}
