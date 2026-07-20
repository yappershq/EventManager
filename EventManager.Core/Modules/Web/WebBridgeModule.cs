using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EventManager.Plugins;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using SqlSugar;

namespace EventManager.Modules.Web;

/// <summary>
/// The cs-tema bridge — three DB lanes over the shared MySQL (PlayerAnalytics DB for now):
///   observe  — em_state heartbeat (~10s) + em_catalog (settings schema, on change),
///   execute  — em_commands poll (~2s), executed on the game thread via the SAME coordinator
///              paths as the in-game menu, result written back,
///   prepare  — em_requests poll (~30s), workshop staging via ISteamApi.DownloadItem
///              (background, no map change), Apply = map change + auto-arm on the new map.
/// Inert without a database config: tries configs/eventmanager.database.jsonc, falls back to
/// configs/playeranalytics.database.jsonc (zero-new-config bootstrap; move to its own DB later).
/// All DB I/O happens on worker tasks; all game work is marshalled via InvokeFrameAction.
/// </summary>
internal sealed class WebBridgeModule : IModule, IGameListener, ISteamListener
{
    private readonly ILogger<WebBridgeModule> _logger;
    private readonly InterfaceBridge          _bridge;
    private readonly EventCoordinator         _coordinator;
    private readonly StreamToolsModule        _tools;

    private ISqlSugarClient? _db;
    private string           _tag = "event";
    private CancellationTokenSource? _cts;
    private bool _listenersInstalled;

    private string? _pendingArmEventId;                       // set by apply, consumed on map spawn
    private bool    _pendingFireStart;                        // true = also Start() after arming (scheduled)
    private readonly Dictionary<string, string> _lastCatalogJson = new();
    private readonly object _stagingLock = new();
    private readonly List<(int RequestId, ulong WorkshopId)> _staging = [];

    /// <summary>Game-thread-safe cache of ready requests (menus must never block on DB).</summary>
    private volatile IReadOnlyList<EmRequest> _readyCache = [];

    int IGameListener.ListenerVersion   => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority  => -50; // after gamemode config syncs, before nothing that matters
    int ISteamListener.ListenerVersion  => ISteamListener.ApiVersion;
    int ISteamListener.ListenerPriority => 0;

    public WebBridgeModule(
        ILogger<WebBridgeModule> logger,
        InterfaceBridge          bridge,
        EventCoordinator         coordinator,
        StreamToolsModule        tools)
    {
        _logger      = logger;
        _bridge      = bridge;
        _coordinator = coordinator;
        _tools       = tools;
    }

    // ── IModule ────────────────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
    {
        var config = LoadDbConfig("eventmanager.database.jsonc") ?? LoadDbConfig("playeranalytics.database.jsonc");
        if (config is null)
        {
            _logger.LogInformation("[EventManager.Web] No database config — web bridge inert.");
            return;
        }

        try
        {
            // Shared fleet MySQL: keep our pool tiny (house rule: Max Pool Size ≤ 4).
            var conn = $"Server={config.Host};Port={config.Port};Database={config.Database};" +
                       $"User={config.User};Password={config.Password};Maximum Pool Size=4;";
            _db = new SqlSugarScope(new ConnectionConfig
            {
                DbType                = DbType.MySql,
                ConnectionString      = conn,
                IsAutoCloseConnection = true,
                InitKeyType           = InitKeyType.Attribute,
            });
            _db.CodeFirst.InitTables(typeof(EmState), typeof(EmCatalog), typeof(EmCommand), typeof(EmRequest), typeof(EmSchedule));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EventManager.Web] Database init failed — web bridge inert.");
            _db = null;
            return;
        }

        _tag = config.ServerTag;

        _bridge.ModSharp.InstallGameListener(this);
        _bridge.ModSharp.InstallSteamListener(this);
        _listenersInstalled = true;

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop("heartbeat", TimeSpan.FromSeconds(10), HeartbeatTickAsync, _cts.Token));
        _ = Task.Run(() => Loop("commands", TimeSpan.FromSeconds(2), CommandTickAsync, _cts.Token));
        _ = Task.Run(() => Loop("requests", TimeSpan.FromSeconds(30), RequestTickAsync, _cts.Token));
        _ = Task.Run(() => Loop("schedules", TimeSpan.FromSeconds(15), ScheduleTickAsync, _cts.Token));

        _logger.LogInformation("[EventManager.Web] Bridge active — tag '{Tag}', db '{Db}'.", _tag, config.Database);
    }

    public void Shutdown()
    {
        _cts?.Cancel();

        if (_listenersInstalled)
        {
            _bridge.ModSharp.RemoveGameListener(this);
            _bridge.ModSharp.RemoveSteamListener(this);
            _listenersInstalled = false;
        }
    }

    // ── Config ─────────────────────────────────────────────────────────────

    private sealed record DbConfig(string Host, int Port, string Database, string User, string Password, string ServerTag);

    private DbConfig? LoadDbConfig(string fileName)
    {
        var path = Path.Combine(_bridge.SharpPath, "configs", fileName);
        if (!File.Exists(path)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling     = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var db = doc.RootElement.GetProperty("Database");
            if (!string.Equals(db.GetProperty("Type").GetString(), "mysql", StringComparison.OrdinalIgnoreCase))
                return null; // only MySQL is meaningful for a SHARED web bridge

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
            _logger.LogWarning(ex, "[EventManager.Web] Failed to parse {File}.", fileName);
            return null;
        }
    }

    // ── Worker plumbing ────────────────────────────────────────────────────

    private async Task Loop(string name, TimeSpan interval, Func<Task> tick, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await tick(); }
            catch (Exception ex) { _logger.LogError(ex, "[EventManager.Web] {Loop} tick failed.", name); }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Run work on the game thread and await its result from a worker task.</summary>
    private Task<T> OnGameThread<T>(Func<T> fn)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            try { tcs.TrySetResult(fn()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    // ── Observe lane ───────────────────────────────────────────────────────

    private sealed record StateSnapshot(
        string Map, int Players, string PlayersJson, string? ActiveId, string? ArmedId,
        string StartMode, bool Intro, string? LiveJson,
        List<(string Id, string Name, string SettingsJson, string ConVarsJson, string ActionsJson, string? MapsJson)> Catalog);

    private async Task HeartbeatTickAsync()
    {
        if (_db is null) return;

        var snap = await OnGameThread(CaptureSnapshot);
        var now  = DateTime.UtcNow;

        string? downloadsJson = null;
        lock (_stagingLock)
        {
            if (_staging.Count > 0)
                downloadsJson = JsonSerializer.Serialize(_staging.Select(s => new { requestId = s.RequestId, workshopId = s.WorkshopId }));
        }

        await _db.Storageable(new EmState
        {
            ServerTag     = _tag,
            HeartbeatAt   = now,
            Map           = snap.Map,
            PlayerCount   = snap.Players,
            ActiveEventId = snap.ActiveId,
            ArmedEventId  = snap.ArmedId,
            StartMode     = snap.StartMode,
            IntroActive   = snap.Intro,
            DownloadsJson = downloadsJson,
            PlayersJson   = snap.PlayersJson,
            LiveJson      = snap.LiveJson,
            UpdatedAt     = now,
        }).ExecuteCommandAsync();

        foreach (var (id, name, settingsJson, conVarsJson, actionsJson, mapsJson) in snap.Catalog)
        {
            var payload = settingsJson + "|" + conVarsJson + "|" + actionsJson + "|" + mapsJson + "|" + name;
            if (_lastCatalogJson.TryGetValue(id, out var prev) && prev == payload) continue;

            _lastCatalogJson[id] = payload;
            await _db.Storageable(new EmCatalog
            {
                Id                = $"{_tag}:{id}",
                ServerTag         = _tag,
                EventId           = id,
                DisplayName       = name,
                SettingsJson      = settingsJson,
                ConVarsJson       = conVarsJson,
                ActionsJson       = actionsJson,
                SupportedMapsJson = mapsJson,
                Registered        = true,
                UpdatedAt         = now,
            }).ExecuteCommandAsync();
        }

        await UpdateRequestLifecyclesAsync(snap.ActiveId);
    }

    private StateSnapshot CaptureSnapshot()
    {
        // Roles + live gameplay state from the active event (site marks seekers/solo + shows a live board).
        var activeMode = _coordinator.ActiveEventId is { } activeId ? _coordinator.Find(activeId) : null;
        var roles      = activeMode?.GetActivePlayerRoles();
        var liveJson   = activeMode?.GetLiveStateJson();

        var players = new List<object>();
        foreach (var c in _bridge.ClientManager.GetGameClients(inGame: true))
        {
            if (c.IsFakeClient) continue;
            var sid = (ulong)c.SteamId;
            var role = roles is not null && roles.TryGetValue(sid, out var r) ? r : null;
            players.Add(new { slot = (int)(byte)c.Slot, steamId = sid.ToString(), name = c.Name, role });
        }

        var catalog = new List<(string, string, string, string, string, string?)>();
        foreach (var mode in _coordinator.Registered)
        {
            var settings = mode.GetSettings().Select(s => new
            {
                key = s.Key, displayName = s.DisplayName, type = s.Type.ToString(),
                value = s.Value, choices = s.Choices,
            });
            var actions = mode.GetActions().Select(a => new
            {
                key = a.Key, displayName = a.DisplayName, arg = a.Arg.ToString(),
            });
            var maps = mode.SupportedMaps;
            catalog.Add((mode.Id, mode.DisplayName,
                JsonSerializer.Serialize(settings),
                JsonSerializer.Serialize(mode.GameConVars),
                JsonSerializer.Serialize(actions),
                maps is { Count: > 0 } ? JsonSerializer.Serialize(maps) : null));
        }

        return new StateSnapshot(
            _bridge.ModSharp.GetMapName() ?? "unknown",
            players.Count,
            JsonSerializer.Serialize(players),
            _coordinator.ActiveEventId,
            _coordinator.ArmedEventId,
            _coordinator.StartMode.ToString(),
            _tools.IntroActive,
            liveJson,
            catalog);
    }

    /// <summary>applied → live when its event is running; live → done when it stops.</summary>
    private async Task UpdateRequestLifecyclesAsync(string? activeId)
    {
        if (_db is null) return;

        var open = await _db.Queryable<EmRequest>()
            .Where(r => r.ServerTag == _tag && (r.Status == "applied" || r.Status == "live"))
            .ToListAsync();

        foreach (var r in open)
        {
            var newStatus = r.Status switch
            {
                "applied" when string.Equals(activeId, r.EventId, StringComparison.OrdinalIgnoreCase) => "live",
                "live" when !string.Equals(activeId, r.EventId, StringComparison.OrdinalIgnoreCase)   => "done",
                _ => null,
            };
            if (newStatus is null) continue;

            r.Status    = newStatus;
            r.UpdatedAt = DateTime.UtcNow;
            await _db.Updateable(r).ExecuteCommandAsync();
        }
    }

    // ── Execute lane ───────────────────────────────────────────────────────

    private async Task CommandTickAsync()
    {
        if (_db is null) return;

        var queued = await _db.Queryable<EmCommand>()
            .Where(c => c.ServerTag == _tag && c.Status == "queued")
            .OrderBy(c => c.Id)
            .Take(8)
            .ToListAsync();

        foreach (var cmd in queued)
        {
            string result;
            bool   ok;
            try
            {
                (ok, result) = await OnGameThread(() => Execute(cmd.Command, cmd.Args));
            }
            catch (Exception ex)
            {
                (ok, result) = (false, ex.Message);
            }

            cmd.Status     = ok ? "done" : "failed";
            cmd.Result     = result.Length > 250 ? result[..250] : result;
            cmd.ExecutedAt = DateTime.UtcNow;
            await _db.Updateable(cmd).ExecuteCommandAsync();

            _logger.LogInformation("[EventManager.Web] cmd #{Id} '{Cmd} {Args}' by {Who} → {Status} ({Result})",
                cmd.Id, cmd.Command, cmd.Args, cmd.IssuedBy, cmd.Status, cmd.Result);
        }
    }

    /// <summary>Game-thread command executor — same verbs and paths as the in-game menu/console.</summary>
    private (bool Ok, string Result) Execute(string command, string args)
    {
        var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        switch (command.ToLowerInvariant())
        {
            case "on":
                if (a.Length < 1) return (false, "usage: on <id>");
                var r = _coordinator.TryActivate(a[0], out var mode);
                return r is ActivateResult.Started or ActivateResult.Armed
                    ? (true, $"{r}:{mode?.Id}")
                    : (false, r.ToString());

            case "start":
                var sr = _coordinator.Start(out var started);
                return sr == ActivateResult.Started ? (true, $"started:{started?.Id}") : (false, sr.ToString());

            case "off":
                if (_coordinator.Disarm() is { } disarmed) return (true, $"disarmed:{disarmed.Id}");
                var stopped = _coordinator.DeactivateCurrent();
                return (true, stopped is null ? "nothing-active" : $"deactivated:{stopped.Id}");

            case "startmode":
                if (a.Length < 1 || a[0].ToLowerInvariant() is not ("warmup" or "direct"))
                    return (false, "usage: startmode warmup|direct");
                _coordinator.StartMode = a[0].ToLowerInvariant() == "warmup" ? StartMode.Warmup : StartMode.Direct;
                return (true, _coordinator.StartMode.ToString());

            case "set":
                if (a.Length < 3) return (false, "usage: set <id> <key> <value>");
                var target = _coordinator.Find(a[0]);
                if (target is null) return (false, "unknown-event");
                var value = string.Join(' ', a[2..]);
                return target.TrySetSetting(a[1], value)
                    ? (true, $"{a[0]}.{a[1]}={value}")
                    : (false, "rejected");

            case "intro":
                var on = a.Length >= 1 && a[0].ToLowerInvariant() is "on" or "1" or "true";
                return (on ? _tools.IntroOn() : _tools.IntroOff(), on ? "intro-on" : "intro-off");

            case "respawnall":
                return (true, $"respawned:{_tools.RespawnAll()}");

            case "countdown":
                var secs = a.Length >= 1 && int.TryParse(a[0], out var s) ? s : 5;
                _tools.Countdown(secs);
                return (true, $"countdown:{secs}");

            case "apply":
                if (a.Length < 1 || !int.TryParse(a[0], out var reqId)) return (false, "usage: apply <requestId>");
                return ApplyRequest(reqId);

            case "action":
                if (a.Length < 2) return (false, "usage: action <eventId> <key> [arg]");
                var actMode = _coordinator.Find(a[0]);
                if (actMode is null) return (false, "unknown-event");
                var actArg = a.Length >= 3 ? string.Join(' ', a[2..]) : "";
                return actMode.TryInvokeAction(a[1], actArg)
                    ? (true, $"action:{a[0]}.{a[1]}")
                    : (false, "action-rejected");

            default:
                return (false, $"unknown-command:{command}");
        }
    }

    // ── Schedule lane (unattended timed fire) ──────────────────────────────

    private async Task ScheduleTickAsync()
    {
        if (_db is null) return;

        var nowUtc = DateTime.UtcNow;
        var due = await _db.Queryable<EmSchedule>()
            .Where(x => x.ServerTag == _tag && x.Status == "pending" && x.ScheduledAt <= nowUtc)
            .OrderBy(x => x.ScheduledAt)
            .ToListAsync();

        foreach (var sch in due)
        {
            var (ok, result) = await OnGameThread(() => FireSchedule(sch));
            sch.Status  = ok ? "fired" : "failed";
            sch.Result  = result.Length > 250 ? result[..250] : result;
            sch.FiredAt = DateTime.UtcNow;
            await _db.Updateable(sch).ExecuteCommandAsync();

            _logger.LogInformation("[EventManager.Web] schedule #{Id} '{Event}' → {Status} ({Result})",
                sch.Id, sch.EventId, sch.Status, sch.Result);
        }
    }

    /// <summary>Game thread: fire a scheduled event — map change if needed, then activate + start.</summary>
    private (bool Ok, string Result) FireSchedule(EmSchedule sch)
    {
        if (_coordinator.Find(sch.EventId) is null) return (false, "event-not-registered");

        var wantMap = !string.IsNullOrEmpty(sch.MapName) || sch.WorkshopId != 0;
        var curMap  = _bridge.ModSharp.GetMapName() ?? "";
        var onWantedMap = !wantMap
            || string.Equals(curMap, sch.MapName, StringComparison.OrdinalIgnoreCase);

        _coordinator.StartMode = sch.StartMode.Equals("Warmup", StringComparison.OrdinalIgnoreCase)
            ? StartMode.Warmup : StartMode.Direct;

        if (wantMap && !onWantedMap)
        {
            // Change map; OnServerSpawn activates + (for a schedule) starts.
            _pendingArmEventId = sch.EventId;
            _pendingFireStart  = true;
            _bridge.ModSharp.ServerCommand(sch.WorkshopId != 0
                ? $"host_workshop_map {sch.WorkshopId.ToString(CultureInfo.InvariantCulture)}"
                : $"changelevel {sch.MapName}");
            return (true, $"map-change:{(sch.WorkshopId != 0 ? sch.WorkshopId.ToString() : sch.MapName)}");
        }

        // Already on the right map (or map-agnostic): activate now, and start if it only armed.
        var r = _coordinator.TryActivate(sch.EventId, out _);
        if (r == ActivateResult.Armed)
            _coordinator.Start(out _);

        return r is ActivateResult.Started or ActivateResult.Armed
            ? (true, r.ToString())
            : (false, r.ToString());
    }

    // ── Prepare lane (requests + workshop staging) ─────────────────────────

    private async Task RequestTickAsync()
    {
        if (_db is null) return;

        var approved = await _db.Queryable<EmRequest>()
            .Where(x => x.ServerTag == _tag && x.Status == "approved")
            .ToListAsync();

        foreach (var req in approved)
        {
            var started = await OnGameThread(() => StageWorkshopItem(req.Id, req.WorkshopId));
            req.Status     = started ? "downloading" : "failed";
            req.ServerNote = started ? null : "workshop download rejected (steam not connected / bad id?)";
            req.UpdatedAt  = DateTime.UtcNow;
            await _db.Updateable(req).ExecuteCommandAsync();
        }

        _readyCache = await _db.Queryable<EmRequest>()
            .Where(x => x.ServerTag == _tag && x.Status == "ready")
            .OrderBy(x => x.Id)
            .Take(10)
            .ToListAsync();
    }

    /// <summary>Game thread: start (or short-circuit) the background workshop download.</summary>
    private bool StageWorkshopItem(int requestId, ulong workshopId)
    {
        if (workshopId == 0)
        {
            // Stock map request — nothing to download.
            MarkRequestReady(requestId);
            return true;
        }

        var state = _bridge.SteamApi.GetItemState(workshopId);
        if ((state & WorkshopItemState.ItemStateInstalled) != 0 || _bridge.ModSharp.WorkshopMapExists(workshopId))
        {
            MarkRequestReady(requestId);
            return true;
        }

        lock (_stagingLock)
            if (!_staging.Any(x => x.RequestId == requestId))
                _staging.Add((requestId, workshopId));

        // Can return false before Steam connects — OnSteamServersConnected retries.
        return _bridge.SteamApi.DownloadItem(workshopId, highPriority: true) || true;
    }

    private void MarkRequestReady(int requestId)
    {
        lock (_stagingLock)
            _staging.RemoveAll(x => x.RequestId == requestId);

        _ = Task.Run(async () =>
        {
            if (_db is null) return;
            try
            {
                await _db.Updateable<EmRequest>()
                    .SetColumns(x => new EmRequest { Status = "ready", UpdatedAt = DateTime.UtcNow })
                    .Where(x => x.Id == requestId && (x.Status == "approved" || x.Status == "downloading"))
                    .ExecuteCommandAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EventManager.Web] Failed to mark request {Id} ready.", requestId);
            }
        });
    }

    /// <summary>Ready requests for the in-game Queue page — cached, never blocks the game thread.</summary>
    public IReadOnlyList<EmRequest> GetReadyRequests() => _readyCache;

    public bool IsActive => _db is not null;

    /// <summary>Apply a ready request: change to its map and arm its event on the new map.</summary>
    public (bool Ok, string Result) ApplyRequest(int requestId)
    {
        if (_db is null) return (false, "bridge-inert");

        var req = _readyCache.FirstOrDefault(x => x.Id == requestId);
        if (req is null) return (false, "request-not-ready");
        if (_coordinator.Find(req.EventId) is null) return (false, "event-not-registered");

        _pendingArmEventId = req.EventId;

        _ = Task.Run(async () =>
        {
            try
            {
                await _db.Updateable<EmRequest>()
                    .SetColumns(x => new EmRequest { Status = "applied", UpdatedAt = DateTime.UtcNow })
                    .Where(x => x.Id == requestId)
                    .ExecuteCommandAsync();
            }
            catch { /* status catches up via lifecycle pass */ }
        });

        _readyCache = _readyCache.Where(x => x.Id != requestId).ToList();

        _bridge.ModSharp.ServerCommand(req.WorkshopId != 0
            ? $"host_workshop_map {req.WorkshopId.ToString(CultureInfo.InvariantCulture)}"
            : $"changelevel {req.MapName}");

        return (true, $"applying:{req.EventId}@{(req.WorkshopId != 0 ? req.WorkshopId.ToString() : req.MapName)}");
    }

    // ── Listeners ──────────────────────────────────────────────────────────

    /// <summary>New map up: arm the pending event (Warmup mode → armed lobby, operator/site starts).</summary>
    void IGameListener.OnServerSpawn()
    {
        if (_pendingArmEventId is not { } id) return;

        _pendingArmEventId = null;
        var fireStart = _pendingFireStart;
        _pendingFireStart = false;

        var prev = _coordinator.StartMode;
        _coordinator.StartMode = StartMode.Warmup; // arm first (works for both apply and scheduled fire)
        var result = _coordinator.TryActivate(id, out _);

        // A scheduled fire runs unattended — end warmup into round 1 straight away.
        if (fireStart && result == ActivateResult.Armed)
            _coordinator.Start(out _);

        _coordinator.StartMode = prev;

        _logger.LogInformation("[EventManager.Web] Post-map {Kind} '{Id}': {Result}.",
            fireStart ? "fire" : "arm", id, result);
    }

    void ISteamListener.OnItemInstalled(ulong publishedFileId)
    {
        List<(int RequestId, ulong WorkshopId)> hits;
        lock (_stagingLock)
            hits = _staging.Where(x => x.WorkshopId == publishedFileId).ToList();

        foreach (var hit in hits)
        {
            _logger.LogInformation("[EventManager.Web] Workshop {Id} installed — request {Req} ready.",
                publishedFileId, hit.RequestId);
            MarkRequestReady(hit.RequestId);
        }
    }

    void ISteamListener.OnSteamServersConnected()
    {
        List<(int RequestId, ulong WorkshopId)> pending;
        lock (_stagingLock)
            pending = _staging.ToList();

        foreach (var p in pending)
            _bridge.SteamApi.DownloadItem(p.WorkshopId, highPriority: true);
    }
}
