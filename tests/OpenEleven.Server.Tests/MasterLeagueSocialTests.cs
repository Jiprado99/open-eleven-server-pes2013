// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.MasterLeague;
using static OpenEleven.Server.Tests.MasterLeagueTests;

namespace OpenEleven.Server.Tests;

/// <summary>Master League competitions, Rival Rank, news and events.</summary>
public class MasterLeagueSocialTests
{
    private static async Task AddPlayersAsync(ServerHarness harness, params int[] pids)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        foreach (var pid in pids)
        {
            var account = new Account { GameId = $"s{pid}", PasswordHash = "x" };
            account.Players.Add(new Player { Pid = pid, Name = $"s{pid}", NormalizedName = $"S{pid}", Rating = 1000 });
            db.Accounts.Add(account);
        }

        await db.SaveChangesAsync();
    }

    private static async Task AddMatchAsync(ServerHarness harness, int home, int away, int homeGoals, int awayGoals, DateTime playedAt,
        string type = "ML_RANK", int compeId = 0)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        db.Matches.Add(new MatchRecord
        {
            HomePid = home, AwayPid = away, HomeScore = homeGoals, AwayScore = awayGoals, Completed = true, PlayedAt = playedAt, CompeId = compeId,
            Settings = new KvWriter().Write(new KvMessage().Set("match_type", type)).TrimEnd('\0'),
        });
        await db.SaveChangesAsync();
    }

    private static void Competition(Dictionary<string, string?> s, int id, DateTime start, DateTime end, string prizes = "")
    {
        var at = $"Server:Competitions:0:";
        s[at + "Id"] = id.ToString();
        s[at + "Name"] = "ML CUP";
        s[at + "MasterLeague"] = "true";
        s[at + "EntryStart"] = start.ToString("O");
        s[at + "EntryEnd"] = start.AddDays(1).ToString("O");
        s[at + "MatchStart"] = start.AddDays(1).ToString("O");
        s[at + "MatchEnd"] = end.ToString("O");
        s[at + "End"] = end.ToString("O");
        s[at + "MinTeamPower"] = "70";
        if (prizes.Length > 0)
            foreach (var (prize, i) in prizes.Split(',').Select((p, i) => (p, i)))
                s[at + $"Prizes:{i}"] = prize;
    }

    [Fact]
    public async Task A_master_league_competition_needs_a_club_with_enough_power_and_freezes_the_market()
    {
        var now = DateTime.UtcNow;
        await using var harness = await HarnessAsync(s => Competition(s, 70, now.AddHours(-1), now.AddDays(5)));
        await AddPlayersAsync(harness, 51, 52);
        var (a, b) = (Manager(harness, 51), Manager(harness, 52));

        Assert.Equal("ERR_NOTREGISTERED", Assert.Single(await harness.DispatchAsync(a, "CMD_JOIN_COMPLG", withFields: m => m.Set("compe_id", 70))).GetString("result"));
        await CreateClubAsync(harness, a);
        Assert.Equal("NOERR", Assert.Single(await harness.DispatchAsync(a, "CMD_JOIN_COMPLG", withFields: m => m.Set("compe_id", 70))).GetString("result"));

        var info = Assert.IsType<KvMessage>(Assert.Single(await harness.DispatchAsync(a, "CMD_GET_COMPLG_INFO", withFields: m => m.Set("compe_id", 70))).GetValue("compe_data"));
        Assert.Equal(("YES", "TRUE", "FALSE"), (info.GetString("is_ml_compe"), info.GetString("use_ml"), info.GetString("use_club")));

        Assert.Equal("ERR_COMPE_ENTRY", Assert.Single(await harness.DispatchAsync(a, "CMD_BUY_ML_MARKET_GAMEPLAYER",
            withFields: m => m.Set("gameplayer_id", Average))).GetString("result"));

        // A club of team power 74 passes a minimum of 70; one weakened below it does not.
        await CreateClubAsync(harness, b);
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            await db.MlTeamPlayers.Where(p => p.Pid == 52 && p.GamePlayerId >= 55254).ExecuteDeleteAsync();
        }

        Assert.Equal("ERR_COMPE_JOIN_TEAMPOWER", Assert.Single(await harness.DispatchAsync(b, "CMD_JOIN_COMPLG", withFields: m => m.Set("compe_id", 70))).GetString("result"));
    }

    [Fact]
    public async Task The_competition_prize_goes_by_the_final_table_and_is_paid_once()
    {
        var now = DateTime.UtcNow;
        await using var harness = await HarnessAsync(s => Competition(s, 71, now.AddDays(-10), now.AddDays(-1), "2000000,1000000"));
        await AddPlayersAsync(harness, 53, 54);
        var (a, b) = (Manager(harness, 53), Manager(harness, 54));
        await CreateClubAsync(harness, a);
        await CreateClubAsync(harness, b);
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            await db.Players.Where(p => p.Pid == 53 || p.Pid == 54).ExecuteUpdateAsync(p => p.SetProperty(x => x.CompetitionId, 71));
        }

        await AddMatchAsync(harness, 53, 54, 1, 3, now.AddDays(-5), "ML_COMPE", 71);

        var prize = Assert.Single(await harness.DispatchAsync(b, "CMD_GET_ML_COMPE_PRIZE"));
        Assert.Equal((2_000_000, 1, "ML CUP"), (prize.GetInt32("prize"), prize.GetInt32("my_pre_rank"), prize.GetString("compe_name")));
        Assert.Equal(7_000_000, prize.GetInt32("team_funds"));
        Assert.Equal(0, Assert.Single(await harness.DispatchAsync(b, "CMD_GET_ML_COMPE_PRIZE")).GetInt32("prize"));
        Assert.Equal(1_000_000, Assert.Single(await harness.DispatchAsync(a, "CMD_GET_ML_COMPE_PRIZE")).GetInt32("prize"));
    }

    [Fact]
    public async Task Rival_rank_tables_the_week_and_pays_last_week_s_top_three_once()
    {
        await using var harness = await HarnessAsync();
        await AddPlayersAsync(harness, 55, 56, 57);
        foreach (var pid in new[] { 55, 56, 57 })
            await CreateClubAsync(harness, Manager(harness, pid), $"R{pid}");

        await using var scope = harness.Services.CreateAsyncScope();
        var league = scope.ServiceProvider.GetRequiredService<MlLeague>();
        var thisWeek = DateTimeOffset.FromUnixTimeSeconds(league.WeekStart).UtcDateTime;
        await AddMatchAsync(harness, 55, 56, 2, 0, thisWeek.AddHours(1));
        await AddMatchAsync(harness, 57, 55, 1, 1, thisWeek.AddDays(-3));
        await AddMatchAsync(harness, 56, 57, 0, 0, thisWeek.AddDays(-3), "OC_RANKING"); // not an ML match

        async Task<KvMessage> Http(string msg, int pid)
            => new KvReader().Parse(await MlHttp.RenderAsync(league, "ML_RIVAL_RANK", $"msg=\"{msg}\",pid={pid}", 0, default));

        var now = await Http("CMD_GET_ML_RIVAL_RANK", 55);
        Assert.Equal(2, now.GetInt32("rival_player_num"));
        Assert.Equal([55, 56], now.GetList("rank_list").Select(r => r.GetInt32("pid")));
        Assert.Equal(3, Assert.IsType<KvArray>(now.GetValue("price_list")).Values.Count);

        var status = await Http("CMD_GET_ML_RIVAL_RANK_STATUS", 57);
        Assert.Equal(("FALSE", "TRUE", "BEGINNER"), (status.GetString("is_rank_entry"), status.GetString("has_rank_last"), status.GetString("status")));

        var last = await Http("CMD_GET_ML_RIVAL_RANK_LAST", 55);
        Assert.Equal(2, last.GetInt32("rival_player_num"));

        // Last week 55 and 57 drew: level on everything, the lower pid leads.
        Assert.Equal(1_000_000, (await Http("CMD_GET_ML_RIVAL_RANK_BONUS", 55)).GetInt32("income"));
        var bonus = await Http("CMD_GET_ML_RIVAL_RANK_BONUS", 57);
        Assert.Equal((600_000, 5_600_000), (bonus.GetInt32("income"), bonus.GetInt32("post_team_funds")));
        Assert.Equal("ERR_NO_WIN_POINT", (await Http("CMD_GET_ML_RIVAL_RANK_BONUS", 57)).GetString("result"));
        Assert.Equal("ERR_NO_WIN_POINT", (await Http("CMD_GET_ML_RIVAL_RANK_BONUS", 56)).GetString("result"));
    }

    [Fact]
    public async Task Club_events_make_the_news_and_take_comments()
    {
        await using var harness = await HarnessAsync(s => s["Server:Protocol:BlockedTerms:0"] = "badword");
        await AddPlayersAsync(harness, 58);
        var a = Manager(harness, 58);
        await CreateClubAsync(harness, a);
        await harness.DispatchAsync(a, "CMD_BUY_ML_MARKET_GAMEPLAYER", withFields: m => m.Set("gameplayer_id", Average));

        var events = Assert.Single(await harness.DispatchAsync(a, "CMD_GET_ML_EVENTLIST", withFields: m => m.Set("pid", 58)));
        Assert.Equal(["BUY", "TEAM_CREATE"], events.GetList("event_list").Select(e => e.GetString("event_type")));

        Assert.Equal("ERR_NGWORD", Assert.Single(await harness.DispatchAsync(a, "CMD_SET_ML_NEWS_COMMENT_BUY",
            withFields: m => m.Set("gameplayer_id", Average).Set("comment", "badword"))).GetString("result"));
        Assert.Equal("NOERR", Assert.Single(await harness.DispatchAsync(a, "CMD_SET_ML_NEWS_COMMENT_BUY",
            withFields: m => m.Set("gameplayer_id", Average).Set("comment", "Welcome!"))).GetString("result"));

        var comments = Assert.Single(await harness.DispatchAsync(a, "CMD_GET_ML_NEWS_LIST", withFields: m => m.Set("news_type", "COMMENT")));
        var news = Assert.Single(comments.GetList("news_list"));
        Assert.Equal(("BUY", "AVERAGE", "Welcome!", "s58"), (news.GetString("news_kind"), news.GetString("text"), news.GetString("comment"), news.GetString("name")));
        Assert.Equal(2, Assert.Single(await harness.DispatchAsync(a, "CMD_GET_ML_NEWS_LIST", withFields: m => m.Set("news_type", "REGULAR"))).GetInt32("news_list_num"));
    }
}
