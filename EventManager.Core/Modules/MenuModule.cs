using System.Globalization;
using System.Linq;
using EventManager.Plugins;
using EventManager.Shared;
using EventManager.Utils;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared.Objects;

namespace EventManager.Modules;

/// <summary>
/// The !events menu — AdminPanel-style nested navigation built on the full MenuManager surface:
/// lazy <c>SubMenu(titleFactory, menuFactory)</c> pages (always render live state), generator
/// items carrying per-item <c>Color</c> + selection <c>HintText</c>, <c>Spacer()</c> section
/// grouping, dynamic <c>DisabledItem</c> factories, and per-viewer localized <c>Title</c>
/// factories. Every item title/hint resolves against the RENDERING viewer (the delegate's
/// client parameter), never a captured opener.
/// </summary>
internal sealed class MenuModule : IModule
{
    private const string ColorActive  = "#7CFC00"; // active event rows / confirm
    private const string ColorDanger  = "#FF6B6B"; // disable / stop actions

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
            .Title(viewer => Loc.Text(lm, viewer, "EventManager_Menu_Title"))

            // Status line — colored when an event runs; click jumps to the relevant page.
            .Item((IGameClient viewer, ref MenuItemContext ctx) =>
            {
                if (_coordinator.ActiveEventId is { } id && _coordinator.Find(id) is { } active)
                {
                    ctx.Title    = Loc.Text(lm, viewer, "EventManager_Menu_Status_Active", active.DisplayName);
                    ctx.Color    = ColorActive;
                    ctx.HintText = Loc.Text(lm, viewer, "EventManager_Hint_Status_Active");
                    ctx.Action   = ctrl => ctrl.Next(c => BuildEventPage(c, active));
                }
                else
                {
                    ctx.Title    = Loc.Text(lm, viewer, "EventManager_Menu_Status_None");
                    ctx.HintText = Loc.Text(lm, viewer, "EventManager_Hint_Status_None");
                    ctx.Action   = ctrl => ctrl.Next(BuildEventsPage);
                }
            })

            .Spacer()
            .SubMenu(
                viewer => Loc.Text(lm, viewer, "EventManager_Menu_Events", _coordinator.Registered.Count),
                BuildEventsPage)
            .SubMenu(
                viewer => Loc.Text(lm, viewer, "EventManager_Menu_Tools"),
                BuildToolsPage)
            .ExitItem()
            .Build();

        mm.DisplayMenu(client, menu);
        return true;
    }

    // ── Events list ────────────────────────────────────────────────────────

    private Menu BuildEventsPage(IGameClient client)
    {
        var lm = _bridge.LocalizerManager;

        var builder = Menu.Create()
            .Title(viewer => Loc.Text(lm, viewer, "EventManager_Menu_Events_Title"));

        // Quick "disable current" on top while an event runs (Title unset → row skipped when vanilla).
        builder = builder.Item((IGameClient viewer, ref MenuItemContext ctx) =>
        {
            if (_coordinator.ActiveEventId is not { } id || _coordinator.Find(id) is not { } active)
                return;

            ctx.Title    = Loc.Text(lm, viewer, "EventManager_Menu_DisableCurrent", active.DisplayName);
            ctx.Color    = ColorDanger;
            ctx.HintText = Loc.Text(lm, viewer, "EventManager_Hint_Disable");
            ctx.Action   = ctrl =>
            {
                var stopped = _coordinator.DeactivateCurrent();
                if (stopped is not null)
                    Loc.Chat(lm, ctrl.Client, "EventManager_Deactivated", stopped.DisplayName);
                ctrl.Refresh();
            };
        });

        if (_coordinator.Registered.Count == 0)
            builder = builder.DisabledItem(viewer => Loc.Text(lm, viewer, "EventManager_Menu_NoEvents"));

        foreach (var mode in _coordinator.Registered)
        {
            var m = mode; // capture per item
            builder = builder.Item((IGameClient viewer, ref MenuItemContext ctx) =>
            {
                var active = _coordinator.IsActive(m.Id);
                ctx.Title    = (active ? "● " : "") + m.DisplayName;
                ctx.Color    = active ? ColorActive : null;
                ctx.HintText = active ? Loc.Text(lm, viewer, "EventManager_Hint_ActiveRow") : null;
                ctx.Action   = ctrl => ctrl.Next(c => BuildEventPage(c, m));
            });
        }

        return builder.BackItem().ExitItem().Build();
    }

    // ── One event ──────────────────────────────────────────────────────────

    private Menu BuildEventPage(IGameClient client, IEventMode mode)
    {
        var lm = _bridge.LocalizerManager;

        var builder = Menu.Create().Title(mode.DisplayName);

        // Enable / Disable — colored, hinted, with a confirm page when another event runs.
        builder = builder.Item((IGameClient viewer, ref MenuItemContext ctx) =>
        {
            if (_coordinator.IsActive(mode.Id))
            {
                ctx.Title    = Loc.Text(lm, viewer, "EventManager_Menu_Disable");
                ctx.Color    = ColorDanger;
                ctx.HintText = Loc.Text(lm, viewer, "EventManager_Hint_Disable");
                ctx.Action   = ctrl =>
                {
                    _coordinator.DeactivateCurrent();
                    Loc.Chat(lm, ctrl.Client, "EventManager_Deactivated", mode.DisplayName);
                    ctrl.Refresh();
                };
                return;
            }

            ctx.Title    = Loc.Text(lm, viewer, "EventManager_Menu_Enable");
            ctx.Color    = ColorActive;
            ctx.HintText = Loc.Text(lm, viewer, mode.RequiresRoundRestart
                ? "EventManager_Hint_EnableRestart"
                : "EventManager_Hint_Enable");
            ctx.Action   = ctrl =>
            {
                if (_coordinator.ActiveEventId is { } otherId && _coordinator.Find(otherId) is { } other)
                {
                    ctrl.Next(c => BuildConfirmSwitchPage(c, other, mode));
                    return;
                }

                Activate(ctrl, mode);
                ctrl.Refresh();
            };
        });

        var settings = mode.GetSettings();
        if (settings.Count > 0)
            builder = builder.Spacer();

        foreach (var setting in settings)
        {
            var key = setting.Key; // capture

            switch (setting.Type)
            {
                case EventSettingType.Bool:
                    builder = builder.Item((IGameClient viewer, ref MenuItemContext ctx) =>
                    {
                        ctx.Title    = SettingTitle(mode, key, boolAsState: true);
                        ctx.HintText = Loc.Text(lm, viewer, "EventManager_Hint_Toggle");
                        ctx.Action   = ctrl =>
                        {
                            var on = bool.TryParse(Current(mode, key), out var b)
                                ? b
                                : Current(mode, key) == "1";
                            mode.TrySetSetting(key, on ? "false" : "true");
                            ctrl.Refresh();
                        };
                    });
                    break;

                case EventSettingType.Choice:
                    builder = builder.Item((IGameClient viewer, ref MenuItemContext ctx) =>
                    {
                        ctx.Title    = SettingTitle(mode, key) + " »";
                        ctx.HintText = Loc.Text(lm, viewer, "EventManager_Hint_Pick");
                        ctx.Action   = ctrl => ctrl.Next(c => BuildChoicePage(c, mode, key));
                    });
                    break;

                case EventSettingType.Int:
                case EventSettingType.Float:
                    var numType = setting.Type; // capture
                    builder = builder.Item((IGameClient viewer, ref MenuItemContext ctx) =>
                    {
                        ctx.Title    = SettingTitle(mode, key) + " »";
                        ctx.HintText = Loc.Text(lm, viewer, "EventManager_Hint_Pick");
                        ctx.Action   = ctrl => ctrl.Next(c => BuildNumberPage(c, mode, key, numType));
                    });
                    break;

                default: // Text — edited via chat; hint carries the exact command
                    builder = builder.Item((IGameClient viewer, ref MenuItemContext ctx) =>
                    {
                        ctx.Title    = SettingTitle(mode, key);
                        ctx.State    = MenuItemState.Disabled;
                        ctx.HintText = Loc.Text(lm, viewer, "EventManager_Hint_TextSetting", mode.Id, key);
                    });
                    break;
            }
        }

        return builder.BackItem().ExitItem().Build();
    }

    private Menu BuildConfirmSwitchPage(IGameClient client, IEventMode from, IEventMode to)
    {
        var lm = _bridge.LocalizerManager;

        return Menu.Create()
            .Title(viewer => Loc.Text(lm, viewer, "EventManager_Menu_Confirm_Title"))
            .DisabledItem(viewer => Loc.Text(lm, viewer, "EventManager_Menu_Confirm_Line", from.DisplayName, to.DisplayName))
            .Spacer()
            .Item((IGameClient viewer, ref MenuItemContext ctx) =>
            {
                ctx.Title    = Loc.Text(lm, viewer, "EventManager_Menu_Confirm_Yes");
                ctx.Color    = ColorActive;
                ctx.HintText = Loc.Text(lm, viewer, to.RequiresRoundRestart
                    ? "EventManager_Hint_EnableRestart"
                    : "EventManager_Hint_Enable");
                ctx.Action   = ctrl =>
                {
                    Activate(ctrl, to);
                    ctrl.Exit();
                };
            })
            .BackItem(viewer => Loc.Text(lm, viewer, "EventManager_Menu_Confirm_No"))
            .ExitItem()
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
                builder = builder.Item((IGameClient viewer, ref MenuItemContext ctx) =>
                {
                    var current = Current(mode, key) == c;
                    ctx.Title  = (current ? "● " : "") + c;
                    ctx.Color  = current ? ColorActive : null;
                    ctx.Action = ctrl =>
                    {
                        mode.TrySetSetting(key, c);
                        ctrl.Refresh();
                    };
                });
            }
        }

        return builder.BackItem().ExitItem().Build();
    }

    private Menu BuildNumberPage(IGameClient client, IEventMode mode, string key, EventSettingType type)
    {
        var lm      = _bridge.LocalizerManager;
        var setting = mode.GetSettings().FirstOrDefault(s => s.Key == key);

        (double small, double big) = type == EventSettingType.Int ? (1d, 10d) : (0.5d, 5d);

        var builder = Menu.Create()
            .Title(setting?.DisplayName ?? key)
            .DisabledItem(viewer => Loc.Text(lm, viewer, "EventManager_Menu_Number_Current", Current(mode, key)))
            .Spacer();

        foreach (var delta in new[] { -big, -small, small, big })
        {
            var d = delta; // capture
            builder = builder.Item((IGameClient viewer, ref MenuItemContext ctx) =>
            {
                ctx.Title    = (d > 0 ? "+" : "−") + System.Math.Abs(d).ToString("0.##", CultureInfo.InvariantCulture);
                ctx.HintText = Loc.Text(lm, viewer, "EventManager_Hint_Adjust", mode.Id, key);
                ctx.Action   = ctrl =>
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
                };
            });
        }

        return builder.BackItem().ExitItem().Build();
    }

    // ── Stream tools ───────────────────────────────────────────────────────

    private Menu BuildToolsPage(IGameClient client)
    {
        var lm = _bridge.LocalizerManager;

        return Menu.Create()
            .Title(viewer => Loc.Text(lm, viewer, "EventManager_Menu_Tools"))
            .Item((IGameClient viewer, ref MenuItemContext ctx) =>
            {
                var on = _tools.IntroActive;
                ctx.Title    = Loc.Text(lm, viewer, on ? "EventManager_Menu_IntroOff" : "EventManager_Menu_IntroOn");
                ctx.Color    = on ? ColorActive : null;
                ctx.HintText = Loc.Text(lm, viewer, "EventManager_Hint_Intro");
                ctx.Action   = ctrl =>
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
                };
            })
            .Item((IGameClient viewer, ref MenuItemContext ctx) =>
            {
                ctx.Title    = Loc.Text(lm, viewer, "EventManager_Menu_RespawnAll");
                ctx.HintText = Loc.Text(lm, viewer, "EventManager_Hint_RespawnAll");
                ctx.Action   = ctrl =>
                {
                    var n = _tools.RespawnAll();
                    Loc.Chat(lm, ctrl.Client, "EventManager_RespawnAll", n);
                    ctrl.Refresh();
                };
            })
            .SubMenu(
                viewer => Loc.Text(lm, viewer, "EventManager_Menu_Countdown") + " »",
                BuildCountdownPage)
            .BackItem()
            .ExitItem()
            .Build();
    }

    private Menu BuildCountdownPage(IGameClient client)
    {
        var lm      = _bridge.LocalizerManager;
        var builder = Menu.Create()
            .Title(viewer => Loc.Text(lm, viewer, "EventManager_Menu_Countdown"));

        foreach (var secs in new[] { 3, 5, 10 })
        {
            var s = secs; // capture
            builder = builder.Item((IGameClient viewer, ref MenuItemContext ctx) =>
            {
                ctx.Title    = Loc.Text(lm, viewer, "EventManager_Menu_Countdown_Secs", s);
                ctx.HintText = Loc.Text(lm, viewer, "EventManager_Hint_Countdown");
                ctx.Action   = ctrl =>
                {
                    _tools.Countdown(s);
                    ctrl.Exit();
                };
            });
        }

        return builder.BackItem().ExitItem().Build();
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
