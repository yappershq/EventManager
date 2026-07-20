using System;
using System.Collections.Generic;
using System.Linq;
using ChallengeEngine.Shared;

namespace ChallengeEngine.Challenges;

/// <summary>
/// Gun Game — a kill advances you up a weapon ladder; each player carries only their current tier's
/// gun (strip + give on every advance). A kill on the final (knife) tier wins the heat instantly;
/// otherwise the highest tier at the timer wins. Frag-based (OnKill); map-agnostic.
/// </summary>
internal sealed class GunGame : IChallenge
{
    private static readonly string[] Ladder =
    {
        "weapon_ak47", "weapon_m4a1", "weapon_awp", "weapon_deagle",
        "weapon_usp_silencer", "weapon_glock", "weapon_knife",
    };

    public string Id             => "gungame";
    public string DisplayNameKey => "challenge.gungame.name";
    public int    MinPlayers     => 2;
    public int    RoundSeconds   => 150;
    public ChallengeWinRule WinRule => ChallengeWinRule.FirstToScore;

    private sealed class State
    {
        public readonly Dictionary<ulong, int> Tier = new();
    }

    public void StartRound(IRoundContext ctx)
    {
        ctx.RespawnAll();
        var st = new State();
        ctx.Scratch["gg"] = st;
        foreach (var c in ctx.AlivePlayers)
        {
            var id = (ulong)c.SteamId;
            st.Tier[id] = 0;
            Equip(ctx, id, 0);
        }
        ctx.CenterAll("GUN GAME — a kill advances your weapon. Knife kill wins!");
    }

    public void OnKill(IRoundContext ctx, ulong victim, ulong? attacker)
    {
        if (!ctx.Scratch.TryGetValue("gg", out var raw) || raw is not State st) return;

        if (attacker is { } a && a != victim)
        {
            var tier = st.Tier.GetValueOrDefault(a);
            if (tier >= Ladder.Length - 1) // a knife-tier kill wins outright
            {
                ctx.EndRound(new RoundResult(new[] { new PlayerScore(a, 1000) }, a));
                return;
            }
            tier++;
            st.Tier[a] = tier;
            Equip(ctx, a, tier);
        }

        // Respawn the victim back at their own tier.
        ctx.Respawn(victim);
        Equip(ctx, victim, st.Tier.GetValueOrDefault(victim));
    }

    public RoundResult ForceEnd(IRoundContext ctx)
    {
        if (!ctx.Scratch.TryGetValue("gg", out var raw) || raw is not State st || st.Tier.Count == 0)
            return new RoundResult([], null);

        // No knife-winner by the timer → furthest up the ladder wins; points scale with tier reached.
        var scores = st.Tier.Select(kv => new PlayerScore(kv.Key, (kv.Value + 1) * 100)).ToList();
        var winner = st.Tier.MaxBy(kv => kv.Value).Key;
        return new RoundResult(scores, winner);
    }

    public LeaveReaction OnPlayerLeft(IRoundContext ctx, ulong steamId) => LeaveReaction.Continue;

    private static void Equip(IRoundContext ctx, ulong steamId, int tier)
    {
        ctx.StripWeapons(steamId);
        ctx.GiveWeapon(steamId, Ladder[Math.Clamp(tier, 0, Ladder.Length - 1)]);
    }
}
