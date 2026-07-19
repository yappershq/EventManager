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
    private readonly Nav.LiveNavMesh _nav;
    private readonly HashSet<ulong>       _eliminated = new();
    private readonly Dictionary<ulong, int> _pending  = new();   // instant awards → folded into result
    private readonly Dictionary<int, CEntityHandle<IBaseEntity>> _markers = new();  // token id → serial handle
    private int _markerCounter;

    private static readonly System.Random Rng = new();

    public RoundContext(InterfaceBridge bridge, SessionEngine engine, Nav.LiveNavMesh nav, int roundNumber, int phase, IReadOnlyCollection<string> modifiers)
    {
        _bridge     = bridge;
        _engine     = engine;
        _nav        = nav;
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

    // Bots included (doc: "fake clients OK for testing") so bot-only smoke tests work; excluded only when eliminated.
    public IReadOnlyList<IGameClient> AlivePlayers =>
        _bridge.ClientManager.GetGameClients(inGame: true)
            .Where(c => !_eliminated.Contains((ulong)c.SteamId))
            .ToList();

    public IGameClient? GetPlayer(ulong steamId)
        => _bridge.ClientManager.GetGameClient((SteamID)steamId);

    public float Now => _bridge.ModSharp.GetGlobals().CurTime;

    // ── Arena helpers ─────────────────────────────────────────────────────────

    public Vector GetArenaCenter()
    {
        var centroid = GetSpawnCentroid();

        // Best: a random WALKABLE nav-area near the map center → a roaming hill that's never in a
        // wall/void, on any map. Nav returns null if unavailable (gamedata missing / game updated).
        if (_nav.RandomReachablePoint(centroid, 1500f) is { } navPoint)
            return navPoint;

        // Fallback: a bombsite (designed contested zone). Last resort: the raw spawn centroid.
        var sites = new List<Vector>();
        IBaseEntity? b = null;
        while ((b = _bridge.EntityManager.FindEntityByClassname(b, "func_bomb_target")) is not null)
            sites.Add(b.GetCenter());
        return sites.Count > 0 ? sites[Rng.Next(sites.Count)] : centroid;
    }

    public Vector GetSpawnCentroid()
    {
        Vector sum = new(0, 0, 0);
        var count = 0;
        foreach (var cls in new[] { "info_player_terrorist", "info_player_counterterrorist" })
        {
            IBaseEntity? e = null;
            while ((e = _bridge.EntityManager.FindEntityByClassname(e, cls)) is not null)
            {
                var o = e.GetAbsOrigin();
                sum = new Vector(sum.X + o.X, sum.Y + o.Y, sum.Z + o.Z);
                count++;
            }
        }
        return count == 0 ? new Vector(0, 0, 0) : new Vector(sum.X / count, sum.Y / count, sum.Z / count);
    }

    public void RespawnAll()
    {
        foreach (var c in _bridge.ClientManager.GetGameClients(inGame: true))
            if (c.GetPlayerController() is { IsValidEntity: true } ctrl)
                ctrl.Respawn();
    }

    public bool TryGetOrigin(IGameClient client, out Vector origin)
    {
        origin = default;
        var pawn = client.GetPlayerController()?.GetPlayerPawn();
        if (pawn is not { IsValidEntity: true, IsAlive: true }) return false;
        origin = pawn.GetAbsOrigin();
        return true;
    }

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
            if (!c.IsFakeClient && c.GetPlayerController() is { IsValidEntity: true } ctrl)
                ctrl.EmitSoundClient(soundEvent);
    }

    public bool TeleportSafe(IGameClient client, Vector position, Vector? angles = null)
    {
        // ponytail: direct teleport for now. The SuperPowers EntityPlacementTest + DropToGround port
        // (wall/void/telefrag safety) lands when a challenge actually needs precise placement.
        var pawn = client.GetPlayerController()?.GetPlayerPawn();
        if (pawn is not { IsValidEntity: true, IsAlive: true }) return false;
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
