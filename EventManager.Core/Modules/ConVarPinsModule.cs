using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using EventManager.Plugins;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace EventManager.Modules;

/// <summary>
/// Workshop-map convar protection. Custom/workshop maps ship cfgs and vscripts that set hostile
/// convars (<c>sv_cheats 1</c>, movement/gravity junk, …). This pins a configured allow-list of
/// convars to safe values and snaps any of them back the instant something changes it — via a
/// change hook (catches map-cfg execs, vscript sets, and anything that slips past CvarGuard's
/// point_servercommand block) plus a re-assert on every map load (after the gamemode cfg exec)
/// and a short delayed re-assert to beat delayed workshop execs.
///
/// This is EventManager-internal by design (no CvarGuard import — two copies of one defense drift).
/// Config: <c>configs/eventmanager.pins.jsonc</c> (ships as .example). Inert when absent.
/// </summary>
internal sealed class ConVarPinsModule : IModule, IGameListener
{
    private readonly ILogger<ConVarPinsModule> _logger;
    private readonly InterfaceBridge           _bridge;

    private readonly Dictionary<string, string>          _pins  = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IConVar, string>         _hooked = new();
    private IConVarManager.DelegateConVarChange?        _changeCallback;

    private bool _reasserting; // re-entrancy guard: our own SetString re-fires the change hook

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

        _changeCallback = OnPinnedConVarChanged;

        foreach (var (name, _) in _pins)
        {
            if (_bridge.ConVarManager.FindConVar(name) is not { } cvar)
            {
                _logger.LogWarning("[EventManager.Pins] Convar '{Name}' not found — pin skipped.", name);
                continue;
            }

            _bridge.ConVarManager.InstallChangeHook(cvar, _changeCallback);
            _hooked[cvar] = _pins[name];
        }

        _bridge.ModSharp.InstallGameListener(this);
        ReassertAll();

        _logger.LogInformation("[EventManager.Pins] Protecting {Count} convar(s).", _hooked.Count);
    }

    public void Shutdown()
    {
        if (_changeCallback is not null)
            foreach (var cvar in _hooked.Keys)
                _bridge.ConVarManager.RemoveChangeHook(cvar, _changeCallback);

        _hooked.Clear();

        if (_pins.Count > 0)
            _bridge.ModSharp.RemoveGameListener(this);
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

    /// <summary>A pinned convar changed — snap it back if something set it off-value.</summary>
    private void OnPinnedConVarChanged(IConVar conVar)
    {
        if (_reasserting) return; // our own re-assert; ignore
        if (!_hooked.TryGetValue(conVar, out var pinned)) return;

        if (!string.Equals(conVar.GetString(), pinned, StringComparison.Ordinal))
        {
            _reasserting = true;
            try { conVar.SetString(pinned); }
            finally { _reasserting = false; }

            _logger.LogInformation("[EventManager.Pins] '{Name}' forced back to '{Value}'.", conVar.Name, pinned);
        }
    }

    private void ReassertAll()
    {
        _reasserting = true;
        try
        {
            foreach (var (cvar, value) in _hooked)
                cvar.SetString(value);
        }
        finally { _reasserting = false; }
    }

    /// <summary>Map load: re-assert after the gamemode/workshop cfg exec, then again shortly after
    /// to beat delayed execs (workshop maps sometimes exec on a timer).</summary>
    void IGameListener.OnServerSpawn()
    {
        ReassertAll();
        _bridge.ModSharp.PushTimer(ReassertAll, 3.0, Sharp.Shared.Enums.GameTimerFlags.StopOnMapEnd);
    }
}
