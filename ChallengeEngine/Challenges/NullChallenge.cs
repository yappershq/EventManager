using System.Collections.Generic;
using System.Linq;
using ChallengeEngine.Shared;

namespace ChallengeEngine.Challenges;

/// <summary>
/// Smoke-test challenge: no gameplay, just awards points to whoever's alive and ends on the engine's
/// round timer, so the FSM (loop → escalate → finale → crown) can be verified with bots before any
/// real challenge exists. Not shipped to players.
/// </summary>
internal sealed class NullChallenge : IChallenge
{
    public string Id             => "null";
    public string DisplayNameKey => "challenge.null.name";
    public int    MinPlayers     => 1;
    public int    RoundSeconds   => 15;
    public ChallengeWinRule WinRule => ChallengeWinRule.MostObjective;

    public void StartRound(IRoundContext ctx)
        => ctx.CenterAll("Test heat"); // ponytail: NullChallenge is dev-only; real challenges localize.

    public RoundResult ForceEnd(IRoundContext ctx)
    {
        var alive  = ctx.AlivePlayers;
        var scores = alive.Select((c, i) => new PlayerScore((ulong)c.SteamId, 10 + (alive.Count - i))).ToList();
        var winner = alive.Count > 0 ? (ulong)alive[0].SteamId : (ulong?)null;
        return new RoundResult(scores, winner);
    }

    public LeaveReaction OnPlayerLeft(IRoundContext ctx, ulong steamId) => LeaveReaction.Continue;
}
