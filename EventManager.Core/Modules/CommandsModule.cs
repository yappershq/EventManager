using System.Collections.Generic;
using System.Linq;
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
    private const string AdminFlag = "eventmanager:admin";

    private readonly ILogger<CommandsModule> _logger;
    private readonly InterfaceBridge         _bridge;
    private readonly EventCoordinator        _coordinator;
    private readonly StreamToolsModule       _tools;
    private readonly MenuModule              _menu;

    private IAdminManager? _adminManager;

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
    }

    public void Shutdown() { }

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

            case "off":
            {
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
                : "";
            Loc.Chat(lm, client, "EventManager_List_Entry", mode.Id, mode.DisplayName, suffix);
        }
    }

    private void ActivateFor(IGameClient client, string id)
    {
        var lm = _bridge.LocalizerManager;

        if (_coordinator.TryActivate(id, out var mode, out var reason))
        {
            Loc.Chat(lm, client, "EventManager_Activated", mode!.DisplayName);
            _logger.LogInformation("[EventManager] {Admin} activated '{Id}'.", client.Name, mode.Id);
            return;
        }

        Loc.Chat(lm, client, reason switch
        {
            "already" => "EventManager_AlreadyActive",
            "failed"  => "EventManager_ActivateFailed",
            _         => "EventManager_Unknown",
        }, mode?.DisplayName ?? id);
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
                var id = cmd.GetArg(2);
                if (!_coordinator.TryActivate(id, out _, out var reason))
                    _logger.LogWarning("[EventManager] Console activate '{Id}' failed: {Reason}.", id, reason);
                break;
            }

            case "off":
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
