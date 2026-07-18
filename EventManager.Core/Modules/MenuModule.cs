using System.Globalization;
using System.Linq;
using EventManager.Plugins;
using EventManager.Shared;
using EventManager.Utils;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared.Objects;

namespace EventManager.Modules;

/// <summary>
/// The !events menu — AdminPanel-style nested navigation: status-aware root, an Events page,
/// per-event pages with picker submenus for settings (choices get their own page with a ●
/// marker; numbers get an adjuster page), a confirm page when switching away from a running
/// event, and a Stream Tools page. Menus are built lazily per navigation (ctrl.Next(factory))
/// so every page always renders live state; factory item titles keep toggles fresh on Refresh().
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

    // ── Root ───────────────────────────────────────────────────────────────

    /// <summary>Opens the root menu. Returns false when MenuManager is unavailable.</summary>
    public bool OpenRoot(IGameClient client)
    {
        if (_bridge.MenuManager is not { } mm) return false;

        var lm = _bridge.LocalizerManager;

        var menu = Menu.Create()
            .Title(Loc.Text(lm, client, "EventManager_Menu_Title"))

            // Status line — click jumps to the active event's page (or the list when vanilla).
            .Item(
                _ => _coordinator.ActiveEventId is { } id && _coordinator.Find(id) is { } active
                    ? Loc.Text(lm, client, "EventManager_Menu_Status_Active", active.DisplayName)
                    : Loc.Text(lm, client, "EventManager_Menu_Status_None"),
                ctrl =>
                {
                    if (_coordinator.ActiveEventId is { } id && _coordinator.Find(id) is { } active)
                        ctrl.Next(c => BuildEventPage(c, active));
                    else
                        ctrl.Next(BuildEventsPage);
                })

            .Item(
                _ => Loc.Text(lm, client, "EventManager_Menu_Events", _coordinator.Registered.Count),
                ctrl => ctrl.Next(BuildEventsPage))

            .Item(
                Loc.Text(lm, client, "EventManager_Menu_Tools"),
                ctrl => ctrl.Next(BuildToolsPage))

            .ExitItem("Exit")
            .Build();

        mm.DisplayMenu(client, menu);
        return true;
    }

    // ── Events list ────────────────────────────────────────────────────────

    private Menu BuildEventsPage(IGameClient client)
    {
        var lm = _bridge.LocalizerManager;

        var builder = Menu.Create().Title(Loc.Text(lm, client, "EventManager_Menu_Events_Title"));

        // Quick "disable current" on top while an event runs. Generator item: leaving Title
        // unset skips the row entirely when the server is vanilla.
        builder = builder.Item((IGameClient viewer, ref MenuItemContext ctx) =>
        {
            if (_coordinator.ActiveEventId is not { } id || _coordinator.Find(id) is not { } active)
                return;

            ctx.Title  = Loc.Text(lm, viewer, "EventManager_Menu_DisableCurrent", active.DisplayName);
            ctx.Action = ctrl =>
            {
                var stopped = _coordinator.DeactivateCurrent();
                if (stopped is not null)
                    Loc.Chat(lm, ctrl.Client, "EventManager_Deactivated", stopped.DisplayName);
                ctrl.Refresh();
            };
        });

        if (_coordinator.Registered.Count == 0)
            builder = builder.DisabledItem(Loc.Text(lm, client, "EventManager_Menu_NoEvents"));

        foreach (var mode in _coordinator.Registered)
        {
            var m = mode; // capture per item
            builder = builder.Item(
                _ => (_coordinator.IsActive(m.Id) ? "● " : "") + m.DisplayName,
                ctrl => ctrl.Next(c => BuildEventPage(c, m)));
        }

        return builder.BackItem("« Back").ExitItem("Exit").Build();
    }

    // ── One event ──────────────────────────────────────────────────────────

    private Menu BuildEventPage(IGameClient client, IEventMode mode)
    {
        var lm = _bridge.LocalizerManager;

        var builder = Menu.Create().Title(mode.DisplayName);

        // Enable / Disable — with a confirm page when another event is running.
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
                    ctrl.Refresh();
                    return;
                }

                if (_coordinator.ActiveEventId is { } otherId && _coordinator.Find(otherId) is { } other)
                {
                    ctrl.Next(c => BuildConfirmSwitchPage(c, other, mode));
                    return;
                }

                Activate(ctrl, mode);
                ctrl.Refresh();
            });

        foreach (var setting in mode.GetSettings())
        {
            var key = setting.Key; // capture

            switch (setting.Type)
            {
                case EventSettingType.Bool:
                    builder = builder.Item(
                        _ => SettingTitle(mode, key, boolAsState: true),
                        ctrl =>
                        {
                            var on = bool.TryParse(Current(mode, key), out var b)
                                ? b
                                : Current(mode, key) == "1";
                            mode.TrySetSetting(key, on ? "false" : "true");
                            ctrl.Refresh();
                        });
                    break;

                case EventSettingType.Choice:
                    builder = builder.Item(
                        _ => SettingTitle(mode, key) + " »",
                        ctrl => ctrl.Next(c => BuildChoicePage(c, mode, key)));
                    break;

                case EventSettingType.Int:
                case EventSettingType.Float:
                    builder = builder.Item(
                        _ => SettingTitle(mode, key) + " »",
                        ctrl => ctrl.Next(c => BuildNumberPage(c, mode, key, setting.Type)));
                    break;

                default: // Text — edited via chat, shown here for discoverability
                    builder = builder.DisabledItem(
                        SettingTitle(mode, key) + " " +
                        Loc.Text(lm, client, "EventManager_Menu_SettingHint", mode.Id, key));
                    break;
            }
        }

        return builder.BackItem("« Back").ExitItem("Exit").Build();
    }

    private Menu BuildConfirmSwitchPage(IGameClient client, IEventMode from, IEventMode to)
    {
        var lm = _bridge.LocalizerManager;

        return Menu.Create()
            .Title(Loc.Text(lm, client, "EventManager_Menu_Confirm_Title"))
            .DisabledItem(Loc.Text(lm, client, "EventManager_Menu_Confirm_Line", from.DisplayName, to.DisplayName))
            .Item(Loc.Text(lm, client, "EventManager_Menu_Confirm_Yes"), ctrl =>
            {
                Activate(ctrl, to);
                ctrl.Exit();
            })
            .BackItem(Loc.Text(lm, client, "EventManager_Menu_Confirm_No"))
            .ExitItem("Exit")
            .Build();
    }

    // ── Setting pickers ────────────────────────────────────────────────────

    private Menu BuildChoicePage(IGameClient client, IEventMode mode, string key)
    {
        var setting = mode.GetSettings().FirstOrDefault(s => s.Key == key);
        var builder = Menu.Create().Title(setting?.DisplayName ?? key);

        if (setting?.Choices is { Count: > 0 } choices)
        {
            foreach (var choice in choices)
            {
                var c = choice; // capture
                builder = builder.Item(
                    _ => (Current(mode, key) == c ? "● " : "") + c,
                    ctrl =>
                    {
                        mode.TrySetSetting(key, c);
                        ctrl.Refresh();
                    });
            }
        }

        return builder.BackItem("« Back").ExitItem("Exit").Build();
    }

    private Menu BuildNumberPage(IGameClient client, IEventMode mode, string key, EventSettingType type)
    {
        var lm      = _bridge.LocalizerManager;
        var setting = mode.GetSettings().FirstOrDefault(s => s.Key == key);

        (double small, double big) = type == EventSettingType.Int ? (1d, 10d) : (0.5d, 5d);

        var builder = Menu.Create()
            .Title(setting?.DisplayName ?? key)
            .Item(_ => Loc.Text(lm, client, "EventManager_Menu_Number_Current", Current(mode, key)),
                  ctrl => ctrl.Refresh());

        foreach (var delta in new[] { -big, -small, small, big })
        {
            var d = delta; // capture
            builder = builder.Item(
                (d > 0 ? "+" : "−") + System.Math.Abs(d).ToString("0.##", CultureInfo.InvariantCulture),
                ctrl =>
                {
                    if (double.TryParse(Current(mode, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var cur))
                    {
                        var next = cur + d;
                        var text = type == EventSettingType.Int
                            ? ((int)System.Math.Round(next)).ToString(CultureInfo.InvariantCulture)
                            : next.ToString("0.##", CultureInfo.InvariantCulture);

                        if (!mode.TrySetSetting(key, text))
                            Loc.Chat(lm, ctrl.Client, "EventManager_SettingRejected", mode.Id, key, text);
                    }

                    ctrl.Refresh();
                });
        }

        return builder
            .DisabledItem(Loc.Text(lm, client, "EventManager_Menu_SettingHint", mode.Id, key))
            .BackItem("« Back")
            .ExitItem("Exit")
            .Build();
    }

    // ── Stream tools ───────────────────────────────────────────────────────

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
            .Item(Loc.Text(lm, client, "EventManager_Menu_Countdown") + " »",
                  ctrl => ctrl.Next(BuildCountdownPage))
            .BackItem("« Back")
            .ExitItem("Exit")
            .Build();
    }

    private Menu BuildCountdownPage(IGameClient client)
    {
        var lm      = _bridge.LocalizerManager;
        var builder = Menu.Create().Title(Loc.Text(lm, client, "EventManager_Menu_Countdown"));

        foreach (var secs in new[] { 3, 5, 10 })
        {
            var s = secs; // capture
            builder = builder.Item(
                Loc.Text(lm, client, "EventManager_Menu_Countdown_Secs", s),
                ctrl =>
                {
                    _tools.Countdown(s);
                    ctrl.Exit();
                });
        }

        return builder.BackItem("« Back").ExitItem("Exit").Build();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void Activate(IMenuController ctrl, IEventMode mode)
    {
        var lm = _bridge.LocalizerManager;

        if (_coordinator.TryActivate(mode.Id, out _, out var reason))
            Loc.Chat(lm, ctrl.Client, "EventManager_Activated", mode.DisplayName);
        else if (reason == "failed")
            Loc.Chat(lm, ctrl.Client, "EventManager_ActivateFailed", mode.Id);
    }

    private string Current(IEventMode mode, string key)
        => mode.GetSettings().FirstOrDefault(s => s.Key == key)?.Value ?? "";

    private string SettingTitle(IEventMode mode, string key, bool boolAsState = false)
    {
        var s = mode.GetSettings().FirstOrDefault(x => x.Key == key);
        if (s is null) return "";

        var value = boolAsState
            ? (bool.TryParse(s.Value, out var b) && b) || s.Value == "1" ? "ON" : "OFF"
            : s.Value;

        return $"{s.DisplayName}: {value}";
    }
}
