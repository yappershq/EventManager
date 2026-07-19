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

    /// <summary>In-game humans still in play (fake clients + eliminated excluded).</summary>
    IReadOnlyList<IGameClient> AlivePlayers { get; }

    /// <summary>Re-resolve a client by SteamID (never store the reference across callbacks).</summary>
    IGameClient? GetPlayer(ulong steamId);

    /// <summary>Per-heat scratch state, discarded when the heat ends.</summary>
    IDictionary<string, object> Scratch { get; }

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
