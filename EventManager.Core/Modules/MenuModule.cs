using System.Linq;
using EventManager.Plugins;
using EventManager.Shared;
using EventManager.Utils;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared.Objects;

namespace EventManager.Modules;

/// <summary>
/// The !events menu. Menus are built per open (event list + settings are dynamic); factory
/// items re-read live values so Refresh() shows toggles instantly.
/// </summary>
internal sealed class MenuModule : IModule
{
    private readonly InterfaceBridge   _bridge;
    private readonly EventCoordinator  _coordinator;
    private readonly StreamToolsModule _tools;

    public MenuModule(InterfaceBridge bridge, EventCoordinator coordinator, StreamToolsModule tools)
    {
        _bridge      = bridge;
        _coordinator = coordinator;
        _tools       = tools;
    }

    // ── IModule ────────────────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown() { }

    /// <summary>Opens the root menu. Returns false when MenuManager is unavailable.</summary>
    public bool OpenRoot(IGameClient client)
    {
        if (_bridge.MenuManager is not { } mm) return false;

        var lm = _bridge.LocalizerManager;

        var builder = Menu.Create().Title(Loc.Text(lm, client, "EventManager_Menu_Title"));

        // "Disable current" shortcut on top while an event runs (factory: reflects live state).
        builder = builder.Item(
            _ => _coordinator.ActiveEventId is { } id && _coordinator.Find(id) is { } active
                ? Loc.Text(lm, client, "EventManager_Menu_DisableCurrent", active.DisplayName)
                : "", // empty title → item skipped
            ctrl =>
            {
                var stopped = _coordinator.DeactivateCurrent();
                if (stopped is not null)
                    Loc.Chat(lm, ctrl.Client, "EventManager_Deactivated", stopped.DisplayName);
                ctrl.Refresh();
            });

        foreach (var mode in _coordinator.Registered)
        {
            var m = mode; // capture per item
            builder = builder.Item(
                _ => (_coordinator.IsActive(m.Id) ? "● " : "") + m.DisplayName,
                ctrl => ctrl.Next(BuildEventPage(ctrl.Client, m)));
        }

        builder = builder
            .Item(Loc.Text(lm, client, "EventManager_Menu_Tools"), ctrl => ctrl.Next(BuildToolsPage(ctrl.Client)))
            .ExitItem();

        mm.DisplayMenu(client, builder.Build());
        return true;
    }

    private Menu BuildEventPage(IGameClient client, IEventMode mode)
    {
        var lm = _bridge.LocalizerManager;

        var builder = Menu.Create().Title(mode.DisplayName);

        // Enable/Disable toggle (factory: reflects live state after Refresh).
        builder = builder.Item(
            _ => Loc.Text(lm, client, _coordinator.IsActive(mode.Id)
                ? "EventManager_Menu_Disable"
                : "EventManager_Menu_Enable"),
            ctrl =>
            {
                if (_coordinator.IsActive(mode.Id))
                {
                    _coordinator.DeactivateCurrent();
                    Loc.Chat(lm, ctrl.Client, "EventManager_Deactivated", mode.DisplayName);
                }
                else if (_coordinator.TryActivate(mode.Id, out _, out var reason))
                {
                    Loc.Chat(lm, ctrl.Client, "EventManager_Activated", mode.DisplayName);
                }
                else if (reason == "failed")
                {
                    Loc.Chat(lm, ctrl.Client, "EventManager_ActivateFailed", mode.Id);
                }

                ctrl.Refresh();
            });

        // Settings: Bool toggles + Choice cycles inline; other types are shown with a chat hint.
        foreach (var setting in mode.GetSettings())
        {
            var key = setting.Key; // capture

            switch (setting.Type)
            {
                case EventSettingType.Bool:
                    builder = builder.Item(
                        _ => FormatSetting(mode, key),
                        ctrl =>
                        {
                            // Contract says lowercase "true"/"false", but tolerate any casing/1/0.
                            var on = bool.TryParse(Current(mode, key), out var b)
                                ? b
                                : Current(mode, key) == "1";
                            mode.TrySetSetting(key, on ? "false" : "true");
                            ctrl.Refresh();
                        });
                    break;

                case EventSettingType.Choice:
                    builder = builder.Item(
                        _ => FormatSetting(mode, key),
                        ctrl =>
                        {
                            var s = mode.GetSettings().FirstOrDefault(x => x.Key == key);
                            if (s?.Choices is { Count: > 0 } choices)
                            {
                                var idx = 0;
                                for (var i = 0; i < choices.Count; i++)
                                    if (choices[i] == s.Value) { idx = i; break; }

                                mode.TrySetSetting(key, choices[(idx + 1) % choices.Count]);
                            }
                            ctrl.Refresh();
                        });
                    break;

                default:
                    builder = builder.DisabledItem(
                        $"{setting.DisplayName}: {setting.Value} " +
                        Loc.Text(lm, client, "EventManager_Menu_SettingHint", mode.Id, key));
                    break;
            }
        }

        return builder.BackItem().ExitItem().Build();
    }

    private Menu BuildToolsPage(IGameClient client)
    {
        var lm = _bridge.LocalizerManager;

        return Menu.Create()
            .Title(Loc.Text(lm, client, "EventManager_Menu_Tools"))
            .Item(
                _ => Loc.Text(lm, client, _tools.IntroActive
                    ? "EventManager_Menu_IntroOff"
                    : "EventManager_Menu_IntroOn"),
                ctrl =>
                {
                    if (_tools.IntroActive)
                    {
                        _tools.IntroOff();
                        Loc.Chat(lm, ctrl.Client, "EventManager_Intro_Off");
                    }
                    else if (_tools.IntroOn())
                    {
                        Loc.Chat(lm, ctrl.Client, "EventManager_Intro_On");
                    }

                    ctrl.Refresh();
                })
            .Item(Loc.Text(lm, client, "EventManager_Menu_RespawnAll"), ctrl =>
            {
                var n = _tools.RespawnAll();
                Loc.Chat(lm, ctrl.Client, "EventManager_RespawnAll", n);
                ctrl.Refresh();
            })
            .Item(Loc.Text(lm, client, "EventManager_Menu_Countdown"), ctrl =>
            {
                _tools.Countdown(5);
                ctrl.Exit();
            })
            .BackItem()
            .ExitItem()
            .Build();
    }

    private string Current(IEventMode mode, string key)
        => mode.GetSettings().FirstOrDefault(s => s.Key == key)?.Value ?? "";

    private string FormatSetting(IEventMode mode, string key)
    {
        var s = mode.GetSettings().FirstOrDefault(x => x.Key == key);
        return s is null ? "" : $"{s.DisplayName}: {s.Value}";
    }
}
