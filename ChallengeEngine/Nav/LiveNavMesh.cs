using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Utilities;
using SVector = Sharp.Shared.Types.Vector;

namespace ChallengeEngine.Nav;

/// <summary>
/// Reads CS2's baked nav mesh from the live engine to pick guaranteed-walkable points (the hill).
/// Lifted from MonsterMod (live-verified: 77-waypoint A* across de_dust2) — trimmed to just what a
/// challenge needs: nearest-area + a random reachable point. Every pointer deref is bounds-gated, so a
/// wrong offset returns null and the caller falls back rather than segfaulting.
///
/// Gamedata: TheNavMesh global + CNavArea corner offsets ship in challengeengine.games.jsonc;
/// GetNearestNavArea is already signed by ModSharp core. Nav simply stays disabled (→ fallback) if the
/// gamedata isn't deployed or the game build shifted the sig — see docs/CHALLENGE_ENGINE.md.
/// </summary>
internal sealed unsafe class LiveNavMesh(ISharedSystem shared, ILogger<LiveNavMesh> log)
{
    private nint _theNavMeshAddr;
    private int  _nwCorner, _seCorner, _mConnect;
    private nint _criteria;
    private IPhysicsQueryManager? _physics;
    private readonly Random _rng = new();

    // Hits world geometry, player-clip, AND physics props — so a hull trace catches box crates
    // sitting in an otherwise-"open" nav area.
    private const InteractionLayers WorldMask =
        InteractionLayers.Solid | InteractionLayers.Sky | InteractionLayers.PlayerClip
        | InteractionLayers.WorldGeometry | InteractionLayers.PhysicsProp;

    // Standing player hull — the space the hill must actually be clear for.
    private static readonly Sharp.Shared.Types.TraceShapeHull StandHull = new()
    {
        Mins = new SVector(-16f, -16f, 0f),
        Maxs = new SVector(16f, 16f, 72f),
    };

    // CNavMesh::GetNearestNavArea(this, const Vector* pos, const int* layer, uint flags,
    //   Vector* outClosest, NavSearchInfo_t* criteria, float maxDist) -> CNavArea*.
    private delegate* unmanaged<nint, SVector*, int*, uint, nint, nint, float, nint> _getNearestArea;

    public bool Ready { get; private set; }

    public bool Init()
    {
        _physics = shared.GetPhysicsQueryManager();

        var gd = shared.GetModSharp().GetGameData();
        try { gd.Register("challengeengine.games"); }
        catch (Exception ex)
        {
            log.LogWarning("[Nav] gamedata not loaded ({M}) — nav hill disabled, falls back to bombsite/centroid.", ex.Message);
            return false;
        }

        if (!gd.GetAddress("CNavMesh::GetNearestNavArea", out var fn) || fn == nint.Zero)
        {
            log.LogWarning("[Nav] GetNearestNavArea not resolved (core gamedata) — nav hill disabled.");
            return false;
        }
        _getNearestArea = (delegate* unmanaged<nint, SVector*, int*, uint, nint, nint, float, nint>)fn;

        if (!gd.GetAddress("CNavMesh::TheNavMesh", out _theNavMeshAddr) || _theNavMeshAddr == nint.Zero ||
            !gd.GetOffset("CNavArea::m_nwCorner", out _nwCorner) ||
            !gd.GetOffset("CNavArea::m_seCorner", out _seCorner) ||
            !gd.GetOffset("CNavArea::m_connect",  out _mConnect))
        {
            log.LogWarning("[Nav] TheNavMesh/CNavArea offsets not resolved — nav hill disabled.");
            return false;
        }

        // NavSearchInfo_t filter for GetNearestNavArea (r9): generous tolerances + include-ALL attribute
        // mask (+0x20 = ~0), else the engine's accept test rejects every area and returns null.
        _criteria = (nint)NativeMemory.AllocZeroed(0x48);
        *(float*)(_criteria + 0x10) = 16384f;
        *(float*)(_criteria + 0x14) = 16384f;
        *(float*)(_criteria + 0x18) = 16384f;
        *(ulong*)(_criteria + 0x20) = 0xFFFFFFFFFFFFFFFFUL;

        Ready = true;
        log.LogInformation("[Nav] live nav ready — walkable hill placement enabled.");
        return true;
    }

    private static bool IsPlausible(nint p) => (ulong)p is > 0x10000UL and < 0x0000_8000_0000_0000UL;

    private nint NavMesh() => _theNavMeshAddr == nint.Zero ? nint.Zero : (nint)_theNavMeshAddr.GetInt64(0);

    private nint GetArea(SVector pos)
    {
        var mesh = NavMesh();
        if (!Ready || !IsPlausible(mesh) || _criteria == nint.Zero) return nint.Zero;

        var layer = 0;
        var area  = _getNearestArea(mesh, &pos, &layer, 0u, nint.Zero, _criteria, 10000f);
        return IsPlausible(area) ? area : nint.Zero;
    }

    private Vector3 Lo(nint a) => new(a.GetFloat(_nwCorner), a.GetFloat(_nwCorner + 4), a.GetFloat(_nwCorner + 8));
    private Vector3 Hi(nint a) => new(a.GetFloat(_seCorner), a.GetFloat(_seCorner + 4), a.GetFloat(_seCorner + 8));
    private Vector3 Center(nint a) => (Lo(a) + Hi(a)) * 0.5f;

    private IEnumerable<nint> Neighbors(nint area)
    {
        for (var dir = 0; dir < 4; dir++)
        {
            var list = (nint)area.GetInt64(_mConnect + dir * 8);
            if (!IsPlausible(list)) continue;

            var count = list.GetInt32(0);
            if (count is <= 0 or > 64) continue;

            for (var i = 0; i < count; i++)
            {
                var n = (nint)list.GetInt64(8 + i * 16);
                if (IsPlausible(n)) yield return n;
            }
        }
    }

    /// <summary>
    /// Center of one of the map's MOST OPEN walkable areas — a proper KotH hill (a plaza/mid, not a
    /// corridor or a cramped nook). Flood-fills the whole connected nav graph from <paramref name="seed"/>,
    /// scores each area by local openness (its own footprint + its neighbours' — so a tile in the middle
    /// of a big open space wins over an isolated large-but-walled one), then picks randomly among the
    /// top few for variety. Null if nav isn't ready or there's no area under the seed.
    /// </summary>
    public SVector? MostOpenPoint(SVector seed)
    {
        if (!Ready) return null;

        var start = GetArea(seed);
        if (start == nint.Zero) return null;

        var visited = new HashSet<nint> { start };
        var queue   = new Queue<nint>();
        queue.Enqueue(start);

        var scored = new List<(nint area, float score)>();
        while (queue.Count > 0 && scored.Count < 20000) // cap: a real CS2 map is a few thousand areas
        {
            var a = queue.Dequeue();
            var score = Footprint(a);
            foreach (var n in Neighbors(a))
            {
                score += Footprint(n); // local openness = own area + surrounding areas
                if (visited.Add(n)) queue.Enqueue(n);
            }
            scored.Add((a, score));
        }

        if (scored.Count == 0) return null;

        scored.Sort((x, y) => y.score.CompareTo(x.score));

        // Among the top-K most-open areas, keep only those actually CLEAR of geometry/box props at the
        // center (nav openness is floor area — a crate stack could still sit in it). Random among the
        // clear ones → open, unobstructed, AND varied. If none pass, fall back to the most-open area.
        var topK  = Math.Min(12, scored.Count);
        var clear = new List<nint>();
        for (var i = 0; i < topK; i++)
            if (IsClear(Center(scored[i].area)))
                clear.Add(scored[i].area);

        var chosen = clear.Count > 0 ? clear[_rng.Next(clear.Count)] : scored[0].area;
        var c = Center(chosen);
        return new SVector(c.X, c.Y, c.Z);
    }

    /// <summary>True if a standing player hull at this point doesn't overlap world geometry or a box prop.</summary>
    private bool IsClear(Vector3 center)
    {
        if (_physics is null) return true; // can't test → accept (nav already prefers walkable floor)

        var at  = new SVector(center.X, center.Y, center.Z + 2f); // nudge off the floor to avoid a floor-touch hit
        var ray = new Sharp.Shared.Types.TraceShapeRay(StandHull);
        return !_physics.TraceShape(ray, at, at, WorldMask, CollisionGroupType.Default, TraceQueryFlag.All).DidHit();
    }

    private float Footprint(nint a)
    {
        var lo = Lo(a);
        var hi = Hi(a);
        return MathF.Abs(hi.X - lo.X) * MathF.Abs(hi.Y - lo.Y);
    }
}
