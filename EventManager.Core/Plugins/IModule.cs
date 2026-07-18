namespace EventManager.Plugins;

/// <summary>Internal module lifecycle, driven by <see cref="EventManagerPlugin"/> in DI order.</summary>
internal interface IModule
{
    bool Init();

    void OnPostInit();

    void OnAllSharpModulesLoaded();

    void Shutdown();
}
