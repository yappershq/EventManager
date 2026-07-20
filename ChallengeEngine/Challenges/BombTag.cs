using System;
using System.Collections.Generic;
using System.Linq;
using ChallengeEngine.Shared;
using Sharp.Shared.Types;

namespace ChallengeEngine.Challenges;

/// <summary>
/// Bomb Tag (hot potato) — one player holds the bomb and passes it by tagging (getting close to)
/// another. Every fuse segment the current holder is blown up and out; you score for every second
/// you're NOT holding it. Last player standing (or most survival time at the timer) wins. Uses Tick
/// (proximity + fuse) + Slay/Eliminate; map-agnostic.
/// </summary>
internal sealed class BombTag : IChallenge
{
    private const float PassRange     = 150f;  // tag radius
    private const float PassCooldown  = 1.5f;   // stops instant bounce-back
    private const float SegmentSeconds = 18f;   // fuse per holder

    public string Id             => "bombtag";
    public string DisplayNameKey => "challenge.bombtag.name";
    public int    MinPlayers     => 3;
    public int    RoundSeconds   => 120;
    public ChallengeWinRule WinRule => ChallengeWinRule.LastAlive;

    private sealed class State
    {
        public ulong Holder;
        public float NextDetonate;
        public float LastPass;
        public float LastTick;
        public readonly Dictionary<ulong, float> Survival = new();
    }

    public void StartRound(IRoundContext ctx)
    {
        ctx.RespawnAll();
        var alive = ctx.AlivePlayers;
        var st = new State { LastTick = ctx.Now, NextDetonate = ctx.Now + SegmentSeconds };
        if (alive.Count > 0) st.Holder = (ulong)alive[Rng.Next(alive.Count)].SteamId;
        ctx.Scratch["bt"] = st;
        ctx.CenterAll("BOMB TAG — pass the bomb by tagging someone. Don't be holding it when it blows!");
    }

    public void Tick(IRoundContext ctx)
    {
        if (!ctx.Scratch.TryGetValue("bt", out var raw) || raw is not State st) return;

        var now = ctx.Now;
        var dt  = Math.Max(0f, now - st.LastTick);
        st.LastTick = now;

        var alive = ctx.AlivePlayers;
        if (alive.Count <= 1) { ctx.EndRound(Result(st)); return; }

        // Survival scoring: everyone NOT holding the bomb banks time.
        foreach (var c in alive)
        {
            var id = (ulong)c.SteamId;
            if (id != st.Holder) st.Survival[id] = st.Survival.GetValueOrDefault(id) + dt;
        }

        // Ensure the holder is still alive; if not, hand off to someone.
        if (!alive.Any(c => (ulong)c.SteamId == st.Holder))
            st.Holder = (ulong)alive[Rng.Next(alive.Count)].SteamId;

        // Pass by proximity.
        if (now - st.LastPass > PassCooldown
            && ctx.GetPlayer(st.Holder) is { } holderClient
            && ctx.TryGetOrigin(holderClient, out var ho))
        {
            foreach (var c in alive)
            {
                var id = (ulong)c.SteamId;
                if (id == st.Holder) continue;
                if (ctx.TryGetOrigin(c, out var o) && Dist2(ho, o) <= PassRange * PassRange)
                {
                    st.Holder   = id;
                    st.LastPass = now;
                    break;
                }
            }
        }

        // Fuse: blow up the current holder.
        if (now >= st.NextDetonate)
        {
            ctx.Slay(st.Holder, explode: true);
            ctx.Eliminate(st.Holder, "bomb");
            st.NextDetonate = now + SegmentSeconds;

            var remaining = ctx.AlivePlayers;
            if (remaining.Count <= 1) { ctx.EndRound(Result(st)); return; }
            st.Holder = (ulong)remaining[Rng.Next(remaining.Count)].SteamId;
        }

        var holderName = ctx.GetPlayer(st.Holder)?.Name ?? "—";
        var fuse = Math.Max(0, (int)(st.NextDetonate - now));
        ctx.CenterAll($"BOMB TAG — 💣 {holderName} has the bomb! ({fuse}s)");
    }

    public RoundResult ForceEnd(IRoundContext ctx)
        => ctx.Scratch.TryGetValue("bt", out var raw) && raw is State st ? Result(st) : new RoundResult([], null);

    public LeaveReaction OnPlayerLeft(IRoundContext ctx, ulong steamId)
    {
        // If the bomb-holder disconnects, the round self-heals on the next Tick (holder re-picked).
        return LeaveReaction.Continue;
    }

    private static RoundResult Result(State st)
    {
        if (st.Survival.Count == 0) return new RoundResult([], null);
        var scores = st.Survival.Select(kv => new PlayerScore(kv.Key, (int)Math.Round(kv.Value))).ToList();
        var winner = st.Survival.MaxBy(kv => kv.Value).Key;
        return new RoundResult(scores, winner);
    }

    private static float Dist2(Vector a, Vector b)
    {
        var dx = a.X - b.X; var dy = a.Y - b.Y; var dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static readonly Random Rng = new();
}
