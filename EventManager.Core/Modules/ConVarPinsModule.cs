using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using EventManager.Plugins;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;

namespace EventManager.Modules;

/// <summary>
/// Workshop-map convar protection. Custom/workshop maps ship cfgs and vscripts that set hostile
/// convars (<c>sv_cheats 1</c>, movement/gravity junk, …) — they exec at MAP LOAD. This pins a
/// configured allow-list of convars to safe values and re-asserts them on every map load, at a
/// low listener priority so it runs AFTER the map/gamemode cfg exec (the values it re-asserts
/// win), plus a short delayed re-assert to beat delayed workshop execs.
///
/// NOTE (2026-07-19): an earlier version installed a per-convar CHANGE HOOK to snap values back
/// at runtime. That crashed the Source engine natively — calling <c>SetString</c> from inside the
/// engine's own convar-change dispatch re-enters a non-re-entrant native path (a workshop map
/// setting <c>sv_airaccelerate</c> reliably killed the process a few seconds after boot). The
/// change hook is GONE; map-load re-assert covers the real threat (map cfgs run at load), and
/// runtime point_servercommand is already blocked by CvarGuard.
///
/// EventManager-internal by design (no CvarGuard import — two copies of one defense drift).
/// Config: <c>configs/eventmanager.pins.jsonc</c> (ships as .example). Inert when absent.
/// </summary>
internal sealed class ConVarPinsModule : IModule, IGameListener
{
    private readonly ILogger<ConVarPinsModule> _logger;
    private readonly InterfaceBridge           _bridge;

    private readonly Dictionary<string, string> _pins   = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(IConVar Cvar, string Value)> _resolved = [];

    // Run AFTER gamemode/workshop cfg execs so our values are the last word (descending priority).
    int IGameListener.ListenerVersion  => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => -200;

    public ConVarPinsModule(ILogger<ConVarPinsModule> logger, InterfaceBridge bridge)
    {
        _logger = logger;
        _bridge = bridge;
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
        ReassertAll();

        _logger.LogInformation("[EventManager.Pins] Protecting {Count} convar(s) (re-asserted at map load).",
            _resolved.Count);
    }

    public void Shutdown()
    {
        if (_pins.Count > 0)
            _bridge.ModSharp.RemoveGameListener(this);

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

    // ── Enforcement ────────────────────────────────────────────────────────

    private void ReassertAll()
    {
        foreach (var (cvar, value) in _resolved)
            if (!string.Equals(cvar.GetString(), value, StringComparison.Ordinal))
                cvar.SetString(value);
    }

    /// <summary>Map load: re-assert after the gamemode/workshop cfg exec, then again shortly after
    /// to beat delayed execs (workshop maps sometimes exec on a timer).</summary>
    void IGameListener.OnServerSpawn()
    {
        ReassertAll();
        _bridge.ModSharp.PushTimer(ReassertAll, 3.0, Sharp.Shared.Enums.GameTimerFlags.StopOnMapEnd);
    }
}
