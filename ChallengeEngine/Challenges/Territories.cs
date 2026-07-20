using System;
using System.Collections.Generic;
using System.Linq;
using ChallengeEngine.Shared;
using Sharp.Shared.Types;

namespace ChallengeEngine.Challenges;

/// <summary>
/// Territories — three open zones spawned around the map; standing in ANY of them banks hold-time
/// (multiple players can score at once, in different zones). Most hold at the timer wins. Position-
/// based like KotH but rewards spreading out and contesting several points. Map-agnostic.
/// </summary>
internal sealed class Territories : IChallenge
{
    private const float Half   = 100f;
    private const float ZBelow = 60f;
    private const float ZAbove = 100f;
    private const int   ZoneCount = 3;

    public string Id             => "territories";
    public string DisplayNameKey => "challenge.territories.name";
    public int    MinPlayers     => 2;
    public int    RoundSeconds   => 90;
    public ChallengeWinRule WinRule => ChallengeWinRule.MostObjective;

    private sealed class State
    {
        public readonly List<Vector> Zones = new();
        public float Last;
        public readonly Dictionary<ulong, float> Hold = new();
    }

    public void StartRound(IRoundContext ctx)
    {
        ctx.RespawnAll();
        var st = new State { Last = ctx.Now };
        for (var i = 0; i < ZoneCount; i++)
            st.Zones.Add(ctx.GetArenaCenter()); // top-K-random nav areas → spread-out, walkable zones
        ctx.Scratch["terr"] = st;
        ctx.CenterAll("TERRITORIES — hold ANY zone for points!");
    }

    public void Tick(IRoundContext ctx)
    {
        if (!ctx.Scratch.TryGetValue("terr", out var raw) || raw is not State st) return;

        var now = ctx.Now;
        var dt  = Math.Max(0f, now - st.Last);
        st.Last = now;

        foreach (var c in ctx.AlivePlayers)
        {
            if (!ctx.TryGetOrigin(c, out var o)) continue;
            if (st.Zones.Any(z => Inside(o, z)))
            {
                var id = (ulong)c.SteamId;
                st.Hold[id] = st.Hold.GetValueOrDefault(id) + dt;
            }
        }

        if (st.Hold.Count > 0)
        {
            var leader = st.Hold.MaxBy(kv => kv.Value);
            ctx.CenterAll($"TERRITORIES — leader: {ctx.GetPlayer(leader.Key)?.Name ?? "—"} ({leader.Value:0.0}s)");
        }
    }

    public RoundResult ForceEnd(IRoundContext ctx)
    {
        if (!ctx.Scratch.TryGetValue("terr", out var raw) || raw is not State st || st.Hold.Count == 0)
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
