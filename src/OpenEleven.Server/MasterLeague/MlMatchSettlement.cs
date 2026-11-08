// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using OpenEleven.Data.Entities;

namespace OpenEleven.Server.MasterLeague;

/// <summary>
/// What a Master League Online match (quick match ML_RANK / ML_COMPE) does to both clubs, applied
/// once by MatchResults when the match is settled:
/// <list type="bullet">
/// <item>record, division points (3 / 1), division and an Elo rating of ML matches;</item>
/// <item>money: income by result (+ derby income when both share a base team) less the squad's
/// salaries and the cards (CMD_GET_ML_MATCHINCOME_INFO / CMD_GET_ML_MATCHEXPENSE show it);</item>
/// <item>squad: goals and assists from the match sheet, cards, new injuries, injuries healing,
/// recovery from tiredness, one match off every contract; a footballer whose contract ran out
/// leaves, unless the squad would drop below MinSquad.</item>
/// </list>
/// The footballers who played (games, tiredness) and the manager's ratings come afterwards from each
/// client (CMD_SEND_ML_GAMEPLAYERS, CMD_SET_ML_MYTEAM_PLAYER_RATING).
/// ponytail: the original service's rules are unknown; injuries last a fixed 2 matches and red
/// cards are only counted (no suspension).
/// </summary>
public static class MlMatchSettlement
{
    public const int InjuryMatches = 2;
    public const int RecoveryPerMatch = 25;
    private const int EloK = 32;

    public static bool IsMlMatch(string matchType) => matchType is "ML_RANK" or "ML_COMPE";

    /// <param name="homeScore">1 = home won, 0.5 = draw, 0 = away won.</param>
    /// <param name="events">The match sheet lines (GOAL|phase|side|pid|gameplayer|..., CARD|phase|side|kind|pid|gameplayer, INJURY|phase|side|gameplayer).</param>
    public static async Task SettleAsync(MlLeague league, (int Home, int Away) lineup, double homeScore,
        int homeGoals, int awayGoals, string events, CancellationToken ct = default)
    {
        var home = await league.TeamAsync(lineup.Home, ct);
        var away = await league.TeamAsync(lineup.Away, ct);
        if (home is null && away is null)
            return;

        var lines = events.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('|')).ToList();
        var derby = home is not null && away is not null && home.BaseTeamId == away.BaseTeamId;
        var (homeRating, awayRating) = (home?.Rating ?? 1000, away?.Rating ?? 1000);

        if (home is not null)
            await SettleClubAsync(league, home, "HOME", homeScore, awayRating, homeGoals, awayGoals, derby, lines, ct);
        if (away is not null)
            await SettleClubAsync(league, away, "AWAY", 1 - homeScore, homeRating, awayGoals, homeGoals, derby, lines, ct);
        await league.Db.SaveChangesAsync(ct);
    }

    private static async Task SettleClubAsync(MlLeague league, MlTeam team, string side, double score, int opponentRating,
        int goals, int against, bool derby, List<string[]> lines, CancellationToken ct)
    {
        var o = league.Rules.Options;
        switch (score)
        {
            case 1:
                team.Wins++;
                team.WinRun++;
                team.Points += 3;
                break;
            case 0.5:
                team.Draws++;
                team.WinRun = 0;
                team.Points += 1;
                break;
            default:
                team.Losses++;
                team.WinRun = 0;
                break;
        }

        var expected = 1 / (1 + Math.Pow(10, (opponentRating - team.Rating) / 400.0));
        team.Rating = Math.Max(0, team.Rating + (int)Math.Round(EloK * (score - expected)));
        team.Division = league.Rules.DivisionFor(team.Points);

        var squad = await league.SquadAsync(team.Pid, ct);
        var byId = squad.ToDictionary(p => p.GamePlayerId);

        foreach (var p in squad)
        {
            p.ContractTerm = Math.Max(0, p.ContractTerm - 1);
            p.InjuryMatches = Math.Max(0, p.InjuryMatches - 1);
            p.Tiredness = Math.Max(0, p.Tiredness - RecoveryPerMatch);
        }

        int yellow = 0, red = 0;
        foreach (var l in lines)
        {
            switch (l)
            {
                // GOAL|phase|HOME|pid|goal_gameplayer_id|goal_side|assist_side|assist_gameplayer_id|assist_player_side
                // An own goal (scorer of the other side) credits nobody here.
                case ["GOAL", _, var goalSide, _, var scorer, var scorerSide, ..] when goalSide == side:
                    if (scorerSide is "" || scorerSide == side)
                        if (int.TryParse(scorer, out var s) && byId.TryGetValue(s, out var sp))
                            sp.Goals++;
                    if (l.Length > 7 && (l.Length < 9 || l[8] is "" || l[8] == side)
                        && int.TryParse(l[7], out var a) && a != 0 && byId.TryGetValue(a, out var ap))
                        ap.Assists++;
                    break;
                case ["CARD", _, var cardSide, var kind, _, var who, ..] when cardSide == side:
                    var carded = int.TryParse(who, out var c) ? byId.GetValueOrDefault(c) : null;
                    if (kind == "RED")
                    {
                        red++;
                        if (carded is not null) carded.RedCards++;
                    }
                    else
                    {
                        yellow++;
                        if (carded is not null) carded.YellowCards++;
                    }

                    break;
                case ["INJURY", _, var injurySide, var hurt, ..] when injurySide == side:
                    if (int.TryParse(hurt, out var h) && byId.TryGetValue(h, out var hp))
                        hp.InjuryMatches = InjuryMatches;
                    break;
            }
        }

        var owners = await league.OwnersAsync(squad.Select(p => p.GamePlayerId), ct);
        var salaries = squad.Sum(p => league.Rules.Salary(league.Price(p.GamePlayerId, owners[p.GamePlayerId])));
        var income = score switch { 1 => o.WinIncome, 0.5 => o.DrawIncome, _ => o.LossIncome };
        var derbyIncome = derby ? o.DerbyIncome : 0;
        var expense = salaries + yellow * o.YellowCardCost + red * o.RedCardCost;

        team.LastIncome = income + derbyIncome;
        team.LastPrizeIncome = 0;
        team.LastDerbyIncome = derbyIncome;
        team.LastExpense = expense;
        team.LastYellowCards = yellow;
        team.LastRedCards = red;
        team.LastPurchases = 0;
        team.Funds += team.LastIncome - expense;
        team.UpdatedAt = league.Now;

        // Expired contracts: the footballer leaves while the squad can spare him.
        var size = squad.Count;
        foreach (var p in squad.Where(p => p.ContractTerm == 0).OrderBy(p => league.Catalog.Player(p.GamePlayerId).Overall))
        {
            if (size <= o.MinSquad)
                break;
            league.Db.MlTeamPlayers.Remove(p);
            size--;
        }
    }

    /// <summary>
    /// CMD_SEND_ML_GAMEPLAYERS: the footballers of the manager's own side who played. Each gets a
    /// game and tires (net +10 after the recovery the settlement already gave).
    /// </summary>
    public static async Task PlayedAsync(MlLeague league, int pid, IReadOnlyCollection<int> played, CancellationToken ct)
    {
        if (played.Count == 0)
            return;
        var squad = await league.Db.MlTeamPlayers.Where(p => p.Pid == pid && played.Contains(p.GamePlayerId)).ToListAsync(ct);
        foreach (var p in squad)
        {
            p.Games++;
            p.Tiredness = Math.Min(100, p.Tiredness + RecoveryPerMatch + 10);
        }

        await league.Db.SaveChangesAsync(ct);
    }
}
