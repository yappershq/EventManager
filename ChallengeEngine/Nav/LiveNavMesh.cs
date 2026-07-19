using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
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
    private readonly Random _rng = new();

    // CNavMesh::GetNearestNavArea(this, const Vector* pos, const int* layer, uint flags,
    //   Vector* outClosest, NavSearchInfo_t* criteria, float maxDist) -> CNavArea*.
    private delegate* unmanaged<nint, SVector*, int*, uint, nint, nint, float, nint> _getNearestArea;

    public bool Ready { get; private set; }

    public bool Init()
    {
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

    /// <summary>A random walkable nav-area center within <paramref name="maxDist"/> (XY) of
    /// <paramref name="from"/> — a guaranteed-reachable, roaming hill spot. Null if nav isn't ready or
    /// there's no area under the point.</summary>
    public SVector? RandomReachablePoint(SVector from, float maxDist)
    {
        if (!Ready) return null;

        var start = GetArea(from);
        if (start == nint.Zero) return null;

        var candidates = new List<Vector3>();
        var fromV3 = new Vector3(from.X, from.Y, from.Z);
        foreach (var n1 in Neighbors(start))
        {
            var c1 = Center(n1);
            if (WithinXY(fromV3, c1, maxDist)) candidates.Add(c1);
            foreach (var n2 in Neighbors(n1))
            {
                if (n2 == start) continue;
                var c2 = Center(n2);
                if (WithinXY(fromV3, c2, maxDist)) candidates.Add(c2);
            }
        }

        var pick = candidates.Count > 0 ? candidates[_rng.Next(candidates.Count)] : Center(start);
        return new SVector(pick.X, pick.Y, pick.Z);
    }

    private static bool WithinXY(Vector3 a, Vector3 b, float max)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy <= max * max;
    }
}
