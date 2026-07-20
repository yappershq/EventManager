using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace ChallengeEngine.Persistence;

/// <summary>One player's running totals, passed as an immutable snapshot from the game thread so the
/// async DB writes never touch the live ledger.</summary>
internal readonly record struct ScoreRow(ulong SteamId, string Name, int Points, int RoundWins);

/// <summary>
/// ce_* persistence on the shared PlayerAnalytics MySQL (its own tiny pool). All writes are
/// fire-and-forget from the game thread (caller passes a value snapshot). Reads (resume) run in the
/// background and are applied back on the game thread by the caller. Degrades to a no-op (persistence
/// off) if no DB config is present. Never exposes an ORM type outside Core.
/// </summary>
internal sealed class ChallengeStore(ILogger<ChallengeStore> logger, InterfaceBridge bridge)
{
    private SqlSugarScope? _db;
    private string _tag = "event";

    public bool Ready => _db is not null;

    public bool Init()
    {
        var cfg = LoadDbConfig("challengeengine.database.jsonc") ?? LoadDbConfig("playeranalytics.database.jsonc");
        if (cfg is null)
        {
            logger.LogInformation("[ChallengeEngine.DB] No database config — persistence off (session still runs in memory).");
            return false;
        }

        try
        {
            // Shared fleet MySQL — keep our pool tiny (a few writes per heat); EM's web bridge has its own.
            var conn = $"Server={cfg.Host};Port={cfg.Port};Database={cfg.Database};" +
                       $"User={cfg.User};Password={cfg.Password};Maximum Pool Size=2;";
            _db = new SqlSugarScope(new ConnectionConfig
            {
                DbType                = DbType.MySql,
                ConnectionString      = conn,
                IsAutoCloseConnection = true,
                InitKeyType           = InitKeyType.Attribute,
            });
            _db.CodeFirst.InitTables(typeof(CeSession), typeof(CeScore), typeof(CeLeaderboard));
            _tag = cfg.ServerTag;
            logger.LogInformation("[ChallengeEngine.DB] persistence ready.");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ChallengeEngine.DB] init failed — persistence off.");
            _db = null;
            return false;
        }
    }

    /// <summary>Open a new session row, superseding any stale unfinished one. Returns its id (0 = off).</summary>
    public async Task<int> BeginSessionAsync(string challenge, DateTime endsAtUtc)
    {
        if (_db is null) return 0;
        var now = DateTime.UtcNow;

        await _db.Ado.ExecuteCommandAsync(
            "UPDATE ce_session SET Status='Interrupted', UpdatedAt=@u WHERE ServerTag=@t AND Status='Active'",
            new { u = now, t = _tag });

        return await _db.Insertable(new CeSession
        {
            ServerTag = _tag, Challenge = challenge, Status = "Active",
            StartedAt = now, EndsAt = endsAtUtc, UpdatedAt = now,
        }).ExecuteReturnIdentityAsync();
    }

    /// <summary>Persist per-round standings + the session's phase/round.</summary>
    public async Task SaveRoundAsync(int sessionId, int phase, int round, IReadOnlyList<ScoreRow> totals)
    {
        if (_db is null || sessionId == 0) return;
        var now = DateTime.UtcNow;

        await _db.Updateable<CeSession>()
            .SetColumns(x => new CeSession { Phase = phase, Round = round, UpdatedAt = now })
            .Where(x => x.Id == sessionId).ExecuteCommandAsync();

        await UpsertScoresAsync(sessionId, totals, now);
    }

    /// <summary>The most recent unfinished session's totals — for boot-to-lobby resume. Null if none.</summary>
    public async Task<(int id, List<ScoreRow> totals)?> LoadUnfinishedAsync()
    {
        if (_db is null) return null;

        var s = await _db.Queryable<CeSession>()
            .Where(x => x.ServerTag == _tag && x.Status == "Active")
            .OrderByDescending(x => x.Id).FirstAsync();
        if (s is null) return null;

        var scores = await _db.Queryable<CeScore>().Where(x => x.SessionId == s.Id).ToListAsync();
        var totals = scores.Select(x => new ScoreRow((ulong)x.SteamId, x.Name, x.Points, x.RoundWins)).ToList();
        return (s.Id, totals);
    }

    /// <summary>Crown the champion + fold this session into the season leaderboard, exactly once.</summary>
    public async Task CrownAsync(int sessionId, ulong? champion, IReadOnlyList<ScoreRow> totals)
    {
        if (_db is null || sessionId == 0) return;
        var now = DateTime.UtcNow;

        // Atomic status flip — only the FIRST crown applies the season delta (resume/retry-safe).
        var flipped = await _db.Ado.ExecuteCommandAsync(
            "UPDATE ce_session SET Status='Crowned', Champion=@c, UpdatedAt=@u WHERE Id=@id AND Status<>'Crowned'",
            new { c = champion.HasValue ? (long)champion.Value : (long?)null, u = now, id = sessionId });
        if (flipped != 1) return; // already crowned → don't double-count

        await UpsertScoresAsync(sessionId, totals, now); // ensure final scores are on record

        foreach (var t in totals)
        {
            // Atomic per-player add — never read-modify-write.
            await _db.Ado.ExecuteCommandAsync(
                "INSERT INTO ce_leaderboard (SteamId, Name, TotalPts, Sessions, Wins, UpdatedAt) " +
                "VALUES (@s,@n,@p,1,@w,@u) " +
                "ON DUPLICATE KEY UPDATE TotalPts=TotalPts+@p, Sessions=Sessions+1, Wins=Wins+@w, Name=@n, UpdatedAt=@u",
                new { s = (long)t.SteamId, n = t.Name, p = t.Points, w = t.SteamId == champion ? 1 : 0, u = now });
        }
    }

    private async Task UpsertScoresAsync(int sessionId, IReadOnlyList<ScoreRow> totals, DateTime now)
    {
        if (_db is null || totals.Count == 0) return;
        var rows = totals.Select(t => new CeScore
        {
            SessionId = sessionId, SteamId = (long)t.SteamId, Name = t.Name,
            Points = t.Points, RoundWins = t.RoundWins, UpdatedAt = now,
        }).ToList();
        await _db.Storageable(rows).ExecuteCommandAsync();
    }

    // ── config ────────────────────────────────────────────────────────────

    private sealed record DbConfig(string Host, int Port, string Database, string User, string Password, string ServerTag);

    private DbConfig? LoadDbConfig(string fileName)
    {
        var path = Path.Combine(bridge.SharpPath, "configs", fileName);
        if (!File.Exists(path)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
            });
            var db = doc.RootElement.GetProperty("Database");
            if (!string.Equals(db.GetProperty("Type").GetString(), "mysql", StringComparison.OrdinalIgnoreCase))
                return null;

            var tag = doc.RootElement.TryGetProperty("ServerTag", out var t) ? t.GetString() ?? "event" : "event";
            return new DbConfig(
                db.GetProperty("Host").GetString() ?? "localhost",
                db.TryGetProperty("Port", out var p) ? p.GetInt32() : 3306,
                db.GetProperty("Database").GetString() ?? "",
                db.GetProperty("User").GetString() ?? "",
                db.GetProperty("Password").GetString() ?? "",
                tag);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[ChallengeEngine.DB] Failed to parse {File}.", fileName);
            return null;
        }
    }
}
