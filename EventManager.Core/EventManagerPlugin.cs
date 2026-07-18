using System;
using System.IO;
using EventManager.Modules;
using EventManager.Plugins;
using EventManager.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared;

namespace EventManager;

/// <summary>
/// EventManager — centralized OPTIONAL gate for fun-mode plugins on an event server.
///
/// Lifecycle (honours ModSharp "all PostInits finish before any OAM" guarantee):
///   PostInit           — publish IEventManagerShared so event plugins can register in their OAM.
///   OnAllModulesLoaded — resolve optional external interfaces (Localizer, Menu, CommandCenter, Admin).
///
/// Ground state: no event active — the server runs vanilla until /events enables one.
/// </summary>
public sealed class EventManagerPlugin : IModSharpModule
{
    public string DisplayName   => "EventManager";
    public string DisplayAuthor => "yappershq";

    private readonly IServiceProvider               _provider;
    private readonly ILogger<EventManagerPlugin>    _logger;
    private readonly InterfaceBridge                _bridge;

    public EventManagerPlugin(
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
        _logger   = _provider.GetRequiredService<ILogger<EventManagerPlugin>>();
    }

    public bool Init()
    {
        foreach (var module in _provider.GetServices<IModule>())
            CallSafe(module, static m => { m.Init(); }, "Init");

        return true;
    }

    /// <summary>Publish the gate so event plugins can register in their OAM.</summary>
    public void PostInit()
    {
        var coordinator = _provider.GetRequiredService<EventCoordinator>();
        _bridge.SharpModuleManager
            .RegisterSharpModuleInterface<IEventManagerShared>(this, IEventManagerShared.Identity, coordinator);

        foreach (var module in _provider.GetServices<IModule>())
            CallSafe(module, static m => m.OnPostInit(), "PostInit");

        _logger.LogInformation("[EventManager] Published IEventManagerShared.");
    }

    public void OnAllModulesLoaded()
    {
        // Resolve optional localization/menus BEFORE modules' OAM runs so command handlers see them.
        _bridge.LocalizerManager = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<ILocalizerManager>(ILocalizerManager.Identity)?.Instance;
        _bridge.MenuManager = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<IMenuManager>(IMenuManager.Identity)?.Instance;
        LoadLocaleFiles();

        foreach (var module in _provider.GetServices<IModule>())
            CallSafe(module, static m => m.OnAllSharpModulesLoaded(), "OnAllModulesLoaded");

        _logger.LogInformation("[EventManager] Ready — ground state: no event active.");
    }

    public void Shutdown()
    {
        foreach (var module in _provider.GetServices<IModule>())
            CallSafe(module, static m => m.Shutdown(), "Shutdown");

        if (_provider is IDisposable disposable)
            disposable.Dispose();
    }

    private void LoadLocaleFiles()
    {
        if (_bridge.LocalizerManager is not { } lm)
        {
            _logger.LogInformation("[EventManager] ILocalizerManager not available — user-facing text will be silent.");
            return;
        }

        var localesPath = Path.Combine(_bridge.SharpPath, "locales");
        if (!Directory.Exists(localesPath)) return;

        foreach (var file in Directory.GetFiles(localesPath, "eventmanager*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            lm.LoadLocaleFile(fileName);
            _logger.LogInformation("[EventManager] Loaded locale file: {FileName}", fileName);
        }
    }

    private void CallSafe(IModule module, Action<IModule> action, string phase)
    {
        try
        {
            action(module);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EventManager] Error in {Phase} for {Module}", phase, module.GetType().Name);
        }
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
