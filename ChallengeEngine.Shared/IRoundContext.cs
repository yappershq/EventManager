using System.Collections.Generic;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace ChallengeEngine.Shared;

/// <summary>
/// The engine-provided surface for one heat — the ONLY thing a challenge touches. Every unsafe bit
/// (safe teleport, handle-tracked entities, HUD, sound) is implemented ONCE, correctly, behind this
/// interface, so no challenge grabs a raw engine API and the safety fixes live in one place.
/// </summary>
public interface IRoundContext
{
    int RoundNumber { get; }
    int Phase { get; }
    IReadOnlyCollection<string> Modifiers { get; }

    /// <summary>Game time in seconds (<c>GetGlobals().CurTime</c>) — use for per-tick deltas, not wall clock.</summary>
    float Now { get; }

    /// <summary>In-game players still in play (fake clients OK for testing; eliminated excluded).</summary>
    IReadOnlyList<IGameClient> AlivePlayers { get; }

    /// <summary>Re-resolve a client by SteamID (never store the reference across callbacks).</summary>
    IGameClient? GetPlayer(ulong steamId);

    /// <summary>Per-heat scratch state, discarded when the heat ends.</summary>
    IDictionary<string, object> Scratch { get; }

    // ── Arena helpers (map-agnostic) ──────────────────────────────────────────

    /// <summary>A designed, playable arena center — a random bombsite center, or the spawn centroid on
    /// non-bomb maps. Prefer this over a raw centroid: bombsites are never a wall/void.</summary>
    Vector GetArenaCenter();

    /// <summary>Centroid of the map's spawn points — the fallback "center of the arena".</summary>
    Vector GetSpawnCentroid();

    /// <summary>Respawn every in-game player (heats loop under one long native round).</summary>
    void RespawnAll();

    /// <summary>Current world origin of a player's pawn. False if not alive/spawned.</summary>
    bool TryGetOrigin(IGameClient client, out Vector origin);

    /// <summary>Respawn one player (frag challenges keep the action going).</summary>
    void Respawn(ulong steamId);

    /// <summary>Kill a player's pawn (elimination challenges).</summary>
    void Slay(ulong steamId, bool explode = false);

    /// <summary>Give a player a weapon by classname (e.g. "weapon_ak47").</summary>
    void GiveWeapon(ulong steamId, string weapon);

    // ── Heat control ────────────────────────────────────────────────────────
    void EndRound(RoundResult result);
    void Eliminate(ulong steamId, string? reason = null);
    void AwardPoints(ulong steamId, int points, string? note = null);

    // ── Safe helpers ────────────────────────────────────────────────────────
    void CenterAll(string text);
    void Center(IGameClient client, string text);
    void PlaySoundAll(string soundEvent);

    /// <summary>Placement-validated teleport (avoids walls/void/telefrag). False = no safe spot found.</summary>
    bool TeleportSafe(IGameClient client, Vector position, Vector? angles = null);

    /// <summary>Spawn a marker entity (full precache pipeline); track it by the returned token.</summary>
    EntityToken SpawnMarker(string classname, Vector position, IReadOnlyDictionary<string, string>? keyValues = null);

    /// <summary>Remove a previously-spawned marker (handle-validated — safe even after recycling).</summary>
    void RemoveEntity(EntityToken token);
}
