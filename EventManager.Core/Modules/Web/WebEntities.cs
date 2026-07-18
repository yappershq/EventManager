using System;
using SqlSugar;

namespace EventManager.Modules.Web;

// Tables live in the PlayerAnalytics database for now (prefix 2026-07-18: reuse existing creds,
// zero new env/config anywhere; move to a dedicated DB later). All names are em_-prefixed so the
// eventual move is a straight table copy.

/// <summary>Observe lane: one row per server, heartbeat + live state.</summary>
[SugarTable("em_state")]
internal sealed class EmState
{
    [SugarColumn(IsPrimaryKey = true, Length = 32)]
    public string ServerTag { get; set; } = "";

    public DateTime HeartbeatAt { get; set; }

    [SugarColumn(Length = 128)] public string  Map           { get; set; } = "";
    public int     PlayerCount   { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)] public string? ActiveEventId { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)] public string? ArmedEventId  { get; set; }
    [SugarColumn(Length = 16)] public string  StartMode     { get; set; } = "";
    public bool    IntroActive   { get; set; }

    [SugarColumn(ColumnDataType = "TEXT", IsNullable = true)]
    public string? DownloadsJson { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>Observe lane: one row per registered event — the settings schema the site renders.</summary>
[SugarTable("em_catalog")]
internal sealed class EmCatalog
{
    /// <summary>"{serverTag}:{eventId}" — single-column PK keeps upserts trivial.</summary>
    [SugarColumn(IsPrimaryKey = true, Length = 100)]
    public string Id { get; set; } = "";

    [SugarColumn(Length = 32)] public string ServerTag   { get; set; } = "";
    [SugarColumn(Length = 64)] public string EventId     { get; set; } = "";
    [SugarColumn(Length = 128)] public string DisplayName { get; set; } = "";

    [SugarColumn(ColumnDataType = "TEXT")] public string SettingsJson { get; set; } = "[]";
    [SugarColumn(ColumnDataType = "TEXT")] public string ConVarsJson  { get; set; } = "{}";

    public bool     Registered { get; set; } = true;
    public DateTime UpdatedAt  { get; set; }
}

/// <summary>Execute lane: the site inserts rows; the server polls, executes, writes results.</summary>
[SugarTable("em_commands")]
internal sealed class EmCommand
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [SugarColumn(Length = 32)]  public string ServerTag { get; set; } = "";
    [SugarColumn(Length = 32)]  public string Command   { get; set; } = "";
    [SugarColumn(Length = 256)] public string Args      { get; set; } = "";
    [SugarColumn(Length = 64)]  public string IssuedBy  { get; set; } = "";

    /// <summary>queued → done | failed</summary>
    [SugarColumn(Length = 16)] public string Status { get; set; } = "queued";

    [SugarColumn(Length = 256, IsNullable = true)] public string? Result { get; set; }

    public DateTime  CreatedAt  { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? ExecutedAt { get; set; }
}

/// <summary>Prepare lane: streamer requests. Site owns pending/approved/rejected; server owns the rest.</summary>
[SugarTable("em_requests")]
internal sealed class EmRequest
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [SugarColumn(Length = 32)]  public string ServerTag { get; set; } = "";
    [SugarColumn(Length = 64)]  public string EventId   { get; set; } = "";
    public ulong  WorkshopId { get; set; }
    [SugarColumn(Length = 128)] public string MapName   { get; set; } = "";

    [SugarColumn(Length = 32)]  public string RequestedBySteamId { get; set; } = "";
    [SugarColumn(Length = 64)]  public string RequestedByName    { get; set; } = "";
    [SugarColumn(Length = 512)] public string Note               { get; set; } = "";

    /// <summary>pending → approved → downloading → ready → applied → live → done (| rejected | failed)</summary>
    [SugarColumn(Length = 16)] public string Status { get; set; } = "pending";

    [SugarColumn(Length = 256, IsNullable = true)] public string? ServerNote { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
