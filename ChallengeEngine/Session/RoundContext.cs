using System.Collections.Generic;
using System.Linq;
using ChallengeEngine.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace ChallengeEngine.Session;

/// <summary>Engine-side <see cref="IRoundContext"/> — one instance per heat.</summary>
internal sealed class RoundContext : IRoundContext
{
    private readonly InterfaceBridge _bridge;
    private readonly SessionEngine   _engine;
    private readonly HashSet<ulong>       _eliminated = new();
    private readonly Dictionary<ulong, int> _pending  = new();   // instant awards → folded into result
    private readonly Dictionary<int, CEntityHandle<IBaseEntity>> _markers = new();  // token id → serial handle
    private int _markerCounter;

    public RoundContext(InterfaceBridge bridge, SessionEngine engine, int roundNumber, int phase, IReadOnlyCollection<string> modifiers)
    {
        _bridge     = bridge;
        _engine     = engine;
        RoundNumber = roundNumber;
        Phase       = phase;
        Modifiers   = modifiers;
    }

    public int RoundNumber { get; }
    public int Phase       { get; }
    public IReadOnlyCollection<string> Modifiers { get; }
    public IDictionary<string, object> Scratch { get; } = new Dictionary<string, object>();

    internal IReadOnlyDictionary<ulong, int> PendingAwards => _pending;
    internal bool          Ended  { get; private set; }
    internal RoundResult?  Result { get; private set; }

    public IReadOnlyList<IGameClient> AlivePlayers =>
        _bridge.ClientManager.GetGameClients(inGame: true)
            .Where(c => !c.IsFakeClient && !_eliminated.Contains((ulong)c.SteamId))
            .ToList();

    public IGameClient? GetPlayer(ulong steamId)
        => _bridge.ClientManager.GetGameClient((SteamID)steamId);

    // ── Heat control ─────────────────────────────────────────────────────────

    public void EndRound(RoundResult result)
    {
        if (Ended) return;
        Ended  = true;
        Result = result;
        _engine.OnRoundEnded(this);
    }

    public void Eliminate(ulong steamId, string? reason = null) => _eliminated.Add(steamId);

    public void AwardPoints(ulong steamId, int points, string? note = null)
        => _pending[steamId] = _pending.GetValueOrDefault(steamId) + points;

    // ── Safe helpers ─────────────────────────────────────────────────────────

    public void CenterAll(string text)
    {
        foreach (var c in _bridge.ClientManager.GetGameClients(inGame: true))
            if (!c.IsFakeClient) c.Print(HudPrintChannel.Center, text);
    }

    public void Center(IGameClient client, string text) => client.Print(HudPrintChannel.Center, text);

    public void PlaySoundAll(string soundEvent)
    {
        foreach (var c in _bridge.ClientManager.GetGameClients(inGame: true))
            if (!c.IsFakeClient) c.GetPlayerController()?.EmitSoundClient(soundEvent);
    }

    public bool TeleportSafe(IGameClient client, Vector position, Vector? angles = null)
    {
        // ponytail: Phase 1 does a direct teleport. Phase 2 (KotH) swaps in the SuperPowers
        // EntityPlacementTest + DropToGround port for wall/void/telefrag safety.
        var pawn = client.GetPlayerController()?.GetPlayerPawn();
        if (pawn is null) return false;
        pawn.Teleport(position, angles, null);
        return true;
    }

    public EntityToken SpawnMarker(string classname, Vector position, IReadOnlyDictionary<string, string>? keyValues = null)
    {
        var kv = new Dictionary<string, KeyValuesVariantValueItem>
        {
            ["origin"] = $"{position.X} {position.Y} {position.Z}",
        };
        if (keyValues is not null)
            foreach (var (k, v) in keyValues)
                kv[k.ToLowerInvariant()] = v;   // DispatchSpawn requires lowercase keys

        // SpawnEntitySync = full precache pipeline (safe). CreateEntityByName bypasses precache → can crash.
        var ent = _bridge.EntityManager.SpawnEntitySync(classname, kv);
        if (ent is null) return EntityToken.None;

        var id = ++_markerCounter;
        _markers[id] = ent.RefHandle;   // serial-versioned handle → survives index recycling
        return new EntityToken(id);
    }

    public void RemoveEntity(EntityToken token)
    {
        if (!_markers.Remove(token.Id, out var handle)) return;
        _bridge.EntityManager.FindEntityByHandle(handle)?.Kill();
    }

    /// <summary>Engine sweeps any markers the challenge forgot, after the heat ends.</summary>
    internal void SweepMarkers()
    {
        foreach (var handle in _markers.Values)
            _bridge.EntityManager.FindEntityByHandle(handle)?.Kill();
        _markers.Clear();
    }
}
