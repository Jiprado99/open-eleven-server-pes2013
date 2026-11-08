// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Data;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.MasterLeague;
using static OpenEleven.Server.Tests.MasterLeagueTests;

namespace OpenEleven.Server.Tests;

/// <summary>Master League Online club rankings (ML_RANKING, ML_DIVISIONRANKING urls, CMD_GET_ML_MYRANKING).</summary>
public class MasterLeagueRankingTests
{
    private static async Task<ServerHarness> LeagueAsync()
    {
        var harness = await HarnessAsync();
        foreach (var pid in new[] { 41, 42, 43 })
            await CreateClubAsync(harness, Manager(harness, pid), $"CLUB {pid}");

        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        foreach (var (pid, points, wins, funds) in new[] { (41, 10, 3, 1_000L), (42, 40, 13, 9_000L), (43, 25, 8, 5_000L) })
        {
            var team = await db.MlTeams.SingleAsync(t => t.Pid == pid);
            (team.Points, team.Wins, team.Funds, team.Division) = (points, wins, funds, new MlRules(new()).DivisionFor(points));
        }

        await db.SaveChangesAsync();
        return harness;
    }

    private static async Task<KvMessage> HttpAsync(ServerHarness harness, string type, string body)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        return new KvReader().Parse(await MlHttp.RenderAsync(scope.ServiceProvider.GetRequiredService<MlLeague>(), type, body, 0, default));
    }

    [Fact]
    public async Task The_ranking_orders_clubs_by_the_asked_kind()
    {
        await using var harness = await LeagueAsync();

        var points = await HttpAsync(harness, "ML_RANKING", "msg=\"CMD_GET_ML_RANKING\",start_index=0,order=\"ASC\",rankall_kind=\"DIVISION_POINT\"");
        Assert.Equal(3, points.GetInt32("total_rank_num"));
        Assert.Equal([42, 43, 41], points.GetList("rank_list").Select(r => r.GetInt32("pid")));
        var first = points.GetList("rank_list")[0];
        Assert.Equal((1, "CLUB 42", 13), (first.GetInt32("rank"), first.GetString("team_name"), Assert.IsType<KvMessage>(first.GetValue("match_rec")).GetInt32("win_num")));

        var funds = await HttpAsync(harness, "ML_RANKING", "rankall_kind=TEAM_FUNDS");
        Assert.Equal([42, 43, 41], funds.GetList("rank_list").Select(r => r.GetInt32("pid")));

        var byId = await HttpAsync(harness, "ML_RANKING_BYID", "rankall_kind=WIN&pid=41");
        Assert.Equal(3, byId.GetList("rank_list").Single(r => r.GetInt32("pid") == 41).GetInt32("rank"));
    }

    [Fact]
    public async Task The_division_ranking_lists_one_division()
    {
        await using var harness = await LeagueAsync();
        // 10 points = division 0 (ML_DIVISION_5), 25 and 40 = division 1 (30+ needed: 40 only).
        var mine = await HttpAsync(harness, "ML_DIVISIONRANKING_BYID", "pid=42");
        Assert.Equal("ML_DIVISION_4", mine.GetString("division"));
        Assert.Equal([42], mine.GetList("rank_list").Select(r => r.GetInt32("pid")));

        var lowest = await HttpAsync(harness, "ML_DIVISIONRANKING", "division=0");
        Assert.Equal([43, 41], lowest.GetList("rank_list").Select(r => r.GetInt32("pid")));
    }

    [Fact]
    public async Task My_ranking_places_the_club_and_needs_one()
    {
        await using var harness = await LeagueAsync();
        var reply = Assert.Single(await harness.DispatchAsync(Manager(harness, 43), "CMD_GET_ML_MYRANKING"));
        Assert.Equal((2, 3), (reply.GetInt32("teamfunds_rank"), reply.GetInt32("teamfunds_total_rank_num")));
        Assert.Equal("ERR_NOTFOUND", Assert.Single(await harness.DispatchAsync(Manager(harness, 49), "CMD_GET_ML_MYRANKING")).GetString("result"));
    }
}
