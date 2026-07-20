using System;
using System.Collections.Generic;
using System.Linq;
using ChallengeEngine.Shared;

namespace ChallengeEngine.Challenges;

/// <summary>
/// The Purge — free-for-all frag fest. Everyone spawns armed, kills = points, victims respawn to keep
/// the action going, most frags at the timer wins. Pure OnKill scoring; map-agnostic.
/// </summary>
internal sealed class ThePurge : IChallenge
{
    public string Id             => "purge";
    public string DisplayNameKey => "challenge.purge.name";
    public int    MinPlayers     => 2;
    public int    RoundSeconds   => 75;
    public ChallengeWinRule WinRule => ChallengeWinRule.MostObjective;

    private sealed class State
    {
        public readonly Dictionary<ulong, int> Kills = new();
    }

    public void StartRound(IRoundContext ctx)
    {
        ctx.RespawnAll();
        ctx.Scratch["purge"] = new State();
        foreach (var c in ctx.AlivePlayers)
            ctx.GiveWeapon((ulong)c.SteamId, "weapon_ak47");
        ctx.CenterAll("THE PURGE — every player for themselves. Frag!");
    }

    public void OnKill(IRoundContext ctx, ulong victim, ulong? attacker)
    {
        if (ctx.Scratch.TryGetValue("purge", out var raw) && raw is State st && attacker is { } a)
            st.Kills[a] = st.Kills.GetValueOrDefault(a) + 1;

        // Keep the fest going: respawn the victim + re-arm shortly after.
        ctx.Respawn(victim);
        ctx.GiveWeapon(victim, "weapon_ak47");
    }

    public RoundResult ForceEnd(IRoundContext ctx)
    {
        if (!ctx.Scratch.TryGetValue("purge", out var raw) || raw is not State st || st.Kills.Count == 0)
            return new RoundResult([], null);

        var scores = st.Kills.Select(kv => new PlayerScore(kv.Key, kv.Value * 100)).ToList();
        var winner = st.Kills.MaxBy(kv => kv.Value).Key;
        return new RoundResult(scores, winner);
    }

    public LeaveReaction OnPlayerLeft(IRoundContext ctx, ulong steamId) => LeaveReaction.Continue;
}
