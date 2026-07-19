using ChallengeEngine.Nav;
using ChallengeEngine.Plugins;
using ChallengeEngine.Session;
using Microsoft.Extensions.DependencyInjection;

namespace ChallengeEngine;

internal static class ModuleDependencyInjection
{
    public static IServiceCollection AddModules(this IServiceCollection services)
    {
        services.AddSingleton<LiveNavMesh>();

        services.AddSingleton<SessionEngine>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<SessionEngine>());

        return services;
    }
}
