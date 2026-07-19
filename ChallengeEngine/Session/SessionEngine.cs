using System;
using System.Collections.Generic;
using System.Linq;
using ChallengeEngine.Plugins;
using ChallengeEngine.Utils;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
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
internal sealed class SessionEngine(ILogger<SessionEngine> logger, InterfaceBridge bridge)
    : IModule, IGameListener, IClientListener
{
    private const double IntermissionSeconds = 12.0;

    private readonly Dictionary<string, IChallenge>  _challenges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, PlayerTotals> _ledger     = new();

    private IChallenge?   _active;
    private RoundContext? _round;
    private SessionState  _state = SessionState.Idle;

    private float _sessionEndTime;
    private float _nextPhaseTime;
    private int   _phase;
    private int   _roundNumber;
    private double _phaseCadenceSeconds = 15 * 60;
    private int   _finaleSize = 6;
    private bool  _autostart  = true;
    private bool  _finaleQueued;

    private float Now => bridge.ModSharp.GetGlobals().CurTime;

    // ── IModule ───────────────────────────────────────────────────────────
    public bool Init() => true;
    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
    {
        RegisterChallenge(new Challenges.NullChallenge());
        bridge.ModSharp.InstallGameListener(this);
        bridge.ClientManager.InstallClientListener(this);
    }

    public void Shutdown()
    {
        bridge.ClientManager.RemoveClientListener(this);
        bridge.ModSharp.RemoveGameListener(this);
        EndSession(crowned: false);
    }

    // ── Registration / query (for the plugin's IEventMode) ─────────────────
    public void RegisterChallenge(IChallenge challenge) => _challenges[challenge.Id] = challenge;
    public IReadOnlyCollection<IChallenge> Challenges => _challenges.Values;
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
        _finaleSize          = Math.Clamp(finaleSize, 2, 10);
        _autostart           = autostart;
        _phaseCadenceSeconds = Math.Clamp(escalationMinutes, 1, 60) * 60.0;
        _sessionEndTime      = Now + (float)(Math.Clamp(durationMinutes, 1, 180) * 60);
        _nextPhaseTime       = Now + (float)_phaseCadenceSeconds;
        _state               = SessionState.Lobby;

        logger.LogInformation("[ChallengeEngine] Session started: {Challenge}, {Mins} min.",
            challenge.DisplayNameKey, durationMinutes);

        Loc.ChatAll(bridge.LocalizerManager, bridge.ClientManager, "challenge.start");
        bridge.ModSharp.PushTimer(TryBeginRound, autostart ? 8.0 : 3.0, GameTimerFlags.StopOnMapEnd);
    }

    public void EndSession(bool crowned)
    {
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

        if (!_finaleQueued && Now >= _sessionEndTime)
            _finaleQueued = true;

        var humans = bridge.ClientManager.GetGameClients(inGame: true).Count(c => !c.IsFakeClient);
        if (_active is null || humans < _active.MinPlayers)
        {
            Loc.ChatAll(bridge.LocalizerManager, bridge.ClientManager, "challenge.waiting", humans, _active?.MinPlayers ?? 0);
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
        _round = new RoundContext(bridge, this, _roundNumber, _phase, ActiveModifiers(finale));

        if (finale)
            Loc.ChatAll(bridge.LocalizerManager, bridge.ClientManager, "challenge.finale");
        else
            Loc.ChatAll(bridge.LocalizerManager, bridge.ClientManager, "challenge.round", _roundNumber);

        try { _active.StartRound(_round); }
        catch (Exception ex) { logger.LogError(ex, "[ChallengeEngine] StartRound threw — voiding heat."); NextAfterRound(); return; }

        var secs = Math.Max(10, _active.RoundSeconds);
        bridge.ModSharp.PushTimer(() => OnRoundTimeout(_roundNumber), secs, GameTimerFlags.StopOnMapEnd);
    }

    private void OnRoundTimeout(int round)
    {
        if (_round is null || _round.RoundNumber != round || _round.Ended) return;

        try { _round.EndRound(_active!.ForceEnd(_round)); }
        catch (Exception ex) { logger.LogError(ex, "[ChallengeEngine] ForceEnd threw — voiding heat."); NextAfterRound(); }
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

        var scores = new Dictionary<ulong, int>(ctx.PendingAwards.ToDictionary(k => k.Key, v => v.Value));
        if (ctx.Result is { } r)
            foreach (var s in r.Scores)
                scores[s.SteamId] = scores.GetValueOrDefault(s.SteamId) + s.Points;

        foreach (var (steamId, pts) in scores)
            Totals(steamId).Points += (int)Math.Round(pts * mult);

        if (ctx.Result?.RoundWinnerSteamId is { } winner)
            Totals(winner).RoundWins++;
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
        if (Now < _nextPhaseTime) return;

        _phase++;
        _nextPhaseTime = Now + (float)_phaseCadenceSeconds;
        Loc.ChatAll(bridge.LocalizerManager, bridge.ClientManager, "challenge.escalation", _phase);
        logger.LogInformation("[ChallengeEngine] Escalated to phase {Phase}.", _phase);
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
