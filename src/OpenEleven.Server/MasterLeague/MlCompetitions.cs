// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.MasterLeague;

/// <summary>
/// Master League competitions: the configured competitions with <see cref="CompetitionOptions.MasterLeague"/>.
/// Entry goes through CMD_JOIN_COMPLG like any competition (the entry is Players.CompetitionId);
/// matches are quick matches of type ML_COMPE, recorded with the competition's id.
/// </summary>
public static class MlCompetitions
{
    /// <summary>Whether the club is entered in a running ML competition (transfers are frozen: ERR_COMPE_ENTRY).</summary>
    public static async Task<bool> IsEnteredAsync(MlLeague league, int pid, CancellationToken ct)
    {
        var entered = await league.Db.Players.Where(p => p.Pid == pid).Select(p => p.CompetitionId).FirstOrDefaultAsync(ct);
        var now = DateTimeOffset.FromUnixTimeSeconds(league.Now).UtcDateTime;
        return Competitions.CompetitionCatalog.Find(league.Options, entered) is { MasterLeague: true } c && now >= c.EntryStart && now < c.End;
    }

    /// <summary>The final table of a competition: pids by points (3 / 1), then goal difference and goals.</summary>
    public static async Task<List<int>> TableAsync(MlLeague league, int compeId, CancellationToken ct)
    {
        var matches = await league.Db.Matches.AsNoTracking().Where(m => m.CompeId == compeId && m.PlayoffMatchId == 0 && m.Completed && !m.Disconnected).ToListAsync(ct);
        var entrants = await league.Db.Players.AsNoTracking().Where(p => p.CompetitionId == compeId).Select(p => p.Pid).ToListAsync(ct);
        var table = entrants.Concat(matches.SelectMany(m => new[] { m.HomePid, m.AwayPid })).Distinct()
            .ToDictionary(pid => pid, _ => (Points: 0, Diff: 0, Goals: 0));
        foreach (var m in matches)
        {
            void Add(int pid, int scored, int conceded)
            {
                var t = table[pid];
                table[pid] = (t.Points + (scored > conceded ? 3 : scored == conceded ? 1 : 0), t.Diff + scored - conceded, t.Goals + scored);
            }

            Add(m.HomePid, m.HomeScore, m.AwayScore);
            Add(m.AwayPid, m.AwayScore, m.HomeScore);
        }

        return table.OrderByDescending(t => t.Value.Points).ThenByDescending(t => t.Value.Diff)
            .ThenByDescending(t => t.Value.Goals).ThenBy(t => t.Key).Select(t => t.Key).ToList();
    }

    /// <summary>
    /// CMD_GET_ML_COMPE_PRIZE: prize, team_funds, compe_result, compe_name, my_pre_rank of the club's
    /// last finished ML competition. The prize is paid once. With a playoff the prize goes by its
    /// result (Prizes = VICTORY, SECOND_PLACE, BEST_4, BEST_8, FINAL_ADVANCEMENT, the detail page's
    /// prize_tournament_list); without one by the place in the final table. compe_result is enum 0x16.
    /// </summary>
    public static async Task<KvMessage> PrizeAsync(MlLeague league, int pid, CancellationToken ct)
    {
        var team = await league.TeamAsync(pid, ct);
        var entered = await league.Db.Players.Where(p => p.Pid == pid).Select(p => p.CompetitionId).FirstOrDefaultAsync(ct);
        var now = DateTimeOffset.FromUnixTimeSeconds(league.Now).UtcDateTime;
        var c = Competitions.CompetitionCatalog.Find(league.Options, entered) is { MasterLeague: true } found && now >= found.End ? found : null;
        if (team is null || c is null)
            return new KvMessage().Set("prize", 0).Set("team_funds", team?.Funds ?? 0).Set("compe_result", "NON").Set("compe_name", "").Set("my_pre_rank", 0);

        var place = (await TableAsync(league, c.Id, ct)).IndexOf(pid) + 1;
        var bracket = await Competitions.CompetitionBracket.LoadAsync(league.Db, c.Id, ct);
        var result = bracket.Count > 0 ? Competitions.CompetitionBracket.ResultOf(bracket, pid)
            : place == 1 ? new Competitions.CompetitionResult("VICTORY", 1, 0)
            : place == 2 ? new Competitions.CompetitionResult("SECOND_PLACE", 2, 1)
            : Competitions.CompetitionResult.None;
        var index = bracket.Count > 0 ? result.PrizeIndex : place - 1;
        var prize = index >= 0 && index < c.Prizes.Length ? c.Prizes[index] : 0;
        if (team.PrizeCompeId == c.Id)
            prize = 0;
        else
        {
            team.PrizeCompeId = c.Id;
            team.Funds += prize;
            team.LastPrizeIncome = prize;
            if (result.Result == "VICTORY")
                league.Event(pid, "COMPE", compeId: c.Id, text: c.Name);
            await league.Db.SaveChangesAsync(ct);
        }

        return new KvMessage()
            .Set("prize", prize)
            .Set("team_funds", team.Funds)
            .Set("compe_result", result.Result)
            .Set("compe_name", c.Name)
            .Set("my_pre_rank", place);
    }
}
