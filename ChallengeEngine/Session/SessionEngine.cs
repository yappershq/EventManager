using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ChallengeEngine.Modifiers;
using ChallengeEngine.Persistence;
using ChallengeEngine.Plugins;
using ChallengeEngine.Utils;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEvents;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using ChallengeEngine.Shared;

namespace ChallengeEngine.Session;

internal enum SessionState { Idle, Lobby, Round, Intermission, Finale, Crowned }

/// <summary>
/// The session FSM: Lobby → Round → Intermission → (loop, escalating) → Finale → Crowned. Owns the
/// points ledger, round loop, escalation, finale, and crowning. Challenges plug in via
/// <see cref="IChallenge"/>; the engine drives everything around them. All on the game thread.
///
/// Phase 1: in-memory ledger, timer-driven loop (game-time clock), escalation-by-time, top-N finale,
/// crown. Map changes reconcile via <see cref="IGameListener.OnGameActivate"/> (StopOnMapEnd timers
/// die on map end). Disconnect self-heal via <see cref="IClientListener.OnClientDisconnecting"/>.
/// DB persistence / live overlay come in later phases.
/// </summary>
internal sealed class SessionEngine(ILogger<SessionEngine> logger, InterfaceBridge bridge, Nav.LiveNavMesh nav, ChallengeStore store)
    : IModule, IGameListener, IClientListener, IEventListener
{
    private const double IntermissionSeconds = 12.0;

    private readonly Dictionary<string, IChallenge>  _challenges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IModifier>   _modifiers  = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, PlayerTotals> _ledger     = new();
    private static readonly Random Rng = new();
    private IModifier? _activeModifier;

    private IChallenge?   _active;
    private RoundContext? _round;
    private SessionState  _state = SessionState.Idle;

    private long  _sessionEndMs;   // wall clock (Environment.TickCount64) — survives map change; CurTime resets per level
    private long  _nextPhaseMs;
    private int   _phase;
    private int   _roundNumber;
    private double _phaseCadenceSeconds = 15 * 60;
    private int   _finaleSize = 6;
    private bool  _autostart  = true;
    private bool  _finaleQueued;
    private string? _pendingChallengeId;   // operator swap → applied at the next heat

    private int _sessionDbId;                                  // ce_session row (0 = persistence off / not yet opened)
    private (int id, List<ScoreRow> totals)? _resumeCache;     // preloaded interrupted session, applied on next start

    private static long NowMs => Environment.TickCount64;

    // ── IModule ───────────────────────────────────────────────────────────
    public bool Init() => true;
    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
    {
        nav.Init();   // resolves the nav mesh for walkable hill placement; degrades to fallback if unavailable
        store.Init(); // opens ce_* persistence; degrades to in-memory-only if no DB config
        _ = LoadResumeAsync(); // preload an interrupted session's totals (crash-resume), applied on next start
        RegisterChallenge(new Challenges.KingOfTheHill());
        RegisterChallenge(new Challenges.Territories());
        RegisterChallenge(new Challenges.ThePurge());
        RegisterChallenge(new Challenges.GunGame());
        RegisterChallenge(new Challenges.BombTag());
        RegisterChallenge(new Challenges.NullChallenge());

        // Built-in escalation modifiers (convar overlays; capture/restore, no change-hook). Inspired by
        // SuperPowers powers (Astronaut/BunnyHop/Grenadier/Catapult/FriendlyFire). ConVarModifier
        // null-checks each convar, so a missing/cheat-gated one is skipped rather than crashing.
        RegisterModifier(new ConVarModifier(bridge.ConVarManager, "lowgrav",      "modifier.lowgrav",      ("sv_gravity", "300")));
        RegisterModifier(new ConVarModifier(bridge.ConVarManager, "bhop",         "modifier.bhop",         ("sv_autobunnyhopping", "1"), ("sv_enablebunnyhopping", "1")));
        RegisterModifier(new ConVarModifier(bridge.ConVarManager, "infammo",      "modifier.infammo",      ("sv_infinite_ammo", "1")));
        RegisterModifier(new ConVarModifier(bridge.ConVarManager, "highjump",     "modifier.highjump",     ("sv_jump_impulse", "450")));
        RegisterModifier(new ConVarModifier(bridge.ConVarManager, "friendlyfire", "modifier.friendlyfire", ("mp_friendlyfire", "1")));
        RegisterModifier(new ConVarModifier(bridge.ConVarManager, "grenadier",    "modifier.grenadier",    ("ammo_grenade_limit_total", "5")));

        bridge.ModSharp.InstallGameListener(this);
        bridge.ClientManager.InstallClientListener(this);
        bridge.EventManager.InstallEventListener(this);
        bridge.EventManager.HookEvent("player_death");
    }

    public void Shutdown()
    {
        bridge.EventManager.RemoveEventListener(this);
        bridge.ClientManager.RemoveClientListener(this);
        bridge.ModSharp.RemoveGameListener(this);
        EndSession(crowned: false);
    }

    // ── Registration / query (for the plugin's IEventMode) ─────────────────
    public void RegisterChallenge(IChallenge challenge) => _challenges[challenge.Id] = challenge;
    public IReadOnlyCollection<IChallenge> Challenges => _challenges.Values;
    public void RegisterModifier(IModifier modifier) => _modifiers[modifier.Id] = modifier;
    public IReadOnlyCollection<IModifier> Modifiers => _modifiers.Values;
    public bool IsRunning => _state is not SessionState.Idle;

    // ── Session control (driven by the EventManager IEventMode) ────────────

    public void StartSession(string challengeId, int durationMinutes, int escalationMinutes, int finaleSize, bool autostart)
    {
        if (_challenges.GetValueOrDefault(challengeId) is not { } challenge)
        {
            logger.LogWarning("[ChallengeEngine] Unknown challenge '{Id}' — cannot start.", challengeId);
            return;
        }

        _active              = challenge;
        _ledger.Clear();
        _phase               = 0;
        _roundNumber         = 0;
        _finaleQueued        = false;
        _pendingChallengeId  = null;
        _finaleSize          = Math.Clamp(finaleSize, 2, 10);
        _autostart           = autostart;
        _phaseCadenceSeconds = Math.Clamp(escalationMinutes, 1, 60) * 60.0;
        _sessionEndMs        = NowMs + (long)Math.Clamp(durationMinutes, 1, 180) * 60_000L;
        _nextPhaseMs         = NowMs + (long)(_phaseCadenceSeconds * 1000);
        _state               = SessionState.Lobby;

        logger.LogInformation("[ChallengeEngine] Session started: {Challenge}, {Mins} min.",
            challenge.DisplayNameKey, durationMinutes);

        // Persistence: resume a crashed session's totals, else open a fresh ce_session row.
        if (_resumeCache is { totals.Count: > 0 } rc)
        {
            _sessionDbId = rc.id;
            foreach (var r in rc.totals)
                _ledger[r.SteamId] = new PlayerTotals { SteamId = r.SteamId, Name = r.Name, Points = r.Points, RoundWins = r.RoundWins };
            logger.LogInformation("[ChallengeEngine] Resumed {N} players from an interrupted session.", rc.totals.Count);
        }
        else
        {
            _sessionDbId = 0;
            Persist(BeginSessionAsync(challenge.Id, DateTime.UtcNow.AddMinutes(Math.Clamp(durationMinutes, 1, 180))));
        }
        _resumeCache = null;

        Loc.ChatAll(bridge.LocalizerManager, bridge.ClientManager, "challenge.start");
        bridge.ModSharp.PushTimer(TryBeginRound, autostart ? 8.0 : 3.0, GameTimerFlags.StopOnMapEnd);
    }

    public void EndSession(bool crowned)
    {
        ClearModifier(); // restore any convars a modifier changed
        _round?.SweepMarkers();
        _round  = null;
        _active = null;
        _state  = SessionState.Idle;
        if (!crowned) logger.LogInformation("[ChallengeEngine] Session ended (no crown).");
    }

    // ── Round loop ─────────────────────────────────────────────────────────

    private void TryBeginRound()
    {
        // Re-entrancy / stray-timer guard: only advance from a between-heats state.
        if (_state is not (SessionState.Lobby or SessionState.Intermission)) return;

        // Apply an operator challenge-swap for this heat.
        if (_pendingChallengeId is { } pid && _challenges.GetValueOrDefault(pid) is { } pc)
        {
            _active = pc;
            _pendingChallengeId = null;
        }

        if (!_finaleQueued && NowMs >= _sessionEndMs)
            _finaleQueued = true;

        // Count all in-game players incl. bots so bot-only smoke tests can meet MinPlayers.
        var players = bridge.ClientManager.GetGameClients(inGame: true).Count();
        if (_active is null || players < _active.MinPlayers)
        {
            Loc.ChatAll(bridge.LocalizerManager, bridge.ClientManager, "challenge.waiting", players, _active?.MinPlayers ?? 0);
            bridge.ModSharp.PushTimer(TryBeginRound, 5.0, GameTimerFlags.StopOnMapEnd);
            return;
        }

        MaybeEscalate();
        BeginRound(finale: _finaleQueued);
    }

    private void BeginRound(bool finale)
    {
        if (_active is null) return;

        _roundNumber++;
        _state = finale ? SessionState.Finale : SessionState.Round;
        _round = new RoundContext(bridge, this, nav, _roundNumber, _phase, ActiveModifiers(finale));

        if (finale)
            Loc.ChatAll(bridge.LocalizerManager, bridge.ClientManager, "challenge.finale");
        else
            Loc.ChatAll(bridge.LocalizerManager, bridge.ClientManager, "challenge.round", _roundNumber);

        try { _active.StartRound(_round); }
        catch (Exception ex) { logger.LogError(ex, "[ChallengeEngine] StartRound threw — voiding heat."); _round?.SweepMarkers(); _round = null; NextAfterRound(); return; }

        var secs = Math.Max(10, _active.RoundSeconds);
        var scheduledRound = _roundNumber;   // snapshot: the closure must not re-read the live field (stale-timer guard)
        bridge.ModSharp.PushTimer(() => OnRoundTimeout(scheduledRound), secs, GameTimerFlags.StopOnMapEnd);
        // Repeatable is REQUIRED to loop — a non-Repeatable Func timer fires once then is dropped regardless
        // of returning Continue; TimerAction.Stop clears Repeatable to end it when the heat closes.
        bridge.ModSharp.PushTimer(TickHeat, 0.1, GameTimerFlags.Repeatable | GameTimerFlags.StopOnMapEnd);
    }

    /// <summary>Repeating per-tick pump for the active challenge; stops itself when the heat ends.</summary>
    private TimerAction TickHeat()
    {
        if (_state is not (SessionState.Round or SessionState.Finale) || _round is not { Ended: false } ctx || _active is null)
            return TimerAction.Stop;

        try { _active.Tick(ctx); }
        catch (Exception ex) { logger.LogError(ex, "[ChallengeEngine] Tick threw."); }
        return TimerAction.Continue;
    }

    private void OnRoundTimeout(int round)
    {
        if (_round is null || _round.RoundNumber != round || _round.Ended) return;

        try { _round.EndRound(_active!.ForceEnd(_round)); }
        catch (Exception ex) { logger.LogError(ex, "[ChallengeEngine] ForceEnd threw — voiding heat."); _round?.SweepMarkers(); _round = null; NextAfterRound(); }
    }

    /// <summary>Called by RoundContext.EndRound (challenge- or timeout-driven).</summary>
    internal void OnRoundEnded(RoundContext ctx)
    {
        if (_round != ctx) return;

        var finale = _state == SessionState.Finale;
        ScoreRound(ctx);

        try { _active?.Cleanup(ctx); } catch (Exception ex) { logger.LogError(ex, "[ChallengeEngine] Cleanup threw."); }
        ctx.SweepMarkers();

        if (finale) { CrownChampion(); return; }

        FlashStandings();
        NextAfterRound();
    }

    private void NextAfterRound()
    {
        _state = SessionState.Intermission;
        bridge.ModSharp.PushTimer(TryBeginRound, IntermissionSeconds, GameTimerFlags.StopOnMapEnd);
    }

    // ── Scoring ────────────────────────────────────────────────────────────

    private void ScoreRound(RoundContext ctx)
    {
        var mult = 1.0 + 0.5 * _phase;

        var scores = ctx.PendingAwards.ToDictionary(k => k.Key, v => v.Value);
        if (ctx.Result is { } r)
            foreach (var s in r.Scores)
                scores[s.SteamId] = scores.GetValueOrDefault(s.SteamId) + s.Points;

        foreach (var (steamId, pts) in scores)
            Totals(steamId).Points += (int)Math.Round(pts * mult);

        if (ctx.Result?.RoundWinnerSteamId is { } winner)
            Totals(winner).RoundWins++;

        PersistRound();
    }

    private PlayerTotals Totals(ulong steamId)
    {
        if (!_ledger.TryGetValue(steamId, out var t))
        {
            var name = bridge.ClientManager.GetGameClient((SteamID)steamId)?.Name ?? steamId.ToString();
            _ledger[steamId] = t = new PlayerTotals { SteamId = steamId, Name = name };
        }
        return t;
    }

    private IReadOnlyList<PlayerTotals> Standings =>
        _ledger.Values.OrderByDescending(t => t.Points).ThenByDescending(t => t.RoundWins).ToList();

    // ── Escalation ─────────────────────────────────────────────────────────

    private void MaybeEscalate()
    {
        if (NowMs < _nextPhaseMs) return;

        _phase++;
        _nextPhaseMs = NowMs + (long)(_phaseCadenceSeconds * 1000);
        Loc.ChatAll(bridge.LocalizerManager, bridge.ClientManager, "challenge.escalation", _phase);
        logger.LogInformation("[ChallengeEngine] Escalated to phase {Phase}.", _phase);

        // Escalation injects a fresh random modifier — "stakes rising" made literal.
        var pool = _modifiers.Values.Where(m => !ReferenceEquals(m, _activeModifier)).ToList();
        if (pool.Count > 0) InjectModifier(pool[Rng.Next(pool.Count)].Id);
    }

    // ── Modifiers ──────────────────────────────────────────────────────────

    /// <summary>Enable a modifier (toggling off if it's already active). Restores the previous one first.</summary>
    public void InjectModifier(string id)
    {
        if (!_modifiers.TryGetValue(id, out var m)) return;
        if (ReferenceEquals(_activeModifier, m)) { ClearModifier(); return; }

        ClearModifier();
        try { m.Enable(); _activeModifier = m; }
        catch (Exception ex) { logger.LogError(ex, "[ChallengeEngine] modifier Enable threw."); _activeModifier = null; return; }

        Loc.ChatAll(bridge.LocalizerManager, bridge.ClientManager, m.DisplayNameKey);
    }

    /// <summary>Disable the active modifier and restore its convars.</summary>
    public void ClearModifier()
    {
        if (_activeModifier is not { } m) return;
        try { m.Disable(); } catch (Exception ex) { logger.LogError(ex, "[ChallengeEngine] modifier Disable threw."); }
        _activeModifier = null;
    }

    private IReadOnlyCollection<string> ActiveModifiers(bool finale)
        => finale ? ["finale"] : _phase >= 1 ? [$"phase{_phase}"] : [];

    // ── Finale + crown ─────────────────────────────────────────────────────

    private void CrownChampion()
    {
        var champ = Standings.FirstOrDefault();
        _state = SessionState.Crowned;

        if (champ is null)
            Loc.ChatAll(bridge.LocalizerManager, bridge.ClientManager, "challenge.champion.none");
        else
        {
            Loc.ChatAll(bridge.LocalizerManager, bridge.ClientManager, "challenge.champion", champ.Name, champ.Points, champ.RoundWins);
            logger.LogInformation("[ChallengeEngine] Champion: {Name} ({Pts} pts).", champ.Name, champ.Points);
        }

        if (store.Ready)
            Persist(store.CrownAsync(_sessionDbId, champ?.SteamId, Snapshot()));

        _round?.SweepMarkers();
        _round = null;
        // Stays Crowned until the operator deactivates Challenge Night (EM restores convars on deactivate).
    }

    private void FlashStandings()
    {
        var top = Standings.Take(5).ToList();
        if (top.Count == 0) return;

        Loc.ChatAll(bridge.LocalizerManager, bridge.ClientManager, "challenge.standings.header");
        for (var i = 0; i < top.Count; i++)
            Loc.ChatAll(bridge.LocalizerManager, bridge.ClientManager, "challenge.standings.row", i + 1, top[i].Name, top[i].Points);
    }

    // ── Operator verbs (routed from the IEventMode actions) ────────────────

    /// <summary>End the current heat now without scoring, advancing to the next.</summary>
    public void SkipRound()
    {
        if (_round is { Ended: false } r) r.EndRound(new RoundResult([], null));
    }

    /// <summary>Make the next heat the grand finale (voiding a normal heat in progress).</summary>
    public void ForceFinale()
    {
        _finaleQueued = true;
        if (_state == SessionState.Round && _round is { Ended: false } r) r.EndRound(new RoundResult([], null));
    }

    /// <summary>Begin a heat immediately (when autostart is off or during intermission).</summary>
    public void StartRoundNow()
    {
        if (_state is SessionState.Lobby or SessionState.Intermission)
            bridge.ModSharp.PushTimer(TryBeginRound, 0.1, GameTimerFlags.StopOnMapEnd);
    }

    public void ExtendMinutes(int minutes)
    {
        if (minutes > 0) _sessionEndMs += (long)minutes * 60_000L;
    }

    /// <summary>Swap the challenge for the next heat (applied at the next round boundary).</summary>
    public void RequestChallenge(string challengeId)
    {
        if (_challenges.ContainsKey(challengeId)) _pendingChallengeId = challengeId;
    }

    // ── Control-room surfaces ──────────────────────────────────────────────

    /// <summary>Top-3 current standings as roles (1st/2nd/3rd) for the operator UI.</summary>
    public IReadOnlyDictionary<ulong, string> ActiveRoles()
    {
        var d   = new Dictionary<ulong, string>();
        var top = Standings.Take(3).ToList();
        for (var i = 0; i < top.Count; i++)
            d[top[i].SteamId] = (i + 1) switch { 1 => "1st", 2 => "2nd", _ => "3rd" };
        return d;
    }

    /// <summary>Live gameplay-state JSON for the observe lane → website/OBS overlay.</summary>
    public string LiveStateJson()
    {
        var standings = Standings.Take(10).Select((t, i) => new
        {
            rank = i + 1, steamId = t.SteamId.ToString(), name = t.Name, points = t.Points, roundWins = t.RoundWins,
        });
        var secondsLeft = _state is SessionState.Idle ? 0 : (int)Math.Max(0, (_sessionEndMs - NowMs) / 1000);
        return JsonSerializer.Serialize(new
        {
            state      = _state.ToString(),
            challenge  = _active?.Id,
            round      = _roundNumber,
            phase      = _phase,
            secondsLeft,
            multiplier = 1.0 + 0.5 * _phase,
            standings,
        });
    }

    // ── Persistence plumbing (fire-and-forget writes; async resume preload) ─

    private async Task LoadResumeAsync()
    {
        try { _resumeCache = await store.LoadUnfinishedAsync(); }
        catch (Exception ex) { logger.LogWarning(ex, "[ChallengeEngine] resume preload failed."); }
    }

    private async Task BeginSessionAsync(string challengeId, DateTime endsAtUtc)
        => _sessionDbId = await store.BeginSessionAsync(challengeId, endsAtUtc);

    private void PersistRound()
    {
        if (store.Ready) Persist(store.SaveRoundAsync(_sessionDbId, _phase, _roundNumber, Snapshot()));
    }

    private List<ScoreRow> Snapshot()
        => _ledger.Values.Select(t => new ScoreRow(t.SteamId, t.Name, t.Points, t.RoundWins)).ToList();

    private void Persist(Task task)
        => task.ContinueWith(t => logger.LogWarning(t.Exception, "[ChallengeEngine] DB write failed."),
            TaskContinuationOptions.OnlyOnFaulted);

    // ── Map change reconcile (StopOnMapEnd timers die on map end) ──────────

    int IGameListener.ListenerVersion  => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;

    void IGameListener.OnGameActivate()
    {
        if (!IsRunning) return;

        // The pending loop timer was nuked by the map change — void any in-flight heat and re-arm.
        if (_state is SessionState.Round or SessionState.Finale)
        {
            _round?.SweepMarkers();
            _round = null;
            _state = SessionState.Intermission;
        }
        logger.LogInformation("[ChallengeEngine] Map change — reconciling session loop.");
        bridge.ModSharp.PushTimer(TryBeginRound, 8.0, GameTimerFlags.StopOnMapEnd);
    }

    // ── Disconnect self-heal ───────────────────────────────────────────────

    int IEventListener.ListenerVersion  => IEventListener.ApiVersion;
    int IEventListener.ListenerPriority => 0;

    void IEventListener.FireGameEvent(IGameEvent @event)
    {
        if (@event is not IEventPlayerDeath death) return;
        if (_state is not (SessionState.Round or SessionState.Finale) || _round is not { Ended: false } ctx || _active is null) return;

        if (death.VictimController?.SteamId is not { } vSid) return;
        var victim   = (ulong)vSid;
        ulong? attacker = death.KillerController?.SteamId is { } kSid && (ulong)kSid != victim ? (ulong)kSid : null;

        try { _active.OnKill(ctx, victim, attacker); }
        catch (Exception ex) { logger.LogError(ex, "[ChallengeEngine] OnKill threw."); }
    }

    int IClientListener.ListenerVersion  => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;

    void IClientListener.OnClientDisconnecting(IGameClient client, NetworkDisconnectionReason reason)
    {
        // Points survive (SteamID-keyed ledger). If they held a role this heat, let the challenge react.
        if (_round is not { Ended: false } ctx || _active is null) return;

        LeaveReaction reaction;
        try { reaction = _active.OnPlayerLeft(ctx, (ulong)client.SteamId); }
        catch (Exception ex) { logger.LogError(ex, "[ChallengeEngine] OnPlayerLeft threw."); return; }

        switch (reaction)
        {
            case LeaveReaction.VoidRound:
                ctx.EndRound(new RoundResult([], null));
                break;
            case LeaveReaction.EndRound:
                ctx.EndRound(_active.ForceEnd(ctx));
                break;
            // Continue / ReassignRole: the challenge handled it inline.
        }
    }
}
