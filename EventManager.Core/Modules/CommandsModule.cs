using System.Collections.Generic;
using System.Linq;
using AdminPanel.Shared;
using EventManager.Plugins;
using EventManager.Utils;
using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Modules.CommandCenter.Shared;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace EventManager.Modules;

/// <summary>
/// Registers the /events command via CommandCenter (bare name, no css_ prefix).
///
///   !events                              — open the menu
///   !events list                         — list registered events (● active)
///   !events on &lt;id&gt; | off              — activate / deactivate
///   !events set &lt;id&gt; &lt;key&gt; &lt;value&gt;  — apply an event setting
///   !events intro on|off | respawnall | countdown [secs]
///
/// Everything is admin-gated behind "eventmanager:admin" (server console is trusted).
/// </summary>
internal sealed class CommandsModule : IModule
{
    private const string AdminFlag           = "eventmanager:admin";
    private const string AdminPanelActionId  = "eventmanager.events";

    private readonly ILogger<CommandsModule> _logger;
    private readonly InterfaceBridge         _bridge;
    private readonly EventCoordinator        _coordinator;
    private readonly StreamToolsModule       _tools;
    private readonly MenuModule              _menu;

    private IAdminManager?     _adminManager;
    private IAdminPanelShared? _adminPanel;

    public CommandsModule(
        ILogger<CommandsModule> logger,
        InterfaceBridge         bridge,
        EventCoordinator        coordinator,
        StreamToolsModule       tools,
        MenuModule              menu)
    {
        _logger      = logger;
        _bridge      = bridge;
        _coordinator = coordinator;
        _tools       = tools;
        _menu        = menu;
    }

    // ── IModule ────────────────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
    {
        _adminManager = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<IAdminManager>(IAdminManager.Identity)?.Instance;

        if (_adminManager is not null)
        {
            // Register the permission so wildcard admins ("*") resolve it correctly.
            _adminManager.MountAdminManifest("EventManager", () => new AdminTableManifest(
                PermissionCollection: new Dictionary<string, HashSet<string>>
                {
                    [AdminFlag] = [],
                },
                Roles:  [],
                Admins: []
            ));
        }
        else
        {
            _logger.LogWarning("[EventManager] AdminManager not available — client commands will be denied.");
        }

        var cc = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<ICommandCenter>(ICommandCenter.Identity)?.Instance;

        if (cc is null)
        {
            _logger.LogWarning("[EventManager] CommandCenter not available — commands disabled.");
            return;
        }

        var reg = cc.GetRegistry("eventmanager");
        reg.RegisterClientCommand("events", OnEvents);
        reg.RegisterServerCommand("events", OnEventsServer,
            "Event gate: events list | on <id> | off | set <id> <key> <value> | intro on|off | respawnall | countdown [secs]");

        RegisterAdminPanel();
    }

    public void Shutdown()
    {
        try { _adminPanel?.Unregister(AdminPanelActionId); }
        catch (System.Exception ex) { _logger.LogError(ex, "[EventManager] AdminPanel unregister failed."); }
    }

    // ── AdminPanel integration (!admin → Fun → Events → toggle) ────────────

    private void RegisterAdminPanel()
    {
        _adminPanel = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<IAdminPanelShared>(IAdminPanelShared.Identity)?.Instance;
        if (_adminPanel is null) return; // AdminPanel not installed — chat/console commands still work.

        try
        {
            _adminPanel.RegisterGlobalAction(new AdminPanelGlobalAction
            {
                Id         = AdminPanelActionId,
                Label      = "Events",
                Category   = "Fun",
                Permission = AdminFlag,
                SortOrder  = 40,
                SubMenu    = _ => BuildAdminPanelItems(),
            });
            _logger.LogInformation("[EventManager] AdminPanel integration registered.");
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "[EventManager] AdminPanel register failed.");
        }
    }

    private IReadOnlyList<AdminPanelMenuItem> BuildAdminPanelItems()
    {
        var items = new List<AdminPanelMenuItem>();

        if (_coordinator.ArmedEventId is { } armedId && _coordinator.Find(armedId) is { } armed)
        {
            items.Add(new AdminPanelMenuItem
            {
                Label      = $"▶ Start: {armed.DisplayName}",
                OnSelected = slot => _coordinator.Start(out _),
            });
        }

        if (_coordinator.ActiveEventId is { } activeId && _coordinator.Find(activeId) is { } active)
        {
            items.Add(new AdminPanelMenuItem
            {
                Label      = $"Disable: {active.DisplayName}",
                OnSelected = _ => _coordinator.DeactivateCurrent(),
            });
        }

        foreach (var mode in _coordinator.Registered)
        {
            var m = mode; // capture per item
            var isArmed = string.Equals(_coordinator.ArmedEventId, m.Id, StringComparison.OrdinalIgnoreCase);
            items.Add(new AdminPanelMenuItem
            {
                Label      = (_coordinator.IsActive(m.Id) ? "● " : isArmed ? "▶ " : "") + m.DisplayName,
                OnSelected = slot =>
                {
                    if (_coordinator.IsActive(m.Id))
                    {
                        _coordinator.DeactivateCurrent();
                        return;
                    }

                    // Clicking the armed row = start it (an AdminPanel-only operator is
                    // otherwise stuck in the lobby with no Start affordance).
                    if (string.Equals(_coordinator.ArmedEventId, m.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        _coordinator.Start(out _);
                        return;
                    }

                    var result = _coordinator.TryActivate(m.Id, out _);
                    if (result is not (ActivateResult.Started or ActivateResult.Armed))
                        _logger.LogWarning("[EventManager] AdminPanel activate '{Id}': {Result}.", m.Id, result);
                },
            });
        }

        return items;
    }

    // ── Admin gate ─────────────────────────────────────────────────────────

    private bool Denied(IGameClient client)
    {
        var steam = (SteamID)client.SteamId;
        if (_adminManager?.GetAdmin(steam)?.HasPermission(AdminFlag) == true)
            return false;

        Loc.Chat(_bridge.LocalizerManager, client, "EventManager_NoPermission");
        return true;
    }

    // ── !events (client) ───────────────────────────────────────────────────

    private void OnEvents(IGameClient client, StringCommand cmd)
    {
        if (Denied(client)) return;

        var lm = _bridge.LocalizerManager;

        // No args → menu; fall back to chat usage when MenuManager is missing.
        if (cmd.ArgCount < 1)
        {
            if (!_menu.OpenRoot(client))
                Loc.Chat(lm, client, "EventManager_Usage");
            return;
        }

        switch (cmd.GetArg(1).ToLowerInvariant())
        {
            case "list":
                ListEvents(client);
                break;

            case "on" when cmd.ArgCount >= 2:
                ActivateFor(client, cmd.GetArg(2));
                break;

            case "start":
            {
                var result = _coordinator.Start(out var started);
                Loc.Chat(lm, client, result switch
                {
                    ActivateResult.Started => "EventManager_Activated",
                    ActivateResult.Failed  => "EventManager_ActivateFailed",
                    _                      => "EventManager_NoArmed",
                }, started?.DisplayName ?? "");
                break;
            }

            case "startmode" when cmd.ArgCount >= 2:
            {
                var arg = cmd.GetArg(2).ToLowerInvariant();
                if (arg is not ("warmup" or "direct"))
                {
                    Loc.Chat(lm, client, "EventManager_Usage");
                    break;
                }

                _coordinator.StartMode = arg == "warmup" ? StartMode.Warmup : StartMode.Direct;
                Loc.Chat(lm, client, "EventManager_StartMode", arg);
                break;
            }

            case "off":
            {
                if (_coordinator.Disarm() is { } disarmed)
                {
                    Loc.Chat(lm, client, "EventManager_Disarmed", disarmed.DisplayName);
                    break;
                }

                var stopped = _coordinator.DeactivateCurrent();
                Loc.Chat(lm, client, stopped is null ? "EventManager_NothingActive" : "EventManager_Deactivated",
                    stopped?.DisplayName ?? "");
                break;
            }

            case "set" when cmd.ArgCount >= 4:
                SetFor(client, cmd.GetArg(2), cmd.GetArg(3), cmd.GetArg(4));
                break;

            case "intro" when cmd.ArgCount >= 2:
            {
                var on = cmd.GetArg(2).ToLowerInvariant() is "on" or "1" or "true";
                if (on ? _tools.IntroOn() : _tools.IntroOff())
                    Loc.Chat(lm, client, on ? "EventManager_Intro_On" : "EventManager_Intro_Off");
                break;
            }

            case "respawnall":
                Loc.Chat(lm, client, "EventManager_RespawnAll", _tools.RespawnAll());
                break;

            case "countdown":
            {
                var secs = cmd.ArgCount >= 2 && int.TryParse(cmd.GetArg(2), out var s) ? s : 5;
                _tools.Countdown(secs);
                break;
            }

            default:
                Loc.Chat(lm, client, "EventManager_Usage");
                break;
        }
    }

    private void ListEvents(IGameClient client)
    {
        var lm     = _bridge.LocalizerManager;
        var events = _coordinator.Registered;

        if (events.Count == 0)
        {
            Loc.Chat(lm, client, "EventManager_NoEvents");
            return;
        }

        Loc.Chat(lm, client, "EventManager_List_Header", events.Count);
        foreach (var mode in events)
        {
            var suffix = _coordinator.IsActive(mode.Id)
                ? Loc.Text(lm, client, "EventManager_List_ActiveSuffix")
                : string.Equals(_coordinator.ArmedEventId, mode.Id, StringComparison.OrdinalIgnoreCase)
                    ? Loc.Text(lm, client, "EventManager_List_ArmedSuffix")
                    : "";
            Loc.Chat(lm, client, "EventManager_List_Entry", mode.Id, mode.DisplayName, suffix);
        }
    }

    private void ActivateFor(IGameClient client, string id)
    {
        var lm     = _bridge.LocalizerManager;
        var result = _coordinator.TryActivate(id, out var mode);

        var isArmed = mode is not null
            && string.Equals(_coordinator.ArmedEventId, mode.Id, StringComparison.OrdinalIgnoreCase);

        Loc.Chat(lm, client, result switch
        {
            ActivateResult.Started                => "EventManager_Activated",
            ActivateResult.Armed                  => "EventManager_Armed",
            ActivateResult.Already when isArmed   => "EventManager_AlreadyArmed",
            ActivateResult.Already                => "EventManager_AlreadyActive",
            ActivateResult.Failed                 => "EventManager_ActivateFailed",
            _                                     => "EventManager_Unknown",
        }, mode?.DisplayName ?? id);

        if (result is ActivateResult.Started or ActivateResult.Armed)
            _logger.LogInformation("[EventManager] {Admin} {Action} '{Id}'.",
                client.Name, result == ActivateResult.Started ? "started" : "armed", mode!.Id);
    }

    private void SetFor(IGameClient client, string id, string key, string value)
    {
        var lm   = _bridge.LocalizerManager;
        var mode = _coordinator.Find(id);

        if (mode is null)
        {
            Loc.Chat(lm, client, "EventManager_Unknown", id);
            return;
        }

        Loc.Chat(lm, client,
            mode.TrySetSetting(key, value) ? "EventManager_SettingSet" : "EventManager_SettingRejected",
            mode.Id, key, value);
    }

    // ── events … (server console / RCON — trusted, no gate) ────────────────

    private void OnEventsServer(StringCommand cmd)
    {
        switch (cmd.ArgCount >= 1 ? cmd.GetArg(1).ToLowerInvariant() : "list")
        {
            case "list":
            {
                var events = _coordinator.Registered;
                _logger.LogInformation("[EventManager] {Count} event(s): {List} (active: {Active})",
                    events.Count,
                    string.Join(", ", events.Select(e => e.Id)),
                    _coordinator.ActiveEventId ?? "none");
                break;
            }

            case "on" when cmd.ArgCount >= 2:
            {
                var id     = cmd.GetArg(2);
                var result = _coordinator.TryActivate(id, out _);
                if (result is not (ActivateResult.Started or ActivateResult.Armed))
                    _logger.LogWarning("[EventManager] Console activate '{Id}': {Result}.", id, result);
                break;
            }

            case "start":
                _coordinator.Start(out _);
                break;

            case "startmode" when cmd.ArgCount >= 2:
            {
                var arg = cmd.GetArg(2).ToLowerInvariant();
                if (arg is "warmup" or "direct")
                    _coordinator.StartMode = arg == "warmup" ? StartMode.Warmup : StartMode.Direct;
                _logger.LogInformation("[EventManager] Start mode: {Mode}.", _coordinator.StartMode);
                break;
            }

            case "off":
                if (_coordinator.Disarm() is null)
                    _coordinator.DeactivateCurrent();
                break;

            case "set" when cmd.ArgCount >= 4:
            {
                var mode = _coordinator.Find(cmd.GetArg(2));
                if (mode is null || !mode.TrySetSetting(cmd.GetArg(3), cmd.GetArg(4)))
                    _logger.LogWarning("[EventManager] Console set rejected.");
                break;
            }

            case "intro" when cmd.ArgCount >= 2:
            {
                var on = cmd.GetArg(2).ToLowerInvariant() is "on" or "1" or "true";
                _ = on ? _tools.IntroOn() : _tools.IntroOff();
                break;
            }

            case "respawnall":
                _tools.RespawnAll();
                break;

            case "countdown":
            {
                var secs = cmd.ArgCount >= 2 && int.TryParse(cmd.GetArg(2), out var s) ? s : 5;
                _tools.Countdown(secs);
                break;
            }

            default:
                _logger.LogInformation(
                    "[EventManager] Usage: events list | on <id> | off | set <id> <key> <value> | intro on|off | respawnall | countdown [secs]");
                break;
        }
    }
}
