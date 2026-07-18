using EventManager.Modules;
using EventManager.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace EventManager;

internal static class ModuleDependencyInjection
{
    public static IServiceCollection AddModules(this IServiceCollection services)
    {
        services.AddSingleton<EventCoordinator>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<EventCoordinator>());

        services.AddSingleton<StreamToolsModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<StreamToolsModule>());

        services.AddSingleton<MenuModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<MenuModule>());

        services.AddSingleton<CommandsModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<CommandsModule>());

        return services;
    }
}
