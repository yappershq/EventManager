using System;
using System.Collections.Generic;
using System.Linq;
using ChallengeEngine.Shared;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace ChallengeEngine.Challenges;

/// <summary>
/// King of the Hill — the first real challenge. A "hill" is defined at the map's spawn centroid
/// (map-agnostic, no custom geometry). Each tick, the SOLE occupant of the hill accrues hold-time;
/// contested (≥2 inside) or empty → nobody scores. Hold-seconds = points, most hold wins the heat.
///
/// Phase 2: HUD is a plain center message. Phase 3 swaps to a localized HTML overlay + a visible
/// beam pillar (needs a live-verified schema endpoint set before it ships — see docs).
/// </summary>
internal sealed class KingOfTheHill : IChallenge
{
    private const float Half   = 90f;    // hill half-extent (world units)
    private const float ZBelow = 60f;
    private const float ZAbove = 100f;

    public string Id             => "koth";
    public string DisplayNameKey => "challenge.koth.name";
    public int    MinPlayers     => 2;
    public int    RoundSeconds   => 90;
    public ChallengeWinRule WinRule => ChallengeWinRule.MostObjective;

    private sealed class State
    {
        public Vector Center;
        public float  Last;
        public readonly Dictionary<ulong, float> Hold = new();
    }

    public void StartRound(IRoundContext ctx)
    {
        ctx.RespawnAll();
        ctx.Scratch["koth"] = new State { Center = ctx.GetSpawnCentroid(), Last = ctx.Now };
        // ponytail: literal center HUD in Phase 2; Phase 3 → localized HTML overlay + beam pillar.
        ctx.CenterAll("KING OF THE HILL — hold the center!");
    }

    public void Tick(IRoundContext ctx)
    {
        if (!ctx.Scratch.TryGetValue("koth", out var raw) || raw is not State st) return;

        var now = ctx.Now;
        var dt  = Math.Max(0f, now - st.Last);
        st.Last = now;

        var inside = new List<IGameClient>();
        foreach (var c in ctx.AlivePlayers)
            if (ctx.TryGetOrigin(c, out var o) && Inside(o, st.Center))
                inside.Add(c);

        // Sole occupant accrues; contested (≥2) or empty → nobody scores this tick.
        if (inside.Count == 1)
        {
            var id = (ulong)inside[0].SteamId;
            st.Hold[id] = st.Hold.GetValueOrDefault(id) + dt;
        }

        var leaderName = "—";
        var leaderTime = 0f;
        if (st.Hold.Count > 0)
        {
            var leader = st.Hold.MaxBy(kv => kv.Value);
            leaderTime = leader.Value;
            leaderName = ctx.GetPlayer(leader.Key)?.Name ?? "—";
        }
        var status = inside.Count switch { 0 => "OPEN", 1 => "HELD", _ => "CONTESTED" };
        ctx.CenterAll($"KING OF THE HILL [{status}]\nLeader: {leaderName} ({leaderTime:0.0}s)");
    }

    public RoundResult ForceEnd(IRoundContext ctx)
    {
        if (!ctx.Scratch.TryGetValue("koth", out var raw) || raw is not State st || st.Hold.Count == 0)
            return new RoundResult([], null);

        var scores = st.Hold.Select(kv => new PlayerScore(kv.Key, (int)Math.Round(kv.Value))).ToList();
        var winner = st.Hold.MaxBy(kv => kv.Value).Key;
        return new RoundResult(scores, winner);
    }

    public LeaveReaction OnPlayerLeft(IRoundContext ctx, ulong steamId) => LeaveReaction.Continue;

    private static bool Inside(Vector o, Vector c)
        => Math.Abs(o.X - c.X) <= Half && Math.Abs(o.Y - c.Y) <= Half
           && o.Z >= c.Z - ZBelow && o.Z <= c.Z + ZAbove;
}
