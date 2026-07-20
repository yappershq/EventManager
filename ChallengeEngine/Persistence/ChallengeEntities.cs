using System;
using SqlSugar;

namespace ChallengeEngine.Persistence;

// SteamID stored as long (BIGINT) — cast ulong<->long at the CLR boundary (bit-preserving), avoiding
// any ORM ulong/BIGINT-UNSIGNED quirks. These types live in Core, NEVER in .Shared (ORM-free rule).

[SugarTable("ce_session")]
internal sealed class CeSession
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public int Id { get; set; }
    [SugarColumn(Length = 32)] public string ServerTag { get; set; } = "";
    [SugarColumn(Length = 32)] public string Challenge { get; set; } = "";
    [SugarColumn(Length = 16)] public string Status    { get; set; } = "Active"; // Active | Interrupted | Crowned
    public int      Phase     { get; set; }
    public int      Round     { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime EndsAt    { get; set; }
    [SugarColumn(IsNullable = true)] public long? Champion { get; set; }
    public DateTime UpdatedAt { get; set; }
}

[SugarTable("ce_session_scores")]
internal sealed class CeScore
{
    [SugarColumn(IsPrimaryKey = true)] public int  SessionId { get; set; }
    [SugarColumn(IsPrimaryKey = true)] public long SteamId   { get; set; }
    [SugarColumn(Length = 64)] public string Name { get; set; } = "";
    public int      Points    { get; set; }
    public int      RoundWins { get; set; }
    public DateTime UpdatedAt { get; set; }
}

[SugarTable("ce_leaderboard")]
internal sealed class CeLeaderboard
{
    [SugarColumn(IsPrimaryKey = true)] public long SteamId { get; set; }
    [SugarColumn(Length = 64)] public string Name { get; set; } = "";
    public long     TotalPts  { get; set; }
    public int      Sessions  { get; set; }
    public int      Wins      { get; set; }
    public DateTime UpdatedAt { get; set; }
}
