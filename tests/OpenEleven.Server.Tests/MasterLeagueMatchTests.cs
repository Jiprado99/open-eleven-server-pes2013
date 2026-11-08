// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;
using static OpenEleven.Server.Tests.MasterLeagueTests;

namespace OpenEleven.Server.Tests;

/// <summary>Master League Online matches: the PES 2013 quick match (_NTL) and the settlement.</summary>
public class MasterLeagueMatchTests
{
    private static List<KvMessage> Drain(Session s)
    {
        var pushed = new List<KvMessage>();
        while (s.Queue.Reader.TryRead(out var item))
            if (item.Message is { } m)
                pushed.Add(m);
        return pushed;
    }

    private static Task<IReadOnlyList<KvMessage>> Search(ServerHarness harness, Session s, string type = "ML_RANK")
        => harness.DispatchAsync(s, "MSG_QUICK_REQ_MATCHING_START_NTL", rqid: 9, withFields: r => r
            .Set("result", "NOERR").Set("type", type).Set("player_num", 2).Set("game_time", "GT10")
            .Set("is_rating", "NO").Set("has_guestplayer", "NO").Set("pass_level", 0).Set("is_pass_level", "NO")
            .Set("is_coach_mode", "NO").Set("is_team_power", "NO"));

    private static async Task AddPlayersAsync(ServerHarness harness, params int[] pids)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        foreach (var pid in pids)
        {
            var account = new Account { GameId = $"ml{pid}", PasswordHash = "x" };
            account.Players.Add(new Player { Pid = pid, Name = $"ml{pid}", NormalizedName = $"ML{pid}", Rating = 1000 });
            db.Accounts.Add(account);
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Two_master_league_managers_are_matched_with_their_clubs_in_the_ntl_notice()
    {
        await using var harness = await HarnessAsync();
        var (a, b) = (Manager(harness, 31), Manager(harness, 32));
        await CreateClubAsync(harness, a, "CLUB A");
        await CreateClubAsync(harness, b, "CLUB B");

        Assert.Equal(["MSG_QUICK_REQ_MATCHING_START_NTL", "MSG_QUICK_MATCHING_START_RES_NTL"], (await Search(harness, a)).Select(m => m.MsgName));
        Assert.Contains(await Search(harness, b), m => m.MsgName == "MSG_QUICK_MATCH_MEMBER_NOTICE_NTL");

        var notice = Assert.Single(Drain(a), m => m.MsgName == "MSG_QUICK_MATCH_MEMBER_NOTICE_NTL");
        Assert.Equal(50_000, notice.GetInt32("derby_income")); // both clubs are based on Lazio
        var member = notice.GetList("member")[1];
        foreach (var key in new[]
                 {
                     "pid", "matching_order", "is_owner", "pname", "nickname_id", "manner", "ntl_nat_type", "host_addr",
                     "reflexive_addr", "relayed_addr", "has_guestplayer", "is_begin", "from_country", "from_area",
                     "is_restricted", "rating", "ml_rating", "ml_team_name", "ml_base_team_id", "ml_team_type", "team_power",
                 })
            Assert.True(member.Has(key), key);
        Assert.Equal(("CLUB B", 122, 74), (member.GetString("ml_team_name"), member.GetInt32("ml_base_team_id"), member.GetInt32("team_power")));

        await harness.DispatchAsync(a, "MSG_QUICK_MATCH_MEMBER_REPLY_NTL", withFields: r => r.Set("result", "NOERR"));
        await harness.DispatchAsync(b, "MSG_QUICK_MATCH_MEMBER_REPLY_NTL", withFields: r => r.Set("result", "NOERR"));
        Assert.Contains(Drain(a), m => m.MsgName == "MSG_QUICK_MATCHING_RES_NTL");

        await harness.DispatchAsync(a, "MSG_QUICK_ACCEPT_MATCH_OFFER_NTL", withFields: r => r.Set("result", "NOERR"));
        Assert.Contains(await harness.DispatchAsync(b, "MSG_QUICK_ACCEPT_MATCH_OFFER_NTL", withFields: r => r.Set("result", "NOERR")),
            m => m.MsgName == "MSG_QUICK_ACCEPT_MATCH_OFFER_RES_NTL");
        Assert.Equal("ML_RANK", harness.Hub.RequireRoom(a.RoomId!.Value).MatchType);
    }

    [Fact]
    public async Task Master_league_and_open_searches_never_meet_and_a_club_is_required()
    {
        await using var harness = await HarnessAsync();
        var (a, b, c) = (Manager(harness, 33), Manager(harness, 34), Manager(harness, 35));
        await CreateClubAsync(harness, a);

        Assert.Equal("ERR_QMATCH_STARTMATCHING", (await Search(harness, c)).Single().GetString("result"));
        await Search(harness, a);
        Assert.DoesNotContain(await Search(harness, b, "OC_RANK"), m => m.MsgName == "MSG_QUICK_MATCH_MEMBER_NOTICE_NTL");
    }

    [Fact]
    public async Task A_settled_master_league_match_moves_record_money_and_squad()
    {
        await using var harness = await HarnessAsync();
        var (a, b) = (Manager(harness, 36), Manager(harness, 37));
        await AddPlayersAsync(harness, 36, 37);
        await CreateClubAsync(harness, a);
        await CreateClubAsync(harness, b, "OTHER");

        var room = new Room(900, "ml", 36, 2, ServiceRole.Menu, -1) { MatchType = "ML_RANK", HomeGoals = 2, AwayGoals = 0 };
        room.Sheet.Goal(false, "1ST", "36|55250|HOME|HOME|55251|HOME");
        room.Sheet.Goal(false, "2ND", "36|55250|HOME||0|");
        room.Sheet.Event("CARD|2ND|AWAY|YELLOW|37|55244");
        room.Sheet.Event("INJURY|2ND|HOME|55252");
        await harness.Services.GetRequiredService<MatchResults>().RecordAsync(room, (36, 37), leaverPid: null, noMatch: false);

        // The clients then report who played and their marks.
        await harness.DispatchAsync(a, "CMD_SEND_ML_GAMEPLAYERS", withFields: m => m
            .Set("gameplayer_id_list_num", 2).Set("gameplayer_id_list", new KvArray([55250, 55251])));
        await harness.DispatchAsync(a, "CMD_SET_ML_MYTEAM_PLAYER_RATING", withFields: m => m.SetList("rating_num", "rating_list",
            [new KvMessage().Set("gameplayer_id", 55250).Set("rating", 80)]));

        var income = Assert.Single(await harness.DispatchAsync(a, "CMD_GET_ML_MATCHINCOME_INFO"));
        Assert.Equal(350_000, income.GetInt32("income")); // win 300k + derby 50k
        Assert.Equal(50_000, income.GetInt32("derby_income"));
        var expense = Assert.Single(await harness.DispatchAsync(a, "CMD_GET_ML_MATCHEXPENSE")).GetInt32("expense");
        Assert.True(expense > 0);
        Assert.Equal(5_000_000 + 350_000 - expense, income.GetInt32("team_funds"));
        Assert.Equal(1, Assert.Single(await harness.DispatchAsync(b, "CMD_GET_ML_MATCHINCOME_INFO")).GetInt32("yellowcard_count"));

        var record = Assert.IsType<KvMessage>(Assert.Single(await harness.DispatchAsync(a, "CMD_GET_ML_MATCHRECORD")).GetValue("match_record"));
        Assert.Equal((1, 0, 0), (record.GetInt32("win_num"), record.GetInt32("draw_num"), record.GetInt32("lose_num")));

        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        var (home, away) = (await db.MlTeams.SingleAsync(t => t.Pid == 36), await db.MlTeams.SingleAsync(t => t.Pid == 37));
        Assert.Equal((3, 1, 1016), (home.Points, home.Wins, home.Rating));
        Assert.Equal((0, 1, 984), (away.Points, away.Losses, away.Rating));

        var squad = await db.MlTeamPlayers.Where(p => p.Pid == 36).ToDictionaryAsync(p => p.GamePlayerId);
        Assert.Equal((2, 1, 1, 35, 80), (squad[55250].Goals, squad[55250].Games, squad[55250].RatingCount, squad[55250].Tiredness, squad[55250].LastRating));
        Assert.Equal(1, squad[55251].Assists);
        Assert.Equal(2, squad[55252].InjuryMatches);
        Assert.Equal(29, squad[55244].ContractTerm);
        Assert.Equal(1, (await db.MlTeamPlayers.SingleAsync(p => p.Pid == 37 && p.GamePlayerId == 55244)).YellowCards);
    }

    [Fact]
    public async Task Game_plans_are_kept_per_mode()
    {
        await using var harness = await HarnessAsync();
        await AddPlayersAsync(harness, 38);
        var s = Manager(harness, 38);

        await harness.DispatchAsync(s, "CMD_SET_GAMEPLAN", withFields: m => m.Set("gameplan_data_size", 2).Set("gameplan_data", "AAAA"));
        await harness.DispatchAsync(s, "CMD_SET_GAMEPLAN", withFields: m => m.Set("gameplan_data_size", 2).Set("gameplan_data", "BBBB").Set("mode", "ML"));

        Assert.Equal("AAAA", Assert.Single(await harness.DispatchAsync(s, "CMD_GET_GAMEPLAN")).GetString("gameplan_data"));
        Assert.Equal("BBBB", Assert.Single(await harness.DispatchAsync(s, "CMD_GET_GAMEPLAN", withFields: m => m.Set("mode", "ML"))).GetString("gameplan_data"));
        // Nothing saved for competitions yet: the normal plan.
        Assert.Equal("AAAA", Assert.Single(await harness.DispatchAsync(s, "CMD_GET_GAMEPLAN", withFields: m => m.Set("mode", "COMPE"))).GetString("gameplan_data"));
    }
}
