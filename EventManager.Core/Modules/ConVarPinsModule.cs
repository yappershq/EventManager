using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using EventManager.Plugins;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace EventManager.Modules;

/// <summary>
/// Workshop-map convar protection. Custom/workshop maps ship cfgs and vscripts that set hostile
/// convars (<c>sv_cheats 1</c>, movement/gravity junk, …). Two safe layers:
///   (1) PREVENT — a pre-hook on <c>CPointServerCommand::InputCommand</c> BLOCKS a map entity's
///       command outright when it targets a pinned convar (the same mechanism CvarGuard uses; the
///       event server ships no CvarGuard, so this fills that gap). The bad value never applies.
///   (2) RE-ASSERT — map-cfg execs bypass point_servercommand, so also force pinned values back at
///       map start + round start (safe listener contexts).
/// NEVER a convar change-hook (see note below).
///
/// NOTE (2026-07-19): an earlier version used a per-convar CHANGE HOOK to snap values back. That
/// crashed the Source engine natively — calling <c>SetString</c> from inside the engine's own
/// convar-change dispatch re-enters a non-re-entrant native path (a workshop map setting
/// <c>sv_airaccelerate</c> reliably killed the process seconds after boot). The change hook is
/// gone; the re-asserts below run between frames and are safe.
///
/// Defers to the ACTIVE event: convars the running event declares in its GameConVars are left
/// alone (e.g. LowGravity owns sv_gravity while active) — the manager already captures/restores
/// those, so pins must not fight them.
///
/// EventManager-internal by design (no CvarGuard import — two copies of one defense drift).
/// Config: <c>configs/eventmanager.pins.jsonc</c> (ships as .example). Inert when absent.
/// </summary>
internal sealed class ConVarPinsModule : IModule, IGameListener
{
    private readonly ILogger<ConVarPinsModule> _logger;
    private readonly InterfaceBridge           _bridge;
    private readonly EventCoordinator          _coordinator;

    private readonly Dictionary<string, string>        _pins     = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(IConVar Cvar, string Value)> _resolved = [];

    // Run AFTER gamemode/workshop cfg execs so our values are the last word (descending priority).
    int IGameListener.ListenerVersion  => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => -200;

    public ConVarPinsModule(ILogger<ConVarPinsModule> logger, InterfaceBridge bridge, EventCoordinator coordinator)
    {
        _logger      = logger;
        _bridge      = bridge;
        _coordinator = coordinator;
    }

    // ── IModule ────────────────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
    {
        LoadPins();
        if (_pins.Count == 0)
        {
            _logger.LogInformation("[EventManager.Pins] No pins configured — convar protection inert.");
            return;
        }

        foreach (var (name, value) in _pins)
        {
            if (_bridge.ConVarManager.FindConVar(name) is not { } cvar)
            {
                _logger.LogWarning("[EventManager.Pins] Convar '{Name}' not found — pin skipped.", name);
                continue;
            }

            _resolved.Add((cvar, value));
        }

        _bridge.ModSharp.InstallGameListener(this);
        _bridge.HookManager.PointServerCommand.InstallHookPre(OnPointCommand);
        ReassertAll();

        _logger.LogInformation("[EventManager.Pins] Protecting {Count} convar(s) (blocked at point_servercommand + re-asserted at map/round start).",
            _resolved.Count);
    }

    public void Shutdown()
    {
        if (_pins.Count > 0)
        {
            _bridge.ModSharp.RemoveGameListener(this);
            _bridge.HookManager.PointServerCommand.RemoveHookPre(OnPointCommand);
        }

        _resolved.Clear();
    }

    // ── Config ─────────────────────────────────────────────────────────────

    private void LoadPins()
    {
        var path = Path.Combine(_bridge.SharpPath, "configs", "eventmanager.pins.jsonc");
        if (!File.Exists(path)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling     = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (!doc.RootElement.TryGetProperty("Pins", out var pins)) return;

            foreach (var pin in pins.EnumerateObject())
                _pins[pin.Name] = pin.Value.ValueKind == JsonValueKind.String
                    ? pin.Value.GetString() ?? ""
                    : pin.Value.GetRawText();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EventManager.Pins] Failed to parse eventmanager.pins.jsonc.");
        }
    }

    // ── Enforcement (safe callback contexts only) ──────────────────────────

    /// <summary>Force each pinned convar back to its value, unless the active event owns it.</summary>
    private void ReassertAll()
    {
        // The running event's declared convars win — pins must not fight LowGravity's sv_gravity etc.
        var owned = _coordinator.ActiveEventId is { } id && _coordinator.Find(id) is { } mode
            ? mode.GameConVars
            : null;

        foreach (var (cvar, value) in _resolved)
        {
            if (owned is not null && owned.ContainsKey(cvar.Name)) continue;
            if (!string.Equals(cvar.GetString(), value, StringComparison.Ordinal))
                cvar.SetString(value);
        }
    }

    // Map start: re-assert immediately (after the map/gamemode cfg exec) + a delayed pass.
    void IGameListener.OnServerSpawn()
    {
        ReassertAll();
        _bridge.ModSharp.PushTimer(ReassertAll, 3.0, GameTimerFlags.StopOnMapEnd); // beat delayed execs
    }

    // Round start: cfgs/vscripts commonly re-set convars here.
    void IGameListener.OnRoundRestarted() => ReassertAll();

    // ── point_servercommand block (prevention) ─────────────────────────────

    /// <summary>
    /// Block a map entity's server command when its first token is a pinned convar (unless the
    /// active event owns it). Stops <c>sv_cheats 1</c> &amp; friends before they apply — no fight,
    /// no re-assert needed for this path.
    /// </summary>
    private HookReturnValue<EmptyHookReturn> OnPointCommand(
        IPointServerCommandHookParams param,
        HookReturnValue<EmptyHookReturn> result)
    {
        var cmd = param.Command;
        if (string.IsNullOrWhiteSpace(cmd)) return result;

        // First whitespace-delimited token = the convar/command name.
        var span = cmd.AsSpan().TrimStart();
        var end  = span.IndexOfAny(' ', '\t');
        var name = (end < 0 ? span : span[..end]).ToString();

        if (!_pins.ContainsKey(name)) return result;

        var owned = _coordinator.ActiveEventId is { } id && _coordinator.Find(id) is { } mode
            ? mode.GameConVars
            : null;
        if (owned is not null && owned.ContainsKey(name)) return result; // active event owns it

        _logger.LogInformation("[EventManager.Pins] BLOCKED point_servercommand touching pinned '{Name}': {Cmd}", name, cmd);
        return new HookReturnValue<EmptyHookReturn>(EHookAction.SkipCallReturnOverride, default);
    }
}
