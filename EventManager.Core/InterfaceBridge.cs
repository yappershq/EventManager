using Microsoft.Extensions.Logging;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Managers;

namespace EventManager;

/// <summary>
/// Single cached gateway to every ModSharp manager the Core plugin needs.
/// Built once in the plugin ctor; optional external modules resolved in OnAllModulesLoaded.
/// </summary>
internal sealed class InterfaceBridge
{
    internal string SharpPath { get; }

    internal IClientManager ClientManager { get; }
    internal IConVarManager ConVarManager { get; }

    internal IModSharp           ModSharp           { get; }
    internal ILoggerFactory      LoggerFactory      { get; }
    internal ISharpModuleManager SharpModuleManager { get; }
    internal IModSharpModule     Module             { get; }

    /// <summary>Optional; resolved in OnAllModulesLoaded, null when not installed.</summary>
    internal ILocalizerManager? LocalizerManager { get; set; }
    internal IMenuManager?      MenuManager      { get; set; }

    public InterfaceBridge(IModSharpModule module, ISharedSystem sharedSystem, string sharpPath, ILoggerFactory loggerFactory)
    {
        Module    = module;
        SharpPath = sharpPath;

        ClientManager = sharedSystem.GetClientManager();
        ConVarManager = sharedSystem.GetConVarManager();

        ModSharp           = sharedSystem.GetModSharp();
        LoggerFactory      = loggerFactory;
        SharpModuleManager = sharedSystem.GetSharpModuleManager();
    }
}
