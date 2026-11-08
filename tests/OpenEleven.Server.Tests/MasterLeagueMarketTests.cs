// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Data;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.MasterLeague;
using OpenEleven.Server.State;
using static OpenEleven.Server.Tests.MasterLeagueTests;

namespace OpenEleven.Server.Tests;

/// <summary>The Master League Online transfer market and its HTTP urls.</summary>
public class MasterLeagueMarketTests
{
    private static async Task<string> ResultAsync(ServerHarness harness, Session session, string msg, Action<KvMessage>? fields = null)
        => Assert.Single(await harness.DispatchAsync(session, msg, withFields: fields)).GetString("result")!;

    private static async Task SetFundsAsync(ServerHarness harness, int pid, long funds)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        var team = await db.MlTeams.SingleAsync(t => t.Pid == pid);
        team.Funds = funds;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Buying_pays_the_market_price_and_a_second_owner_raises_it()
    {
        await using var harness = await HarnessAsync();
        var first = Manager(harness, 21);
        var second = Manager(harness, 22);
        await CreateClubAsync(harness, first);
        await CreateClubAsync(harness, second);

        var info = Assert.IsType<KvMessage>(Assert.Single(await harness.DispatchAsync(first, "CMD_GET_ML_TRADE_PLAYER",
            withFields: m => m.Set("gameplayer_id", Average))).GetValue("trade_player"));
        Assert.Equal(300_000, info.GetInt32("market_price"));
        Assert.Equal(0, info.GetInt32("owner_num"));

        var bought = Assert.Single(await harness.DispatchAsync(first, "CMD_BUY_ML_MARKET_GAMEPLAYER", withFields: m => m.Set("gameplayer_id", Average)));
        Assert.Equal("NOERR", bought.GetString("result"));
        Assert.Equal(300_000, bought.GetInt32("market_price"));
        Assert.Equal(4_700_000, bought.GetInt32("team_funds"));
        Assert.Equal(4, bought.GetInt32("enable_buy_num"));

        // Owned once now: +2%.
        var again = Assert.Single(await harness.DispatchAsync(second, "CMD_BUY_ML_MARKET_GAMEPLAYER", withFields: m => m.Set("gameplayer_id", Average)));
        Assert.Equal(306_000, again.GetInt32("market_price"));

        Assert.Equal("ERR_NOTFOUND", await ResultAsync(harness, first, "CMD_BUY_ML_MARKET_GAMEPLAYER", m => m.Set("gameplayer_id", Average)));
        Assert.Equal("ERR_INSUFFICIENT_FUNDS", await ResultAsync(harness, first, "CMD_BUY_ML_MARKET_GAMEPLAYER", m => m.Set("gameplayer_id", Star)));

        var squad = Assert.Single(await harness.DispatchAsync(first, "CMD_GET_ML_MYTEAM_GAMEPLAYERLIST")).GetList("player_list");
        var signed = Assert.Single(squad, p => p.GetInt32("gameplayer_id") == Average);
        Assert.Equal(300_000, signed.GetInt32("buy_price"));
        Assert.Equal(2, signed.GetInt32("owner_num"));
        Assert.Equal(10, signed.GetInt32("contract_term"));
    }

    [Fact]
    public async Task Purchases_are_limited_per_period_and_by_squad_size()
    {
        await using var harness = await HarnessAsync(s =>
        {
            s["Server:MasterLeague:BuysPerPeriod"] = "1";
            s["Server:MasterLeague:MaxSquad"] = "21";
        });
        var session = Manager(harness, 23);
        await CreateClubAsync(harness, session);
        await SetFundsAsync(harness, 23, 100_000_000);

        Assert.Equal("NOERR", await ResultAsync(harness, session, "CMD_BUY_ML_MARKET_GAMEPLAYER", m => m.Set("gameplayer_id", Average)));
        Assert.Equal("ERR_TEAM_MEMBER_FULL", await ResultAsync(harness, session, "CMD_BUY_ML_MARKET_GAMEPLAYER", m => m.Set("gameplayer_id", Star)));

        Assert.Equal("NOERR", await ResultAsync(harness, session, "CMD_SELL_ML_MARKET_GAMEPLAYER", m => m.Set("gameplayer_id", Average)));
        Assert.Equal("ERR_ENABLE_BUY_NUM", await ResultAsync(harness, session, "CMD_BUY_ML_MARKET_GAMEPLAYER", m => m.Set("gameplayer_id", Star)));
    }

    [Fact]
    public async Task Selling_returns_part_of_the_price_but_never_below_the_minimum_squad()
    {
        await using var harness = await HarnessAsync(s => s["Server:MasterLeague:MinSquad"] = "19");
        var session = Manager(harness, 24);
        await CreateClubAsync(harness, session);

        var sold = Assert.Single(await harness.DispatchAsync(session, "CMD_SELL_ML_MARKET_GAMEPLAYER", withFields: m => m.Set("gameplayer_id", 55263)));
        Assert.Equal("NOERR", sold.GetString("result"));
        // Overall 79, owned once before the sale: 300k × 1.25^4 × 1.02 → 747k, 80% → 598k.
        Assert.Equal(5_000_000 + sold.GetInt32("market_price"), sold.GetInt32("team_funds"));
        Assert.InRange(sold.GetInt32("market_price"), 590_000, 600_000);

        Assert.Equal("ERR_TEAM_MEMBER_LACK", await ResultAsync(harness, session, "CMD_SELL_ML_MARKET_GAMEPLAYER", m => m.Set("gameplayer_id", 55262)));
        Assert.Equal("ERR_DATANOTFOUND", await ResultAsync(harness, session, "CMD_SELL_ML_MARKET_GAMEPLAYER", m => m.Set("gameplayer_id", Star)));
    }

    [Fact]
    public async Task Contracts_extend_by_type_until_the_limit()
    {
        await using var harness = await HarnessAsync();
        var session = Manager(harness, 25);
        await CreateClubAsync(harness, session);

        var reply = Assert.Single(await harness.DispatchAsync(session, "CMD_EXTEND_ML_MARKET_GAMEPLAYER",
            withFields: m => m.Set("gameplayer_id", 55244).Set("extend_contract_term", "CONTRACT_TYPE1")));
        Assert.Equal("NOERR", reply.GetString("result"));
        Assert.Equal(5_000_000 - reply.GetInt32("contract_price"), reply.GetInt32("team_funds"));

        // The starting contracts run 30 matches; +10 made 40, +30 would pass 60.
        Assert.Equal("ERR_OVEREXTEND", await ResultAsync(harness, session, "CMD_EXTEND_ML_MARKET_GAMEPLAYER",
            m => m.Set("gameplayer_id", 55244).Set("extend_contract_term", "CONTRACT_TYPE3")));
        Assert.Equal("ERR_NOTFOUND", await ResultAsync(harness, session, "CMD_EXTEND_ML_MARKET_GAMEPLAYER",
            m => m.Set("gameplayer_id", 55244).Set("extend_contract_term", "SOMETHING")));
    }

    [Fact]
    public async Task The_watch_list_adds_lists_and_removes()
    {
        await using var harness = await HarnessAsync();
        var session = Manager(harness, 26);
        await harness.DispatchAsync(session, "CMD_ADD_ML_MARKET_CHECKLIST", withFields: m => m.Set("gameplayer_id", Star));
        await harness.DispatchAsync(session, "CMD_ADD_ML_MARKET_CHECKLIST", withFields: m => m.Set("gameplayer_id", Star));
        await harness.DispatchAsync(session, "CMD_ADD_ML_MARKET_CHECKLIST", withFields: m => m.Set("gameplayer_id", Average));

        var list = Assert.Single(await harness.DispatchAsync(session, "CMD_GET_ML_MARKET_CHECKLIST"));
        Assert.Equal(2, list.GetInt32("gameplayer_list_num"));
        Assert.Equal(95, list.GetList("gameplayer_list")[0].GetInt32("overall"));

        await harness.DispatchAsync(session, "CMD_DELETE_ML_MARKET_CHECKLIST", withFields: m => m.Set("gameplayer_id", Star));
        Assert.Equal(1, Assert.Single(await harness.DispatchAsync(session, "CMD_GET_ML_MARKET_CHECKLIST")).GetInt32("gameplayer_list_num"));
    }

    [Fact]
    public async Task The_market_urls_are_announced_and_answer_history_rankings_and_the_param_size()
    {
        await using var harness = await HarnessAsync();
        var urls = Assert.Single(await harness.DispatchAsync(harness.NewSession(ServiceRole.Gate, SessionState.Connected), "CMD_GET_URLLIST")).GetList("ulist");
        Assert.EndsWith("/ml/ml_pricerankinG".ToLowerInvariant(), Assert.Single(urls, u => u.GetString("type") == "ML_PRICERANKING").GetString("url"));
        Assert.Single(urls, u => u.GetString("type") == "ML_MARKET_PLAYER_PARAM_SIZE");

        var session = Manager(harness, 27);
        await CreateClubAsync(harness, session);
        await harness.DispatchAsync(session, "CMD_BUY_ML_MARKET_GAMEPLAYER", withFields: m => m.Set("gameplayer_id", Average));

        await using var scope = harness.Services.CreateAsyncScope();
        var league = scope.ServiceProvider.GetRequiredService<MlLeague>();
        async Task<KvMessage> Http(string type, string body) => new KvReader().Parse(await MlHttp.RenderAsync(league, type, body, 0, default));

        var size = await Http("ML_MARKET_PLAYER_PARAM_SIZE", "msg=\"CMD_GET_ML_PLAYERPARAMSIZE\"");
        Assert.Equal((0, 0), (size.GetInt32("file_size", -1), size.GetInt32("uncompressed_file_size", -1)));

        var history = await Http("ML_GAMEPLAYERHISTORY", $"msg=\"CMD_GET_ML_MARKET_GAMEPLAYERHISTORY\",gameplayer_id={Average}");
        Assert.Equal("NOERR", history.GetString("result"));
        Assert.Equal(1, history.GetInt32("owner_num"));
        Assert.Equal(1, history.GetInt32("history_num"));
        Assert.Equal(306_000, history.GetList("price_history")[0].GetInt32("market_price")); // recorded after the buy: one owner

        var price = await Http("ML_PRICERANKING", "msg=CMD_GET_ML_MARKET_PRICERANKING&rqid=3");
        Assert.Equal(Star, price.GetList("rank_list")[0].GetInt32("gameplayer_id"));
        var popular = await Http("ML_POPULARRANKING", "");
        Assert.Equal(1, popular.GetList("rank_list")[0].GetInt32("owner_num"));
        Assert.Equal(21, popular.GetInt32("rank_list_num"));
    }
}
