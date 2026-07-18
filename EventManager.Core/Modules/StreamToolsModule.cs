using EventManager.Plugins;
using EventManager.Utils;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;

namespace EventManager.Modules;

/// <summary>
/// Stream QoL tools: intro mode (round can't end while a shot is set up), respawn-all,
/// countdown announce. All invoked from the /events command or menu.
/// </summary>
internal sealed class StreamToolsModule : IModule
{
    private readonly ILogger<StreamToolsModule> _logger;
    private readonly InterfaceBridge            _bridge;

    /// <summary>Captured pre-intro value of mp_ignore_round_win_conditions; null = intro off.</summary>
    private string? _introRevert;

    /// <summary>Generation counter — starting a new countdown orphans the previous timer chain.</summary>
    private int _countdownGen;

    public StreamToolsModule(ILogger<StreamToolsModule> logger, InterfaceBridge bridge)
    {
        _logger = logger;
        _bridge = bridge;
    }

    // ── IModule ────────────────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown() => IntroOff();

    // ── Intro mode ─────────────────────────────────────────────────────────

    public bool IntroActive => _introRevert is not null;

    public bool IntroOn()
    {
        if (IntroActive) return false;

        var cvar = _bridge.ConVarManager.FindConVar("mp_ignore_round_win_conditions");
        if (cvar is null)
        {
            _logger.LogWarning("[EventManager] mp_ignore_round_win_conditions not found — intro mode unavailable.");
            return false;
        }

        _introRevert = cvar.GetString();
        cvar.SetString("1");
        _logger.LogInformation("[EventManager] Intro mode ON.");
        return true;
    }

    public bool IntroOff()
    {
        if (_introRevert is null) return false;

        _bridge.ConVarManager.FindConVar("mp_ignore_round_win_conditions")?.SetString(_introRevert);
        _introRevert = null;
        _logger.LogInformation("[EventManager] Intro mode OFF.");
        return true;
    }

    // ── Respawn all ────────────────────────────────────────────────────────

    /// <summary>Respawns every dead T/CT player. Returns the number respawned.</summary>
    public int RespawnAll()
    {
        var count = 0;

        foreach (var client in _bridge.ClientManager.GetGameClients(inGame: true))
        {
            if (client.GetPlayerController() is not { } controller || !controller.IsValid()) continue;
            if (controller.Team is not (CStrikeTeam.TE or CStrikeTeam.CT)) continue;
            if (controller.GetPlayerPawn() is { IsAlive: true }) continue;

            controller.Respawn();
            count++;
        }

        _logger.LogInformation("[EventManager] Respawned {Count} player(s).", count);
        return count;
    }

    // ── Countdown ──────────────────────────────────────────────────────────

    /// <summary>Center-screen countdown for the whole server: secs..1 then GO.</summary>
    public void Countdown(int seconds)
    {
        seconds = System.Math.Clamp(seconds, 1, 15);

        var gen = ++_countdownGen;
        Loc.ChatAll(_bridge.LocalizerManager, _bridge.ClientManager, "EventManager_Countdown_ChatStart", seconds);
        Tick(gen, seconds);
    }

    private void Tick(int gen, int remaining)
    {
        if (gen != _countdownGen) return; // superseded by a newer countdown

        if (remaining <= 0)
        {
            Loc.CenterAll(_bridge.LocalizerManager, _bridge.ClientManager, "EventManager_Countdown_Go");
            return;
        }

        Loc.CenterAll(_bridge.LocalizerManager, _bridge.ClientManager, "EventManager_Countdown_Tick", remaining);
        // StopOnMapEnd only: a round transition mid-countdown (e.g. the operator pressing Start)
        // must not kill the chain — "GO!" still has to land.
        _bridge.ModSharp.PushTimer(() => Tick(gen, remaining - 1), 1.0, GameTimerFlags.StopOnMapEnd);
    }
}
