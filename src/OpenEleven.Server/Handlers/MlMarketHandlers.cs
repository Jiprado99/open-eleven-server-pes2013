// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.MasterLeague;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// The Master League Online transfer market. The market is not exclusive: any number of clubs can
/// own the same footballer, and each owner raises his price (owner_num). The client searches its
/// own database; the server is only asked about one footballer at a time (CMD_GET_ML_TRADE_PLAYER)
/// and to buy, sell or extend. Key names: the key tables next to these commands in pes2013.exe
/// (around 0x01016xxx of the dump) and FUN_004030c0's dispatch near all_functions.c:544600.
/// </summary>
public sealed class MlMarketHandlers(MlLeague league)
{
    private const int MaxContractTerm = 60;

    /// <summary>Request: gameplayer_id. Reply: the <c>trade_player</c> record.</summary>
    [Command("CMD_GET_ML_TRADE_PLAYER", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> TradePlayer(CommandContext ctx)
    {
        var id = ctx.Request.GetInt32("gameplayer_id", 0);
        if (id <= 0 || !league.Catalog.Exists(id))
            return [ctx.Fail("ERR_INVALIDARG")];

        var owners = await league.OwnersAsync(id, ctx.CancellationToken);
        var price = league.Price(id, owners);
        var overall = league.Catalog.Player(id).Overall;
        var rated = await league.Db.MlTeamPlayers.Where(p => p.GamePlayerId == id)
            .Select(p => new { p.Games, p.RatingSum, p.RatingCount }).ToListAsync(ctx.CancellationToken);
        var ratings = rated.Sum(r => r.RatingCount);
        var activeTeams = await league.Db.MlTeams.CountAsync(ctx.CancellationToken);

        return [ctx.Ok().Set("trade_player", new KvMessage()
            .Set("gameplayer_id", id)
            .Set("market_price", price)
            .Set("contract_price_list", Prices(league.Rules.ContractPrices(price)))
            .Set("base_price", league.Rules.BasePrice(overall))
            .Set("contract_price_base", league.Rules.BaseSalary(overall))
            .Set("owner_num", owners)
            .Set("active_team_num", activeTeams)
            .Set("games", rated.Sum(r => r.Games))
            .Set("rating_avg", ratings == 0 ? 0 : (int)Math.Round((double)rated.Sum(r => r.RatingSum) / ratings)))];
    }

    /// <summary>
    /// Request: gameplayer_id. Pays the market price and signs him for NewContractMatches.
    /// Reply: market_price, contract_price (salary per match), enable_buy_num, team_funds.
    /// </summary>
    [Command("CMD_BUY_ML_MARKET_GAMEPLAYER", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Buy(CommandContext ctx)
    {
        var id = ctx.Request.GetInt32("gameplayer_id", 0);
        var team = await league.TeamAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (team is null || id <= 0 || !league.Catalog.Exists(id))
            return [ctx.Fail("ERR_NOTFOUND")];
        if (await MlCompetitions.IsEnteredAsync(league, team.Pid, ctx.CancellationToken))
            return [ctx.Fail("ERR_COMPE_ENTRY")];

        var squad = await league.SquadAsync(team.Pid, ctx.CancellationToken);
        if (squad.Any(p => p.GamePlayerId == id))
            return [ctx.Fail("ERR_NOTFOUND")];
        if (squad.Count >= league.Rules.Options.MaxSquad)
            return [ctx.Fail("ERR_TEAM_MEMBER_FULL")];
        league.RefreshBuyPeriod(team);
        if (team.BuysLeft <= 0)
            return [ctx.Fail("ERR_ENABLE_BUY_NUM")];

        var price = league.Price(id, await league.OwnersAsync(id, ctx.CancellationToken));
        if (team.Funds < price)
            return [ctx.Fail("ERR_INSUFFICIENT_FUNDS")];

        team.Funds -= price;
        team.BuysLeft--;
        team.LastPurchases++;
        team.UpdatedAt = league.Now;
        var used = squad.Select(p => p.UniformNumber).ToHashSet();
        league.Db.MlTeamPlayers.Add(new MlTeamPlayer
        {
            Pid = team.Pid,
            GamePlayerId = id,
            UniformNumber = MlLeague.FreeNumber(used),
            ContractTerm = league.Rules.Options.NewContractMatches,
            BuyPrice = price,
            JoinedAt = league.Now,
        });
        league.Event(team.Pid, "BUY", id, text: league.Catalog.Player(id).Name);
        await league.Db.SaveChangesAsync(ctx.CancellationToken);
        await league.RecordPricesAsync([id], ctx.CancellationToken);

        return [ctx.Ok()
            .Set("market_price", price)
            .Set("contract_price", league.Rules.Salary(price))
            .Set("enable_buy_num", team.BuysLeft)
            .Set("team_funds", team.Funds)];
    }

    /// <summary>Request: gameplayer_id. Returns SellPercent of the market price. Reply: market_price, enable_buy_num, team_funds.</summary>
    [Command("CMD_SELL_ML_MARKET_GAMEPLAYER", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Sell(CommandContext ctx)
    {
        var id = ctx.Request.GetInt32("gameplayer_id", 0);
        var team = await league.TeamAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (team is null)
            return [ctx.Fail("ERR_DATANOTFOUND")];
        if (await MlCompetitions.IsEnteredAsync(league, team.Pid, ctx.CancellationToken))
            return [ctx.Fail("ERR_COMPE_ENTRY")];
        var squad = await league.SquadAsync(team.Pid, ctx.CancellationToken);
        var player = squad.FirstOrDefault(p => p.GamePlayerId == id);
        if (player is null)
            return [ctx.Fail("ERR_DATANOTFOUND")];
        if (squad.Count <= league.Rules.Options.MinSquad)
            return [ctx.Fail("ERR_TEAM_MEMBER_LACK")];

        var price = league.Rules.SellPrice(league.Price(id, await league.OwnersAsync(id, ctx.CancellationToken)));
        team.Funds += price;
        team.UpdatedAt = league.Now;
        league.RefreshBuyPeriod(team);
        league.Db.MlTeamPlayers.Remove(player);
        league.Event(team.Pid, "SELL", id, text: league.Catalog.Player(id).Name);
        await league.Db.SaveChangesAsync(ctx.CancellationToken);
        await league.RecordPricesAsync([id], ctx.CancellationToken);

        return [ctx.Ok().Set("market_price", price).Set("enable_buy_num", team.BuysLeft).Set("team_funds", team.Funds)];
    }

    /// <summary>
    /// Request: gameplayer_id, extend_contract_term=CONTRACT_TYPE1..3. Adds ExtendMatches[type]
    /// matches for the price in contract_price_list. Reply: contract_price, team_funds.
    /// </summary>
    [Command("CMD_EXTEND_ML_MARKET_GAMEPLAYER", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Extend(CommandContext ctx)
    {
        var id = ctx.Request.GetInt32("gameplayer_id", 0);
        var type = MlRules.ContractIndex(ctx.Request.GetString("extend_contract_term"));
        var team = await league.TeamAsync(ctx.Session.Pid, ctx.CancellationToken);
        var player = team is null ? null
            : await league.Db.MlTeamPlayers.FirstOrDefaultAsync(p => p.Pid == team.Pid && p.GamePlayerId == id, ctx.CancellationToken);
        if (team is null || player is null || type < 0 || type >= league.Rules.Options.ExtendMatchesOrDefault().Length)
            return [ctx.Fail("ERR_NOTFOUND")];
        var matches = league.Rules.Options.ExtendMatchesOrDefault()[type];
        if (player.ContractTerm + matches > MaxContractTerm)
            return [ctx.Fail("ERR_OVEREXTEND")];

        var cost = league.Rules.ContractPrices(league.Price(id, await league.OwnersAsync(id, ctx.CancellationToken)))[type];
        if (team.Funds < cost)
            return [ctx.Fail("ERR_INSUFFICIENT_FUNDS")];

        team.Funds -= cost;
        player.ContractTerm += matches;
        await league.Db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok().Set("contract_price", cost).Set("team_funds", team.Funds)];
    }

    /// <summary>The watch list, in the market list layout (gameplayer_list, total_gameplayer_num).</summary>
    [Command("CMD_GET_ML_MARKET_CHECKLIST", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetChecklist(CommandContext ctx)
    {
        var ids = await league.Db.MlChecklist.Where(c => c.Pid == ctx.Session.Pid).OrderBy(c => c.AddedAt)
            .Select(c => c.GamePlayerId).ToListAsync(ctx.CancellationToken);
        var owners = await league.OwnersAsync(ids, ctx.CancellationToken);
        var list = new List<KvMessage>();
        foreach (var id in ids)
        {
            var stats = await league.StatsAsync(id, owners[id], ctx.CancellationToken);
            list.Add(new KvMessage()
                .Set("gameplayer_id", id)
                .Set("market_price", stats.Now)
                .Set("market_price_old", stats.ThisWeek)
                .Set("market_price_max", stats.Max)
                .Set("market_price_min", stats.Min)
                .Set("overall", league.Catalog.Player(id).Overall)
                .Set("is_trade_stop", "NO")
                .Set("trade_stop_date", 0)
                .Set("trade_stop_term", 0)
                .Set("contract_price", league.Rules.Salary(stats.Now))
                .Set("owner_num", owners[id])
                .Set("ml_condition", "NORMAL"));
        }

        return [ctx.Ok().SetList("gameplayer_list_num", "gameplayer_list", list).Set("total_gameplayer_num", list.Count)];
    }

    [Command("CMD_ADD_ML_MARKET_CHECKLIST", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> AddChecklist(CommandContext ctx)
    {
        var id = ctx.Request.GetInt32("gameplayer_id", 0);
        if (id <= 0 || !league.Catalog.Exists(id))
            return [ctx.Fail("ERR_DATABASE")];
        var count = await league.Db.MlChecklist.CountAsync(c => c.Pid == ctx.Session.Pid, ctx.CancellationToken);
        if (count < 100 && !await league.Db.MlChecklist.AnyAsync(c => c.Pid == ctx.Session.Pid && c.GamePlayerId == id, ctx.CancellationToken))
        {
            league.Db.MlChecklist.Add(new MlChecklistItem { Pid = ctx.Session.Pid, GamePlayerId = id, AddedAt = league.Now });
            await league.Db.SaveChangesAsync(ctx.CancellationToken);
        }

        return [ctx.Ok()];
    }

    [Command("CMD_DELETE_ML_MARKET_CHECKLIST", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> DeleteChecklist(CommandContext ctx)
    {
        var id = ctx.Request.GetInt32("gameplayer_id", 0);
        await league.Db.MlChecklist.Where(c => c.Pid == ctx.Session.Pid && c.GamePlayerId == id).ExecuteDeleteAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>
    /// Price watch of the market screen. ponytail: the push the client expects after the
    /// subscription is not known; it is acknowledged and nothing is pushed.
    /// </summary>
    [Command("CMD_REQWATCH_ML_MARKET_PRICE", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    [Command("CMD_ACK_ML_MARKET_PRICE", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    public ValueTask<KvMessage[]> PriceWatch(CommandContext ctx) => Reply.Of(ctx.Ok());

    internal static KvArray Prices(IEnumerable<long> values) => new(values.Select(v => (object?)v).ToArray());
}
