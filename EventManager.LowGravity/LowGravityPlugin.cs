using System;
using System.Collections.Generic;
using EventManager.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Managers;

namespace EventManager.LowGravity;

/// <summary>
/// Reference IEventMode implementation — the living integration example (see docs/INTEGRATION.md).
/// Low gravity: Activate captures sv_gravity and applies the configured value; Deactivate restores.
/// One Int setting ("gravity") demonstrates the settings contract, applied live while active.
///
/// As a pure demo it stays inert when EventManager is absent — a REAL gamemode would instead
/// activate itself standalone in that branch (that's the whole point of the optional gate).
/// </summary>
public sealed class LowGravityPlugin : IModSharpModule, IEventMode
{
    public string DisplayName   => "EventManager.LowGravity";
    public string DisplayAuthor => "yappershq";

    private readonly ILogger<LowGravityPlugin> _logger;
    private readonly ISharedSystem             _sharedSystem;
    private readonly IConVarManager            _conVars;

    private IDisposable? _registration;
    private string?      _revertGravity;   // captured pre-event sv_gravity; null = not active
    private int          _gravity = 200;

    public LowGravityPlugin(
        ISharedSystem   sharedSystem,
        string?         dllPath,
        string?         sharpPath,
        Version?        version,
        IConfiguration? coreConfiguration,
        bool            hotReload)
    {
        _sharedSystem = sharedSystem;
        _conVars      = sharedSystem.GetConVarManager();
        _logger       = sharedSystem.GetLoggerFactory().CreateLogger<LowGravityPlugin>();
    }

    public bool Init() => true;

    public void PostInit() { }

    public void OnAllModulesLoaded()
    {
        var gate = _sharedSystem.GetSharpModuleManager()
            .GetOptionalSharpModuleInterface<IEventManagerShared>(IEventManagerShared.Identity)?.Instance;

        if (gate is null)
        {
            // Real gamemodes activate themselves here (standalone server, no gate installed).
            _logger.LogInformation("[LowGravity] EventManager not present — demo stays inert.");
            return;
        }

        try
        {
            _registration = gate.RegisterEvent(this);
            _logger.LogInformation("[LowGravity] Registered with EventManager — dormant until activated.");
        }
        catch (ArgumentException ex)
        {
            // Duplicate Id — another plugin already registered "lowgravity".
            _logger.LogError(ex, "[LowGravity] Registration rejected.");
        }
    }

    public void Shutdown()
    {
        _registration?.Dispose(); // deactivates first if we are the active event
        _registration = null;
    }

    // ── IEventMode ─────────────────────────────────────────────────────────

    public string Id          => "lowgravity";
    string IEventMode.DisplayName => "Low Gravity";

    public bool RequiresRoundRestart => false; // gravity applies live

    public void Activate()
    {
        var cvar = _conVars.FindConVar("sv_gravity");
        if (cvar is null) throw new InvalidOperationException("sv_gravity not found");

        _revertGravity = cvar.GetString();
        cvar.SetString(_gravity.ToString());
    }

    public void Deactivate()
    {
        if (_revertGravity is null) return;

        _conVars.FindConVar("sv_gravity")?.SetString(_revertGravity);
        _revertGravity = null;
    }

    public IReadOnlyList<EventSetting> GetSettings() =>
    [
        new("gravity", "Gravity", EventSettingType.Int, _gravity.ToString()),
    ];

    public bool TrySetSetting(string key, string value)
    {
        if (!key.Equals("gravity", StringComparison.OrdinalIgnoreCase)) return false;
        if (!int.TryParse(value, out var g) || g is < 50 or > 1000) return false;

        _gravity = g;

        // Apply live while active.
        if (_revertGravity is not null)
            _conVars.FindConVar("sv_gravity")?.SetString(_gravity.ToString());

        return true;
    }
}
