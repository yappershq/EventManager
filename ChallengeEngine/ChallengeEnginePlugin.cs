using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChallengeEngine.Plugins;
using ChallengeEngine.Session;
using EventManager.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Shared;

namespace ChallengeEngine;

/// <summary>
/// ChallengeEngine — a satellite gamemode that registers as the "Challenge Night" event on the
/// EventManager gate. Hosts round-based scored challenges (IChallenge) over a 30 min–3 h session with
/// a points ledger, escalation, finale, and a crowned champion.
///
/// Lifecycle mirrors the house DI pattern (ServiceCollection + IModule). Registers with the gate in
/// OnAllModulesLoaded; the gate drives it via IEventMode.Activate/Deactivate + settings/actions.
/// </summary>
public sealed class ChallengeEnginePlugin : IModSharpModule, IEventMode
{
    public string DisplayName   => "ChallengeEngine";
    public string DisplayAuthor => "yappershq";

    private readonly IServiceProvider               _provider;
    private readonly ILogger<ChallengeEnginePlugin> _logger;
    private readonly InterfaceBridge                _bridge;
    private readonly SessionEngine                  _session;

    private IDisposable? _registration;

    // Operator-tunable session settings (via IEventMode.GetSettings/TrySetSetting).
    private string _challengeId   = "null";
    private int    _durationMin   = 60;
    private int    _escalationMin = 15;
    private int    _finaleSize    = 6;
    private bool   _autostart     = true;

    /// <summary>Convars the coordinator captures/restores so an engine-timed heat isn't cut short by
    /// CS2's native round/win system. The MANAGER owns these — never set them imperatively.</summary>
    private static readonly Dictionary<string, string> Neutralize = new()
    {
        ["mp_ignore_round_win_conditions"] = "1",
        ["mp_roundtime"]         = "60",
        ["mp_roundtime_defuse"]  = "60",
        ["mp_roundtime_hostage"] = "60",
        ["mp_freezetime"]        = "0",
        ["mp_maxrounds"]         = "0",
        ["mp_timelimit"]         = "0",
        ["mp_team_intro_time"]   = "0",
        ["mp_warmup_pausetimer"] = "1",
    };

    public ChallengeEnginePlugin(
        ISharedSystem   sharedSystem,
        string?         dllPath,
        string?         sharpPath,
        Version?        version,
        IConfiguration? coreConfiguration,
        bool            hotReload)
    {
        ArgumentNullException.ThrowIfNull(sharedSystem);
        ArgumentNullException.ThrowIfNull(sharpPath);

        var loggerFactory = sharedSystem.GetLoggerFactory();
        _bridge = new InterfaceBridge(this, sharedSystem, sharpPath, loggerFactory);

        var services = new ServiceCollection();
        services.AddSingleton(sharedSystem);
        services.AddSingleton(_bridge);
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(LoggerFactoryLogger<>));
        services.AddModules();

        _provider = services.BuildServiceProvider();
        _logger   = _provider.GetRequiredService<ILogger<ChallengeEnginePlugin>>();
        _session  = _provider.GetRequiredService<SessionEngine>();
    }

    public bool Init()
    {
        foreach (var m in _provider.GetServices<IModule>()) CallSafe(m, static x => { x.Init(); }, "Init");
        return true;
    }

    public void PostInit()
    {
        foreach (var m in _provider.GetServices<IModule>()) CallSafe(m, static x => x.OnPostInit(), "PostInit");
    }

    public void OnAllModulesLoaded()
    {
        _bridge.LocalizerManager = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<ILocalizerManager>(ILocalizerManager.Identity)?.Instance;
        LoadLocaleFiles();

        foreach (var m in _provider.GetServices<IModule>()) CallSafe(m, static x => x.OnAllSharpModulesLoaded(), "OnAllModulesLoaded");

        // Register with the EventManager gate. ChallengeEngine has no standalone purpose (its whole
        // control surface IS the gate), so if the gate is absent we simply stay dormant.
        var gate = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<IEventManagerShared>(IEventManagerShared.Identity)?.Instance;
        if (gate is null)
        {
            _logger.LogWarning("[ChallengeEngine] EventManager gate not present — Challenge Night unavailable.");
            return;
        }

        try
        {
            _registration = gate.RegisterEvent(this);
            _logger.LogInformation("[ChallengeEngine] Registered 'Challenge Night' with EventManager.");
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "[ChallengeEngine] Registration rejected (duplicate id?).");
        }
    }

    public void Shutdown()
    {
        _registration?.Dispose(); // deactivates first if we are the active event
        _registration = null;

        foreach (var m in _provider.GetServices<IModule>()) CallSafe(m, static x => x.Shutdown(), "Shutdown");
        if (_provider is IDisposable d) d.Dispose();
    }

    // ── IEventMode ─────────────────────────────────────────────────────────

    public string Id => "challengenight";
    string IEventMode.DisplayName => "Challenge Night";

    public bool RequiresRoundRestart => true;

    public void Activate()   => _session.StartSession(_challengeId, _durationMin, _escalationMin, _finaleSize, _autostart);
    public void Deactivate() => _session.EndSession(crowned: false);

    public IReadOnlyDictionary<string, string> GameConVars => Neutralize;

    public IReadOnlyList<EventSetting> GetSettings()
    {
        var challengeIds = _session.Challenges.Select(c => c.Id).ToArray();
        return
        [
            new("challenge",      "Challenge",      EventSettingType.Choice, _challengeId, challengeIds),
            new("duration_min",   "Duration (min)", EventSettingType.Int,    _durationMin.ToString()),
            new("escalation_min", "Escalate every", EventSettingType.Int,    _escalationMin.ToString()),
            new("finale_size",    "Finale size",    EventSettingType.Int,    _finaleSize.ToString()),
            new("autostart",      "Auto-start",     EventSettingType.Bool,   _autostart ? "true" : "false"),
        ];
    }

    public bool TrySetSetting(string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "challenge":
                if (_session.Challenges.All(c => !c.Id.Equals(value, StringComparison.OrdinalIgnoreCase))) return false;
                _challengeId = value; return true;
            case "duration_min":
                if (!int.TryParse(value, out var d) || d is < 30 or > 180) return false;
                _durationMin = d; return true;
            case "escalation_min":
                if (!int.TryParse(value, out var e) || e is < 5 or > 60) return false;
                _escalationMin = e; return true;
            case "finale_size":
                if (!int.TryParse(value, out var f) || f is < 2 or > 10) return false;
                _finaleSize = f; return true;
            case "autostart":
                if (!bool.TryParse(value, out var a)) return false;
                _autostart = a; return true;
            default:
                return false;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void LoadLocaleFiles()
    {
        if (_bridge.LocalizerManager is not { } lm) return;

        var localesPath = Path.Combine(_bridge.SharpPath, "locales");
        if (!Directory.Exists(localesPath)) return;

        foreach (var file in Directory.GetFiles(localesPath, "challengeengine*.json"))
            lm.LoadLocaleFile(Path.GetFileNameWithoutExtension(file));
    }

    private void CallSafe(IModule module, Action<IModule> action, string phase)
    {
        try { action(module); }
        catch (Exception ex) { _logger.LogError(ex, "[ChallengeEngine] Error in {Phase} for {Module}", phase, module.GetType().Name); }
    }
}

/// <summary>Generic logger adapter bridging ILogger&lt;T&gt; onto ModSharp's factory.</summary>
internal sealed class LoggerFactoryLogger<T>(ILoggerFactory factory) : ILogger<T>
{
    private readonly ILogger _inner = factory.CreateLogger(typeof(T).Name);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);
}
