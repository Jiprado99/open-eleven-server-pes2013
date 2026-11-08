// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Data;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.MasterLeague;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

/// <summary>Master League Online (PES 2013) against a small made-up catalogue.</summary>
public class MasterLeagueTests
{
    internal const int StartTeam = 140;
    internal const int Star = 7511;      // overall 95
    internal const int Average = 9001;   // overall 75

    internal static MlCatalogData Catalogue()
    {
        var players = Enumerable.Range(0, 20).Select(i => new MlFootballer(55244 + i, $"ML {i}", 60 + i, i == 0)).ToList();
        players.Add(new MlFootballer(Star, "STAR", 95, false));
        players.Add(new MlFootballer(Average, "AVERAGE", 75, false));
        return new MlCatalogData
        {
            Players = players,
            Teams =
            [
                new MlSquad(StartTeam, "MASTERLEAGUE DEFAULT", Enumerable.Range(0, 20).Select(i => new MlSquadMember(55244 + i, i + 1)).ToList()),
                new MlSquad(122, "S.S. LAZIO", [new MlSquadMember(Average, 10)]),
            ],
        };
    }

    internal static Task<ServerHarness> HarnessAsync(Action<Dictionary<string, string?>>? configure = null)
        => ServerHarness.CreateAsync(s =>
        {
            s["Server:GameProfile"] = "Pes2013Pc";
            s["Server:MasterLeague:MinSquad"] = "18";
            configure?.Invoke(s);
        }, services => services.AddSingleton(new MlCatalog(Catalogue())));

    internal static Session Manager(ServerHarness harness, int pid)
    {
        var session = harness.NewSession(ServiceRole.Menu, SessionState.PlayerSelected);
        session.Pid = pid;
        session.PlayerName = $"P{pid}";
        return session;
    }

    internal static async Task CreateClubAsync(ServerHarness harness, Session session, string name = "S.S. LAZIO")
    {
        var reply = Assert.Single(await harness.DispatchAsync(session, "CMD_CREATE_ML_MYTEAM", withFields: m => m
            .Set("team_name", name).Set("team_name_short", "LAZ").Set("base_team_id", 122).Set("check_team_name", "NO")));
        Assert.Equal("NOERR", reply.GetString("result"));
    }

    [Fact]
    public async Task A_new_manager_has_no_club_until_creating_one_with_the_master_league_squad()
    {
        await using var harness = await HarnessAsync();
        var session = Manager(harness, 11);

        Assert.Equal("NOERR", Assert.Single(await harness.DispatchAsync(session, "CMD_JOIN_ML_MODE")).GetString("result"));
        Assert.Equal("ERR_NOTFOUND", Assert.Single(await harness.DispatchAsync(session, "CMD_GET_ML_MYTEAM")).GetString("result"));

        await CreateClubAsync(harness, session);
        var team = Assert.Single(await harness.DispatchAsync(session, "CMD_GET_ML_MYTEAM"));
        Assert.Equal("NOERR", team.GetString("result"));
        var record = Assert.IsType<KvMessage>(team.GetValue("myteam"));
        Assert.Equal("CREATED", record.GetString("team_status"));
        Assert.Equal("S.S. LAZIO", record.GetString("team_name"));
        Assert.Equal(122, record.GetInt32("base_team_id"));
        Assert.Equal(5_000_000, record.GetInt32("team_funds"));
        Assert.Equal(74, record.GetInt32("team_power")); // best eleven of overall 60..79
        Assert.Equal(5, record.GetInt32("enable_buy_num"));

        var squad = Assert.Single(await harness.DispatchAsync(session, "CMD_GET_ML_MYTEAM_GAMEPLAYERLIST"));
        var list = squad.GetList("player_list");
        Assert.Equal(20, squad.GetInt32("player_list_num"));
        Assert.Equal(20, list.Count);
        Assert.All(list, p =>
        {
            Assert.IsType<KvMessage>(p.GetValue("injury"));
            Assert.Equal(3, Assert.IsType<KvArray>(p.GetValue("contract_price_list")).Values.Count);
            Assert.Equal("EXCELENT", p.GetString("ml_condition"));
            Assert.Equal(1, p.GetInt32("owner_num"));
        });

        Assert.Equal("ERR_ALREADYEXISTS", Assert.Single(await harness.DispatchAsync(session, "CMD_CREATE_ML_MYTEAM", withFields: m => m
            .Set("team_name", "OTHER").Set("team_name_short", "OTH").Set("base_team_id", 122))).GetString("result"));
    }

    [Fact]
    public async Task Club_names_are_checked_and_a_name_check_creates_nothing()
    {
        await using var harness = await HarnessAsync(s => s["Server:Protocol:BlockedTerms:0"] = "badword");
        var session = Manager(harness, 12);

        Assert.Equal("ERR_NGWORD", Assert.Single(await harness.DispatchAsync(session, "CMD_CREATE_ML_MYTEAM", withFields: m => m
            .Set("team_name", "badword FC").Set("team_name_short", "BAD").Set("base_team_id", 122))).GetString("result"));
        Assert.Equal("NOERR", Assert.Single(await harness.DispatchAsync(session, "CMD_CREATE_ML_MYTEAM", withFields: m => m
            .Set("team_name", "Good FC").Set("team_name_short", "GFC").Set("base_team_id", 122).Set("check_team_name", "YES"))).GetString("result"));
        Assert.Equal("ERR_NOTFOUND", Assert.Single(await harness.DispatchAsync(session, "CMD_GET_ML_MYTEAM")).GetString("result"));
        Assert.Equal("ERR_NOTFOUND", Assert.Single(await harness.DispatchAsync(session, "CMD_CREATE_ML_MYTEAM", withFields: m => m
            .Set("team_name", "Good FC").Set("team_name_short", "GFC").Set("base_team_id", 9999))).GetString("result"));
    }

    [Fact]
    public async Task Name_base_and_shirt_numbers_change_and_a_reset_restores_the_starting_club()
    {
        await using var harness = await HarnessAsync();
        var session = Manager(harness, 13);
        await CreateClubAsync(harness, session);

        await harness.DispatchAsync(session, "CMD_SET_ML_MYTEAM_NAME", withFields: m => m.Set("team_name", "LAZIO ML").Set("team_name_short", "LZM"));
        await harness.DispatchAsync(session, "CMD_SET_ML_MYTEAM_BASE", withFields: m => m.Set("base_team_id", StartTeam));
        // 55244 wears 1 and 55245 wears 2: giving 55244 the 2 swaps them.
        Assert.Equal("NOERR", Assert.Single(await harness.DispatchAsync(session, "CMD_SET_ML_MYTEAM_UNIFORMNUMBER",
            withFields: m => m.Set("gameplayer_id", 55244).Set("uniform_number", 2))).GetString("result"));

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            var team = await db.MlTeams.SingleAsync(t => t.Pid == 13);
            Assert.Equal(("LAZIO ML", "LZM", StartTeam), (team.Name, team.ShortName, team.BaseTeamId));
            var numbers = await db.MlTeamPlayers.Where(p => p.Pid == 13 && p.GamePlayerId <= 55245).ToDictionaryAsync(p => p.GamePlayerId, p => p.UniformNumber);
            Assert.Equal(2, numbers[55244]);
            Assert.Equal(1, numbers[55245]);
            team.Funds = 1;
            await db.SaveChangesAsync();
        }

        Assert.Equal("NOERR", Assert.Single(await harness.DispatchAsync(session, "CMD_RESET_ML_MYTEAM")).GetString("result"));
        var record = Assert.IsType<KvMessage>(Assert.Single(await harness.DispatchAsync(session, "CMD_GET_ML_MYTEAM")).GetValue("myteam"));
        Assert.Equal(5_000_000, record.GetInt32("team_funds"));
        Assert.Equal("LAZIO ML", record.GetString("team_name"));
    }

    [Fact]
    public void The_game_reader_unpacks_wesys_and_parses_footballers_and_squads()
    {
        var players = new byte[28 + 2 * 228];
        void Footballer(int at, int id, string name, byte first, int ability)
        {
            BitConverter.GetBytes(id).CopyTo(players, at + 4);
            Encoding.UTF8.GetBytes(name).CopyTo(players, at + 54);
            players[at + 125] = first;
            foreach (var i in new[] { 126, 127, 128, 129, 130, 132, 133, 134, 135, 136, 137, 138, 139, 140, 141, 143, 144, 145, 146, 147, 148 })
                players[at + i] = (byte)(ability | 0x80); // bit 7 is not part of the value
        }

        Footballer(28, 7511, "MESSI", 67, 90);
        Footballer(28 + 228, 4072, "VALDÉS", 0, 70);
        var parsed = GameDataReader.ParsePlayers(GameDataReader.Unpack(Wesys(players)));
        Assert.Equal([new MlFootballer(7511, "MESSI", 90, false), new MlFootballer(4072, "VALDÉS", 70, true)], parsed);

        var squads = new byte[32 + 164];
        BitConverter.GetBytes(108).CopyTo(squads, 32);
        BitConverter.GetBytes(866).CopyTo(squads, 36);
        BitConverter.GetBytes(7511).CopyTo(squads, 40);
        squads.AsSpan(32 + 132, 32).Fill(0xFF);
        squads[32 + 132] = 6;
        squads[32 + 133] = 10;
        var squad = Assert.Single(GameDataReader.ParseSquads(squads));
        Assert.Equal(108, squad.Id);
        Assert.Equal([new MlSquadMember(866, 6), new MlSquadMember(7511, 10)], squad.Members);
    }

    private static byte[] Wesys(byte[] raw)
    {
        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);
        var header = new byte[16];
        new byte[] { 0, 1, 1, (byte)'W', (byte)'E', (byte)'S', (byte)'Y', (byte)'S' }.CopyTo(header, 0);
        BitConverter.GetBytes((int)packed.Length).CopyTo(header, 8);
        BitConverter.GetBytes(raw.Length).CopyTo(header, 12);
        return [.. header, .. packed.ToArray()];
    }

    [Fact]
    public void Prices_grow_with_overall_and_demand()
    {
        var rules = new MlRules(new MasterLeagueOptions());
        Assert.Equal(300_000, rules.BasePrice(75));
        Assert.True(rules.BasePrice(95) > 20_000_000);
        Assert.Equal(306_000, rules.MarketPrice(75, 1));
        Assert.Equal(600_000, rules.MarketPrice(75, 500)); // demand capped at +100%
        Assert.Equal(240_000, rules.SellPrice(300_000));
        Assert.Equal("ML_DIVISION_5", MlRules.DivisionLabel(0));
        Assert.Equal(2, rules.DivisionFor(100));
    }
}
