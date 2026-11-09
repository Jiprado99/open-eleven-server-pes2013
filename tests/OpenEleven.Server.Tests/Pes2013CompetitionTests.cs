// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Competitions;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

/// <summary>PES 2013 Online Competition: the 2013 keys and enums, the qualifying round, the playoff and the champions.</summary>
public class Pes2013CompetitionTests
{
    private static string At(DateTime t) => t.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>A competition at index <paramref name="i"/>: entry and qualifying round between the times given, ending with the qualifying round.</summary>
    private static void Cup(Dictionary<string, string?> s, int i, int id, DateTime entryStart, DateTime matchStart, DateTime matchEnd,
        params (string Key, string Value)[] more)
    {
        var at = $"Server:Competitions:{i}:";
        s[at + "Id"] = id.ToString(CultureInfo.InvariantCulture);
        s[at + "Name"] = $"Cup {id}";
        s[at + "EntryStart"] = At(entryStart);
        s[at + "EntryEnd"] = At(matchEnd);
        s[at + "MatchStart"] = At(matchStart);
        s[at + "MatchEnd"] = At(matchEnd);
        s[at + "End"] = At(matchEnd);
        foreach (var (key, value) in more)
            s[at + key] = value;
    }

    private static Task<ServerHarness> HarnessAsync(Action<Dictionary<string, string?>> cups, string profile = "Pes2013Pc")
        => ServerHarness.CreateAsync(s =>
        {
            s["Server:GameProfile"] = profile;
            cups(s);
        });

    private static async Task<Session> EntrantAsync(ServerHarness harness, string name, int compeId, int teamId = 0, string lang = "EN")
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        var account = new Account { GameId = name, PasswordHash = "x" };
        var player = new Player
        {
            Name = name, NormalizedName = name.ToUpperInvariant(), Division = Division.D5, CompetitionId = compeId, Lang = lang,
            CompetitionTeamId = teamId, CompetitionTeamName = teamId == 0 ? "" : $"TEAM {teamId}",
        };
        account.Players.Add(player);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        if (compeId != 0)
        {
            db.CompetitionEntries.Add(new CompetitionEntry { Pid = player.Pid, CompeId = compeId });
            await db.SaveChangesAsync();
        }

        var session = harness.NewSession(ServiceRole.Menu, SessionState.PlayerSelected);
        session.Pid = player.Pid;
        session.PlayerName = name;
        session.AccountId = account.Id;
        return session;
    }

    private static async Task PlayAsync(ServerHarness harness, int compeId, int home, int away, int homeGoals, int awayGoals)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        db.Matches.Add(new MatchRecord
        {
            CompeId = compeId, Completed = true, HomePid = home, AwayPid = away, HomeScore = homeGoals, AwayScore = awayGoals,
            HomePeriods = $"{homeGoals},0,0,0,0", AwayPeriods = $"{awayGoals},0,0,0,0", PlayedAt = DateTime.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();
    }

    private static async Task TickAsync(ServerHarness harness, DateTime now)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<ServerOptions>>().CurrentValue;
        await CompetitionEngine.TickAsync(db, harness.Hub, options, now, NullLogger.Instance);
    }

    /// <summary>What the server pushed to a session since the last look.</summary>
    private static List<KvMessage> Pushed(Session s)
    {
        var list = new List<KvMessage>();
        while (s.Queue.Reader.TryRead(out var item))
            if (item.Message is { } m)
                list.Add(m);
        return list;
    }

    private static KvMessage Record(KvMessage m, string key) => Assert.IsType<KvMessage>(m.GetValue(key));

    [Fact]
    public async Task The_list_and_the_detail_carry_the_PES_2013_keys_and_enums()
    {
        var now = DateTime.UtcNow;
        await using var harness = await HarnessAsync(s =>
        {
            // Kind left at the PES 2011 default, EntryLimit at its default.
            Cup(s, 0, 7, now.AddDays(-1), now.AddDays(1), now.AddDays(3), ("Fee", "500"), ("Comment", "Hello"), ("BannerFileName", "compeBannerKONAMIChamp"));
            Cup(s, 1, 8, now.AddDays(-1), now.AddDays(1), now.AddDays(3), ("Kind", "ML_Open"), ("MasterLeague", "true"), ("Prizes:0", "900"));
        });
        var s = await EntrantAsync(harness, "ana", 0);

        Assert.Equal("NOERR", Assert.Single(await harness.DispatchAsync(s, "CMD_JOIN_COMPLG_MODE", withFields: r => r.Set("jdlc_type", "NOTHING"))).GetString("result"));
        Assert.Equal("NOERR", Assert.Single(await harness.DispatchAsync(s, "CMD_LEAVE_COMPLG_MODE")).GetString("result"));

        var list = Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_LIST", withFields: r => r
            .Set("compe_list_kind", "NOW_PLAY").Set("compe_kind", "KONAMI_CHAMPIONSHIP").Set("compe_mode", "NORMAL")));
        var cup = Assert.Single(list.GetList("compe_list"));
        Assert.Equal((7, "KONAMI_CHAMPIONSHIP", "SKILLLEVEL", "ENTRY", "FALSE"),
            (cup.GetInt32("compe_id"), cup.GetString("compe_kind"), cup.GetString("entry_limit"), cup.GetString("status"), cup.GetString("entry_limit_facebook")));
        Assert.Equal(Enumerable.Repeat("TRUE", 6), Assert.IsType<KvArray>(cup.GetValue("entry_limit_skill_level")).Values.Select(v => v?.ToString()));
        foreach (var key in new[] { "entry_limit_team_power_min", "entry_limit_team_power_max", "banner_file_name", "champion_notice", "is_coach_mode", "need_konamiid", "dlcver" })
            Assert.True(cup.Has(key), key);
        Assert.False(cup.Has("entry_limit_division"));

        // compe_mode ML lists only the Master League competitions.
        var ml = Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_LIST", withFields: r => r
            .Set("compe_list_kind", "NOW_PLAY").Set("compe_kind", "KONAMI_CHAMPIONSHIP").Set("compe_mode", "ML")));
        Assert.Equal((8, "ML_Open", "YES"), (Assert.Single(ml.GetList("compe_list")).GetInt32("compe_id"),
            ml.GetList("compe_list")[0].GetString("compe_kind"), ml.GetList("compe_list")[0].GetString("is_ml_compe")));
        var kinds = Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_KIND_LIST", withFields: r => r.Set("compe_mode", "NORMAL")));
        Assert.Equal("KONAMI_CHAMPIONSHIP", Assert.Single(kinds.GetList("compe_kind_list")).GetString("compe_kind"));

        var data = Record(Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_INFO", withFields: r => r.Set("compe_id", 7))), "compe_data");
        Assert.Equal((500, "Hello", "compeBannerKONAMIChamp"), (data.GetInt32("compe_fee"), data.GetString("comment"), data.GetString("banner_file_name")));
        Assert.Equal((3, 5, 5, 8, 5), (Assert.IsType<KvArray>(data.GetValue("gametime")).Values.Count, Assert.IsType<KvArray>(data.GetValue("get_ball_id")).Values.Count,
            Assert.IsType<KvArray>(data.GetValue("get_spike_id")).Values.Count, Assert.IsType<KvArray>(data.GetValue("prize_second_round_list")).Values.Count,
            Assert.IsType<KvArray>(data.GetValue("prize_tournament_list")).Values.Count));
        var mlData = Record(Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_INFO", withFields: r => r.Set("compe_id", 8))), "compe_data");
        Assert.Equal(("900", "FALSE", "TRUE"), (Assert.IsType<KvArray>(mlData.GetValue("prize_tournament_list")).Values[0]?.ToString(), mlData.GetString("use_club"), mlData.GetString("use_ml")));
    }

    [Fact]
    public async Task Is_entry_NO_only_reopens_an_entry_and_skill_levels_limit_who_enters()
    {
        var now = DateTime.UtcNow;
        await using var harness = await HarnessAsync(s =>
        {
            Cup(s, 0, 7, now.AddDays(-1), now.AddDays(1), now.AddDays(3));
            Cup(s, 1, 9, now.AddDays(-1), now.AddDays(1), now.AddDays(3), ("SkillLevels:0", "BEGINNER"));
        });
        var s = await EntrantAsync(harness, "ana", 0);

        Assert.Equal("ERR_COMPE_NOTENTRY", Assert.Single(await harness.DispatchAsync(s, "CMD_JOIN_COMPLG", withFields: r => r.Set("compe_id", 7).Set("is_entry", "NO"))).GetString("result"));
        // A player with no skill level set counts as STANDARD.
        Assert.Equal("ERR_COMPE_JOIN_SKILL_LEVEL", Assert.Single(await harness.DispatchAsync(s, "CMD_JOIN_COMPLG", withFields: r => r.Set("compe_id", 9).Set("is_entry", "YES"))).GetString("result"));
        Assert.Equal("NO", Assert.Single(await harness.DispatchAsync(s, "CMD_JOIN_COMPLG", withFields: r => r.Set("compe_id", 7).Set("is_entry", "YES"))).GetString("is_select_team"));
        Assert.Equal("NOERR", Assert.Single(await harness.DispatchAsync(s, "CMD_JOIN_COMPLG", withFields: r => r.Set("compe_id", 7).Set("is_entry", "NO"))).GetString("result"));
    }

    [Fact]
    public async Task A_repeating_competition_runs_again_with_the_next_compe_id()
    {
        var now = DateTime.UtcNow;
        await using var harness = await HarnessAsync(s => Cup(s, 0, 100, now.AddDays(-10), now.AddDays(-9.5), now.AddDays(-8), ("RepeatEveryDays", "7"), ("PlayoffSize", "0")));
        var options = harness.Services.GetRequiredService<IOptionsMonitor<ServerOptions>>().CurrentValue;

        // Runs 0 and 1 are over, run 2 opens in four days.
        Assert.Equal(new[] { 100, 101, 102 }, CompetitionCatalog.All(options, now).Select(c => c.Id));
        var next = CompetitionCatalog.Find(options, 102)!;
        Assert.Equal(now.AddDays(4), next.EntryStart, TimeSpan.FromSeconds(1));
        Assert.Equal("CREATED", CompetitionCatalog.Status(next, now));

        var s = await EntrantAsync(harness, "ana", 0);
        var schedule = Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_LIST", withFields: r => r.Set("compe_list_kind", "SCHEDULE").Set("compe_mode", "NORMAL")));
        Assert.Equal(102, Assert.Single(schedule.GetList("compe_list")).GetInt32("compe_id"));
    }

    [Fact]
    public async Task Qualifying_matches_pair_only_entrants_with_a_team_while_the_round_is_on()
    {
        var now = DateTime.UtcNow;
        await using var harness = await HarnessAsync(s =>
        {
            Cup(s, 0, 7, now.AddDays(-2), now.AddHours(-1), now.AddDays(2), ("MaxQualifierMatches", "1"));
            Cup(s, 1, 8, now.AddDays(-2), now.AddHours(1), now.AddDays(2));
        });
        var noTeam = await EntrantAsync(harness, "ana", 7);
        var team = await EntrantAsync(harness, "bob", 7, teamId: 108);
        var early = await EntrantAsync(harness, "cid", 8, teamId: 108);
        var other = await EntrantAsync(harness, "dan", 0);

        async Task<string?> Search(Session s) => (await harness.DispatchAsync(s, "MSG_QUICK_REQ_MATCHING_START_NTL", withFields: r => r
            .Set("type", "COMPE").Set("player_num", 2).Set("game_time", "GT10")))[0].GetString("result");

        Assert.Equal("ERR_QMATCH_STARTMATCHING", await Search(noTeam));
        Assert.Equal("ERR_QMATCH_STARTMATCHING", await Search(early));
        Assert.Equal("ERR_QMATCH_STARTMATCHING", await Search(other));
        Assert.Equal("NOERR", await Search(team));

        // Played its one qualifying match: no more.
        await harness.DispatchAsync(team, "CMD_CANCEL_STARTQUICKMATCH_NTL");
        await PlayAsync(harness, 7, team.Pid, noTeam.Pid, 1, 0);
        Assert.Equal("ERR_QMATCH_STARTMATCHING", await Search(team));
    }

    [Fact]
    public async Task My_results_carry_the_PES_2013_keys()
    {
        var now = DateTime.UtcNow;
        await using var harness = await HarnessAsync(s => Cup(s, 0, 7, now.AddDays(-2), now.AddHours(-1), now.AddDays(2)));
        var a = await EntrantAsync(harness, "ana", 7, 108);
        var b = await EntrantAsync(harness, "bob", 7, 105);
        await PlayAsync(harness, 7, a.Pid, b.Pid, 2, 1);

        var mine = Assert.Single(await harness.DispatchAsync(a, "CMD_GET_COMPLG_MYRESULTS"));
        var match = Assert.Single(mine.GetList("match_result_list"));
        Assert.Equal("NORMAL_END", match.GetString("gameend_reason"));
        var home = Record(match, "home");
        Assert.Equal(("FALSE", "STANDARD", "TEAM 108", 2), (home.GetString("is_restricted"), home.GetString("skill_level"), home.GetString("team_name"), Record(home, "score").GetInt32("score_1st")));
    }

    [Fact]
    public async Task The_qualifiers_play_the_playoff_to_a_champion()
    {
        var now = DateTime.UtcNow;
        await using var harness = await HarnessAsync(s => Cup(s, 0, 7, now.AddDays(-2), now.AddHours(-3), now.AddMinutes(-10),
            ("PlayoffSize", "4"), ("PlayoffStart", At(now.AddMinutes(-5))), ("PlayoffRoundMinutes", "30"), ("ShowUpMinutes", "10"),
            ("Kind", "UEFA_Champions_League"), ("CupFileName", "compeTrophyUEFA")));
        var a = await EntrantAsync(harness, "ana", 7, 101);
        var b = await EntrantAsync(harness, "bob", 7, 102);
        var c = await EntrantAsync(harness, "cid", 7, 103, lang: "SP");
        var d = await EntrantAsync(harness, "dan", 7, 104);
        var e = await EntrantAsync(harness, "eve", 7, 105);

        // Table: ana 6, bob 4, cid 3, dan 1, eve 0 (eve also short of nothing: last).
        await PlayAsync(harness, 7, a.Pid, e.Pid, 3, 0);
        await PlayAsync(harness, 7, a.Pid, d.Pid, 2, 0);
        await PlayAsync(harness, 7, b.Pid, e.Pid, 1, 0);
        await PlayAsync(harness, 7, b.Pid, c.Pid, 1, 1);
        await PlayAsync(harness, 7, c.Pid, e.Pid, 2, 1);
        await PlayAsync(harness, 7, d.Pid, c.Pid, 0, 0);
        await PlayAsync(harness, 7, d.Pid, e.Pid, 0, 0);

        foreach (var s in new[] { a, b, c, d, e })
        {
            await harness.DispatchAsync(s, "CMD_WATCH_COMPLG_MATCH_START", rqid: 50);
            await harness.DispatchAsync(s, "CMD_WATCH_COMPLG_END_MATCH", rqid: 51);
        }

        // The qualifying round closes; the first semi-final round (1 v 4, 2 v 3) starts at once.
        await TickAsync(harness, now);
        foreach (var s in new[] { a, b, c, d, e })
            Pushed(s);
        // Arming the end watch after it is decided answers it at once, and only once.
        var eveEnd = Assert.Single(await harness.DispatchAsync(e, "CMD_WATCH_COMPLG_END", rqid: 52));
        Assert.Equal(("SECOND_ROUND_EXIT", "NO"), (eveEnd.GetString("end_kind"), eveEnd.GetString("is_disqualified")));
        Assert.Empty(await harness.DispatchAsync(e, "CMD_WATCH_COMPLG_END", rqid: 53));
        Assert.Equal("FINAL_ADVANCEMENT", Assert.Single(await harness.DispatchAsync(a, "CMD_WATCH_COMPLG_END", rqid: 52)).GetString("end_kind"));

        var table = Assert.Single(await harness.DispatchAsync(a, "CMD_GET_COMPLG_PRE_RESULTS", withFields: r => r.Set("compe_id", 7).Set("start_rank", 1).Set("get_count", 10)));
        Assert.Equal(new[] { "YES", "YES", "YES", "YES", "NO" }, table.GetList("rank_list").Select(r => r.GetString("is_qualifier")));

        // Already on: the watch is answered at once.
        Assert.Equal("TRUE", Assert.Single(await harness.DispatchAsync(a, "CMD_WATCH_COMPLG_MATCH_START", rqid: 50)).GetString("is_match_start"));

        var semi = Assert.Single(await harness.DispatchAsync(a, "CMD_GET_COMPLG_PLAYOFF_MATCH_NTL"));
        Assert.Equal(("MATCH_3", 2), (semi.GetString("match_count"), semi.GetInt32("match_table_num")));
        var mine = semi.GetList("match_table").Single(m => Record(m, "home").GetInt32("pid") == a.Pid);
        Assert.Equal((d.Pid, "TEAM 104"), (Record(mine, "away").GetInt32("pid"), Record(mine, "away").GetString("team_name")));
        foreach (var key in new[] { "name", "team_id", "host_addr", "reflexive_addr", "relayed_addr", "ntl_nat_type", "ml_team_type" })
            Assert.True(Record(mine, "away").Has(key), key);
        await harness.DispatchAsync(d, "CMD_GET_COMPLG_PLAYOFF_MATCH_NTL");
        Assert.NotNull(a.RoomId);
        Assert.Equal(a.RoomId, d.RoomId);

        // ana 2-1 dan, reported through the room like any match.
        var room = harness.Hub.FindRoom(a.RoomId!.Value)!;
        (room.HomeGoals, room.AwayGoals) = (2, 1);
        await harness.Services.GetRequiredService<MatchResults>().RecordAsync(room, MatchResults.Lineup(room), leaverPid: null, noMatch: false);

        // bob turns up, cid does not: at the show-up time bob goes through.
        await harness.DispatchAsync(b, "CMD_GET_COMPLG_PLAYOFF_MATCH_NTL");
        await TickAsync(harness, now.AddMinutes(6));
        var won = Assert.Single(Pushed(a), m => m.GetString("msg") == "CMD_WATCH_COMPLG_END_MATCH");
        Assert.Equal((a.Pid, "NORMAL_END", "FALSE"), (won.GetInt32("win_pid"), won.GetString("end_kind"), won.GetString("is_draw")));
        Assert.Equal(a.Pid, Assert.Single(Pushed(d), m => m.GetString("msg") == "CMD_WATCH_COMPLG_END_MATCH").GetInt32("win_pid"));
        var noShow = Assert.Single(Pushed(c), m => m.GetString("msg") == "CMD_WATCH_COMPLG_END_MATCH");
        Assert.Equal((b.Pid, "TIMEOUT"), (noShow.GetInt32("win_pid"), noShow.GetString("end_kind")));
        Pushed(b);

        // The final starts 30 minutes after the semi-finals; only ana turns up for it.
        await TickAsync(harness, now.AddMinutes(26));
        Assert.Contains(Pushed(a), m => m.GetString("msg") == "CMD_WATCH_COMPLG_MATCH_START");
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            var final = await db.CompetitionPlayoffMatches.SingleAsync(m => m.CompeId == 7 && m.Round == 4);
            Assert.Equal((a.Pid, b.Pid), (final.HomePid, final.AwayPid));
            final.HomeShowedAt = 1;
            await db.SaveChangesAsync();
        }

        await TickAsync(harness, now.AddMinutes(36));
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            var progress = await db.CompetitionProgress.SingleAsync(p => p.CompeId == 7);
            Assert.Equal((true, a.Pid, 101, "UEFA_Champions_League"), (progress.Finished, progress.ChampionPid, progress.ChampionTeamId, progress.Trophy));
        }

        foreach (var s in new[] { a, b, c, d, e })
            Pushed(s);

        // The bracket: every round complete, as the client reads it.
        var bracket = Assert.Single(await harness.DispatchAsync(e, "CMD_GET_COMPLG_TOURNAMENT", withFields: r => r.Set("compe_id", 7)));
        Assert.Equal(("UEFA_Champions_League", "MATCH_4", 4, "compeTrophyUEFA"),
            (bracket.GetString("trophy"), bracket.GetString("match_count"), bracket.GetInt32("player_list_num"), bracket.GetString("cup_file_name")));
        Assert.Equal((8, 4, 2, 1), (bracket.GetList("game_result_1").Count, bracket.GetList("game_result_2").Count,
            bracket.GetList("game_result_3").Count, bracket.GetList("game_result_4").Count));
        var finalLine = Assert.Single(bracket.GetList("game_result_4"));
        Assert.Equal(("TRUE", a.Pid), (finalLine.GetString("is_end"), finalLine.GetInt32("win_pid")));
        Assert.True(Record(finalLine, "home_score").Has("score_pk"));

        Assert.Equal("BEST_4", Assert.Single(await harness.DispatchAsync(d, "CMD_GET_COMPLG_PLAYOFF_RESULTS", withFields: r => r.Set("compe_id", 7))).GetString("my_result"));
        var mineResult = Assert.Single(await harness.DispatchAsync(a, "CMD_GET_COMPLG_PLAYOFF_RESULTS", withFields: r => r.Set("compe_id", 7)));
        Assert.Equal(("VICTORY", 1, 4), (mineResult.GetString("my_result"), mineResult.GetInt32("my_pre_rank"), mineResult.GetInt32("entry_count")));
        Assert.Equal("NON", Assert.Single(await harness.DispatchAsync(e, "CMD_GET_COMPLG_PLAYOFF_RESULTS", withFields: r => r.Set("compe_id", 7))).GetString("my_result"));

        var holders = Assert.Single(await harness.DispatchAsync(e, "CMD_GET_COMPLG_TITLEHOLDER", withFields: r => r.Set("is_ml_compe", "NO")));
        var holder = Assert.Single(holders.GetList("titleholder_list"));
        Assert.Equal((a.Pid, "ana", "Cup 7", "TEAM 101"), (holder.GetInt32("pid"), holder.GetString("name"), holder.GetString("compe_name"), holder.GetString("team_name")));
        Assert.Equal(0, Assert.Single(await harness.DispatchAsync(e, "CMD_GET_COMPLG_TITLEHOLDER", withFields: r => r.Set("is_ml_compe", "YES"))).GetInt32("titleholder_num"));
        Assert.Equal(a.Pid, Assert.Single(await harness.DispatchAsync(e, "CMD_GET_COMPLG_COMMENDATION_DATA", withFields: r => r.Set("compe_id", 7))).GetInt32("pid"));

        // Mails: through to the playoff, then the title; the Spanish speaker reads Spanish.
        var inbox = Assert.Single(await harness.DispatchAsync(a, "CMD_GET_SHORTMAILLIST")).GetList("smlist");
        Assert.Contains(inbox, m => m.GetString("mail_type") == "CUP_WR_PREPASS");
        Assert.Contains(inbox, m => m.GetString("mail_type") == "CUP_WR_FINALWIN" && m.GetString("reply_enable") == "NO");
        var cidMails = Assert.Single(await harness.DispatchAsync(c, "CMD_GET_SHORTMAILLIST")).GetList("smlist");
        Assert.Contains(cidMails, m => m.GetString("mail_type") == "CUP_WR_FINALPRIZE" && m.GetString("mes_subject")!.Contains("resultado final"));
        Assert.Contains(Assert.Single(await harness.DispatchAsync(e, "CMD_GET_SHORTMAILLIST")).GetList("smlist"), m => m.GetString("mail_type") == "CUP_NORMALEND");
    }

    [Fact]
    public async Task Without_a_playoff_the_table_leader_is_champion()
    {
        var now = DateTime.UtcNow;
        await using var harness = await HarnessAsync(s => Cup(s, 0, 7, now.AddDays(-2), now.AddHours(-3), now.AddMinutes(-10)), profile: "Pes2011Pc");
        var a = await EntrantAsync(harness, "ana", 7, 101);
        var b = await EntrantAsync(harness, "bob", 7, 102);
        await PlayAsync(harness, 7, a.Pid, b.Pid, 0, 1);

        await TickAsync(harness, now);
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        Assert.Equal((true, b.Pid), ((await db.CompetitionProgress.SingleAsync()).Finished, (await db.CompetitionProgress.SingleAsync()).ChampionPid));
        Assert.Empty(await db.CompetitionPlayoffMatches.ToListAsync());
        Assert.Empty(await db.ShortMails.ToListAsync()); // the cup mails are PES 2013's

        // PES 2011 reads the last champion at the top level.
        var holder = Assert.Single(await harness.DispatchAsync(a, "CMD_GET_COMPLG_TITLEHOLDER"));
        Assert.Equal((b.Pid, "bob"), (holder.GetInt32("pid"), holder.GetString("name")));
    }

    [Fact]
    public async Task The_bracket_seeds_by_place_and_gives_byes_to_the_top_seeds()
    {
        await using var harness = await HarnessAsync(_ => { });
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        var seeds = Enumerable.Range(1, 6).Select(i => new Player { Pid = 100 + i, CompetitionTeamId = i }).ToList();
        var bracket = CompetitionBracket.Draw(db, 9, seeds);

        // Six qualifiers: the quarter-finals are 1 v bye, 4 v 5, 2 v bye, 3 v 6; seeds 1 and 2 are through already.
        var quarters = bracket.Where(m => m.Round == 2).ToList();
        Assert.Equal((101, 0, true), (quarters[0].HomePid, quarters[0].AwayPid, quarters[0].Finished));
        Assert.Equal((104, 105, false), (quarters[1].HomePid, quarters[1].AwayPid, quarters[1].Finished));
        Assert.Equal((102, 0, true), (quarters[2].HomePid, quarters[2].AwayPid, quarters[2].Finished));
        Assert.Equal((103, 106, false), (quarters[3].HomePid, quarters[3].AwayPid, quarters[3].Finished));
        Assert.Equal(2, CompetitionBracket.CurrentRound(bracket));
        Assert.Equal(101, CompetitionBracket.At(bracket, 3, 0)!.HomePid);
        Assert.Equal("NON", CompetitionBracket.ResultOf(bracket, 999).Result);
    }
}
