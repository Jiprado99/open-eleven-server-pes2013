// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;

namespace OpenEleven.Server.MasterLeague;

/// <summary>
/// Master League Online club rankings. CMD_GET_ML_RANKING (ML_RANKING url) sends start_index,
/// order=ASC and rankall_kind = DIVISION_POINT | WIN | MATCH | TEAM_FUNDS | TEAM_POWER; the reply is
/// date, total_rank_num and rank_list[pid, rank, base_team_id, team_resource, team_power, ml_rating,
/// match_rec{win_num, draw_num, lose_num}, con_win, name, team_name] (key table at 0x01036xxx of the dump).
/// _BYID asks for the page that holds a pid.
/// ponytail: the division ranking's keys are not in the exe's strings; it uses the same layout,
/// restricted to one division and ordered by division points.
/// </summary>
public static class MlRankings
{
    public const int Page = 100;

    public sealed record Row(MlTeam Team, string Name, int Power, long Value);

    public static async Task<List<Row>> RowsAsync(MlLeague league, CancellationToken ct)
    {
        var teams = await league.Db.MlTeams.AsNoTracking().ToListAsync(ct);
        var pids = teams.Select(t => t.Pid).ToList();
        var names = await league.Db.Players.Where(p => pids.Contains(p.Pid)).ToDictionaryAsync(p => p.Pid, p => p.Name, ct);
        var squads = (await league.Db.MlTeamPlayers.AsNoTracking().Where(p => pids.Contains(p.Pid)).ToListAsync(ct))
            .GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.ToList());
        var owners = await league.Db.MlTeamPlayers.GroupBy(p => p.GamePlayerId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        return teams.Select(t =>
        {
            var squad = squads.GetValueOrDefault(t.Pid) ?? [];
            var value = squad.Sum(p => league.Price(p.GamePlayerId, owners.GetValueOrDefault(p.GamePlayerId)));
            return new Row(t, names.GetValueOrDefault(t.Pid, ""), league.TeamPower(squad), value);
        }).ToList();
    }

    public static IOrderedEnumerable<Row> Order(IEnumerable<Row> rows, string? kind) => kind switch
    {
        "WIN" => rows.OrderByDescending(r => r.Team.Wins).ThenByDescending(r => r.Team.Points),
        "MATCH" => rows.OrderByDescending(r => r.Team.Wins + r.Team.Draws + r.Team.Losses).ThenByDescending(r => r.Team.Points),
        "TEAM_FUNDS" => rows.OrderByDescending(r => r.Team.Funds),
        "TEAM_POWER" => rows.OrderByDescending(r => r.Power).ThenByDescending(r => r.Team.Points),
        _ => rows.OrderByDescending(r => r.Team.Points).ThenByDescending(r => r.Team.Rating),
    };

    public static async Task<KvMessage> RankingAsync(MlLeague league, string? kind, int startIndex, string? order, int? byPid, CancellationToken ct)
    {
        var ordered = Order(await RowsAsync(league, ct), kind).ThenBy(r => r.Team.Pid).ToList();
        return Render(league, ordered, startIndex, byPid);
    }

    public static async Task<KvMessage> DivisionRankingAsync(MlLeague league, int division, int startIndex, int? byPid, CancellationToken ct)
    {
        var rows = await RowsAsync(league, ct);
        if (byPid is { } pid && rows.FirstOrDefault(r => r.Team.Pid == pid) is { } mine)
            division = mine.Team.Division;
        if (division is >= 0 and <= 4)
            rows = rows.Where(r => r.Team.Division == division).ToList();
        return Render(league, Order(rows, "DIVISION_POINT").ThenBy(r => r.Team.Pid).ToList(), startIndex, byPid)
            .Set("division", MlRules.DivisionLabel(Math.Max(0, division)));
    }

    private static KvMessage Render(MlLeague league, List<Row> ordered, int startIndex, int? byPid)
    {
        var start = Math.Max(0, startIndex);
        if (byPid is { } pid)
        {
            var at = ordered.FindIndex(r => r.Team.Pid == pid);
            if (at < 0)
                return MlHttp.Fail("ERR_NOTFOUND");
            start = at / Page * Page;
        }

        var list = ordered.Skip(start).Take(Page).Select((r, i) => new KvMessage()
            .Set("pid", r.Team.Pid)
            .Set("rank", start + i + 1)
            .Set("base_team_id", r.Team.BaseTeamId)
            .Set("team_resource", r.Value)
            .Set("team_power", r.Power)
            .Set("ml_rating", r.Team.Rating)
            .Set("match_rec", new KvMessage().Set("win_num", r.Team.Wins).Set("draw_num", r.Team.Draws).Set("lose_num", r.Team.Losses))
            .Set("con_win", r.Team.WinRun)
            .Set("name", r.Name)
            .Set("team_name", r.Team.Name)).ToList();

        return MlHttp.Ok().Set("date", league.Today).Set("total_rank_num", ordered.Count).SetList("rank_list_num", "rank_list", list);
    }

    /// <summary>CMD_GET_ML_MYRANKING: the club's place by team power, funds and rating.</summary>
    public static async Task<KvMessage?> MyRankingAsync(MlLeague league, int pid, CancellationToken ct)
    {
        var rows = await RowsAsync(league, ct);
        if (rows.All(r => r.Team.Pid != pid))
            return null;
        int Place(IEnumerable<Row> ordered) => ordered.Select(r => r.Team.Pid).ToList().IndexOf(pid) + 1;
        return new KvMessage()
            .Set("teampower_rank", Place(Order(rows, "TEAM_POWER")))
            .Set("teampower_total_rank_num", rows.Count)
            .Set("teamfunds_rank", Place(Order(rows, "TEAM_FUNDS")))
            .Set("teamfunds_total_rank_num", rows.Count)
            .Set("rating_rank", Place(rows.OrderByDescending(r => r.Team.Rating)))
            .Set("rating_total_rank_num", rows.Count)
            .Set("date", league.Today);
    }
}
