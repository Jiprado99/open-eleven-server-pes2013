// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Competitions;

/// <summary>A player's final standing (enum 0x16 compe_result) and the prize it pays (index in prize_tournament_list, -1 none).</summary>
public sealed record CompetitionResult(string Result, int Place, int PrizeIndex)
{
    public static readonly CompetitionResult None = new("NON", 0, -1);
}

/// <summary>
/// The playoff bracket. pes2013.exe draws 16 places in four rounds (CMD_GET_COMPLG_TOURNAMENT:
/// player_list of up to 16, game_result_1..4 of 8, 4, 2 and 1 matches, FUN_005bf5f0); fewer
/// qualifiers leave the places of the lowest seeds empty, so the top seeds get byes.
/// </summary>
public static class CompetitionBracket
{
    public const int Places = 16, Rounds = 4;

    /// <summary>Matches in a round: 8, 4, 2, 1.</summary>
    public static int Matches(int round) => Places >> round;

    /// <summary>
    /// Seeds by place, two places per first-round match: 1 and 2 can only meet in the final, 1-4
    /// in the semi-finals, 1-8 in the quarter-finals.
    /// </summary>
    private static readonly int[] SeedOrder = [1, 16, 8, 9, 4, 13, 5, 12, 2, 15, 7, 10, 3, 14, 6, 11];

    /// <summary>The bracket of a competition, by round and slot.</summary>
    public static async Task<List<CompetitionPlayoffMatch>> LoadAsync(GameDbContext db, int compeId, CancellationToken ct)
        => await db.CompetitionPlayoffMatches.Where(m => m.CompeId == compeId)
            .OrderBy(m => m.Round).ThenBy(m => m.Slot).ToListAsync(ct);

    /// <summary>
    /// Draws the bracket from the qualifiers in seed order (best first), each with the team they
    /// picked, and settles the byes. The caller saves.
    /// </summary>
    public static List<CompetitionPlayoffMatch> Draw(GameDbContext db, int compeId, IReadOnlyList<Player> seeds)
    {
        Player? Seed(int s) => s <= seeds.Count ? seeds[s - 1] : null;
        var bracket = new List<CompetitionPlayoffMatch>();
        for (var round = 1; round <= Rounds; round++)
            for (var slot = 0; slot < Matches(round); slot++)
            {
                var m = new CompetitionPlayoffMatch { CompeId = compeId, Round = round, Slot = slot };
                if (round == 1)
                {
                    var (home, away) = (Seed(SeedOrder[2 * slot]), Seed(SeedOrder[2 * slot + 1]));
                    (m.HomePid, m.HomeTeamId) = (home?.Pid ?? 0, home?.CompetitionTeamId ?? 0);
                    (m.AwayPid, m.AwayTeamId) = (away?.Pid ?? 0, away?.CompetitionTeamId ?? 0);
                }

                bracket.Add(m);
                db.CompetitionPlayoffMatches.Add(m);
            }

        SettleByes(bracket);
        return bracket;
    }

    public static CompetitionPlayoffMatch? At(IEnumerable<CompetitionPlayoffMatch> bracket, int round, int slot)
        => bracket.FirstOrDefault(m => m.Round == round && m.Slot == slot);

    /// <summary>Both sides are known: the matches feeding this one are decided (round 1 has none).</summary>
    public static bool Ready(IReadOnlyList<CompetitionPlayoffMatch> bracket, CompetitionPlayoffMatch m)
        => m.Round == 1 || (At(bracket, m.Round - 1, 2 * m.Slot)?.Finished != false && At(bracket, m.Round - 1, 2 * m.Slot + 1)?.Finished != false);

    /// <summary>A ready match with a side missing goes to the other side (or to nobody) without being played.</summary>
    public static void SettleByes(IReadOnlyList<CompetitionPlayoffMatch> bracket)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var m in bracket.Where(m => !m.Finished && (m.HomePid == 0 || m.AwayPid == 0) && Ready(bracket, m)).ToList())
            {
                Finish(bracket, m, m.HomePid != 0 ? m.HomePid : m.AwayPid, "NORMAL_END");
                m.Notified = true; // nothing was played: nobody to tell
                changed = true;
            }
        }
        while (changed);
    }

    /// <summary>Decides a match and moves the winner into the next round.</summary>
    public static void Finish(IReadOnlyList<CompetitionPlayoffMatch> bracket, CompetitionPlayoffMatch m, int winPid, string endKind)
    {
        m.Finished = true;
        m.WinPid = winPid;
        m.EndKind = endKind;
        if (m.Round >= Rounds || At(bracket, m.Round + 1, m.Slot / 2) is not { } next)
            return;

        var teamId = winPid == m.HomePid ? m.HomeTeamId : winPid == m.AwayPid ? m.AwayTeamId : 0;
        if (m.Slot % 2 == 0)
            (next.HomePid, next.HomeTeamId) = (winPid, teamId);
        else
            (next.AwayPid, next.AwayTeamId) = (winPid, teamId);
    }

    /// <summary>The round being played: the first with an undecided match (0 when all are decided).</summary>
    public static int CurrentRound(IReadOnlyList<CompetitionPlayoffMatch> bracket)
        => bracket.Where(m => !m.Finished).Select(m => m.Round).DefaultIfEmpty(0).Min();

    /// <summary>The undecided match of <paramref name="pid"/> in the round being played.</summary>
    public static CompetitionPlayoffMatch? MatchOf(IReadOnlyList<CompetitionPlayoffMatch> bracket, int pid)
    {
        var round = CurrentRound(bracket);
        return bracket.FirstOrDefault(m => m.Round == round && !m.Finished && pid != 0 && (m.HomePid == pid || m.AwayPid == pid));
    }

    /// <summary>The final's winner (0 until decided).</summary>
    public static int Champion(IReadOnlyList<CompetitionPlayoffMatch> bracket)
        => At(bracket, Rounds, 0) is { Finished: true } final ? final.WinPid : 0;

    /// <summary>
    /// Where a player finished: the champion, the finalist, the semi- and quarter-finalists and the
    /// rest of the bracket; a qualifier that is not in it got nothing.
    /// </summary>
    public static CompetitionResult ResultOf(IReadOnlyList<CompetitionPlayoffMatch> bracket, int pid)
    {
        if (pid == 0 || !bracket.Any(m => m.HomePid == pid || m.AwayPid == pid))
            return CompetitionResult.None;
        if (Champion(bracket) == pid)
            return new("VICTORY", 1, 0);

        // The round the player went out in; still in it means no result yet.
        var lost = bracket.Where(m => m.Finished && m.WinPid != pid && (m.HomePid == pid || m.AwayPid == pid))
            .Select(m => m.Round).DefaultIfEmpty(0).Min();
        return lost switch
        {
            4 => new("SECOND_PLACE", 2, 1),
            3 => new("BEST_4", 3, 2),
            2 => new("BEST_8", 5, 3),
            1 => new("FINAL_ADVANCEMENT", 9, 4),
            _ => CompetitionResult.None,
        };
    }

    /// <summary>
    /// Records the result of a played playoff match. A void game (P2P lost, nobody at fault) leaves
    /// the match open for another try. A draw goes to the shoot-out, or to the higher seed (the home
    /// side) when there was none. The caller saves.
    /// </summary>
    public static async Task RecordAsync(GameDbContext db, int playoffMatchId, (int Home, int Away) lineup, MatchRecord record,
        double? lineupHomeScore, int homePk, int awayPk)
    {
        var match = await db.CompetitionPlayoffMatches.FirstOrDefaultAsync(m => m.Id == playoffMatchId);
        if (match is null || match.Finished || lineupHomeScore is not { } score)
            return;

        // The room's sides are the bracket's unless the away player was put on the home side.
        var swapped = lineup.Home == match.AwayPid;
        var bracketHomeScore = swapped ? 1 - score : score;
        (match.HomeScore, match.AwayScore) = swapped ? (record.AwayScore, record.HomeScore) : (record.HomeScore, record.AwayScore);
        (match.HomePeriods, match.AwayPeriods) = swapped ? (record.AwayPeriods, record.HomePeriods) : (record.HomePeriods, record.AwayPeriods);
        match.MatchRecordId = record.Id;
        match.Penalties = homePk + awayPk > 0;
        match.Draw = bracketHomeScore == 0.5;
        var winner = bracketHomeScore >= 0.5 ? match.HomePid : match.AwayPid;

        // Tracked: the bracket holds this same match object.
        var bracket = await LoadAsync(db, match.CompeId, CancellationToken.None);
        Finish(bracket, match, winner, "NORMAL_END");
        SettleByes(bracket);
    }

    /// <summary>Unix seconds now.</summary>
    internal static long Now(DateTime now) => RankSystem.Unix(now);
}
