using Microsoft.Extensions.Logging;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Managers;

namespace ChallengeEngine;

/// <summary>Single cached gateway to the ModSharp managers the engine needs.</summary>
internal sealed class InterfaceBridge
{
    internal string SharpPath { get; }

    internal IEntityManager EntityManager { get; }
    internal IClientManager ClientManager { get; }

    internal IModSharp           ModSharp           { get; }
    internal ILoggerFactory      LoggerFactory      { get; }
    internal ISharpModuleManager SharpModuleManager { get; }
    internal IModSharpModule     Module             { get; }

    internal ILocalizerManager? LocalizerManager { get; set; }

    public InterfaceBridge(IModSharpModule module, ISharedSystem sharedSystem, string sharpPath, ILoggerFactory loggerFactory)
    {
        Module    = module;
        SharpPath = sharpPath;

        EntityManager = sharedSystem.GetEntityManager();
        ClientManager = sharedSystem.GetClientManager();

        ModSharp            = sharedSystem.GetModSharp();
        LoggerFactory       = loggerFactory;
        SharpModuleManager  = sharedSystem.GetSharpModuleManager();
    }
}
