// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.MasterLeague;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Master League Online after a match: the client's reports (sent by the match reporter right after
/// CMD_SET_COMMU_MATCHINFO, all_functions.c:485151) and the money and record screens. The match
/// itself is settled by <see cref="MlMatchSettlement"/> from MatchResults.
/// </summary>
public sealed class MlMatchHandlers(MlLeague league)
{
    /// <summary>Request: gameplayer_id_list_num, gameplayer_id_list (the footballers of this side who played).</summary>
    [Command("CMD_SEND_ML_GAMEPLAYERS", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    public async ValueTask<KvMessage[]> SendGamePlayers(CommandContext ctx)
    {
        await MlMatchSettlement.PlayedAsync(league, ctx.Session.Pid, Ids(ctx.Request.GetValue("gameplayer_id_list")), ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>Request: rating_num, rating_list[gameplayer_id, rating]: the manager's marks for the match.</summary>
    [Command("CMD_SET_ML_MYTEAM_PLAYER_RATING", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    public async ValueTask<KvMessage[]> SetRatings(CommandContext ctx)
    {
        var ratings = ctx.Request.GetList("rating_list")
            .Select(r => (Id: r.GetInt32("gameplayer_id", 0), Rating: r.GetInt32("rating", 0)))
            .Where(r => r.Id > 0 && r.Rating is > 0 and <= 100)
            .GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last().Rating);
        if (ratings.Count == 0)
            return [ctx.Ok()];

        var ids = ratings.Keys.ToList();
        foreach (var p in await league.Db.MlTeamPlayers.Where(p => p.Pid == ctx.Session.Pid && ids.Contains(p.GamePlayerId)).ToListAsync(ctx.CancellationToken))
        {
            p.LastRating = ratings[p.GamePlayerId];
            p.RatingSum += p.LastRating;
            p.RatingCount++;
        }

        await league.Db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>The last ML match's takings: income, prize_income, cards, purchases, team_funds, fb_bonus, derby_income.</summary>
    [Command("CMD_GET_ML_MATCHINCOME_INFO", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    public async ValueTask<KvMessage[]> MatchIncome(CommandContext ctx)
    {
        var team = await league.TeamAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (team is null)
            return [ctx.Fail("ERR_NOTFOUND")];
        return [ctx.Ok()
            .Set("income", team.LastIncome)
            .Set("prize_income", team.LastPrizeIncome)
            .Set("yellowcard_count", team.LastYellowCards)
            .Set("redcard_count", team.LastRedCards)
            .Set("purchase_count", team.LastPurchases)
            .Set("team_funds", team.Funds)
            .Set("fb_bonus", 0)
            .Set("derby_income", team.LastDerbyIncome)];
    }

    [Command("CMD_GET_ML_MATCHEXPENSE", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    public async ValueTask<KvMessage[]> MatchExpense(CommandContext ctx)
    {
        var team = await league.TeamAsync(ctx.Session.Pid, ctx.CancellationToken);
        return [ctx.Ok().Set("expense", team?.LastExpense ?? 0)];
    }

    /// <summary>The club's record: match_record{win_num, lose_num, draw_num}.</summary>
    [Command("CMD_GET_ML_MATCHRECORD", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    public async ValueTask<KvMessage[]> MatchRecord(CommandContext ctx)
    {
        var team = await league.TeamAsync(ctx.Request.GetInt32("pid", ctx.Session.Pid), ctx.CancellationToken);
        return [ctx.Ok().Set("match_record", new KvMessage()
            .Set("win_num", team?.Wins ?? 0)
            .Set("lose_num", team?.Losses ?? 0)
            .Set("draw_num", team?.Draws ?? 0))];
    }

    /// <summary>The club's place by team power, funds and rating (ERR_NOTFOUND without a club).</summary>
    [Command("CMD_GET_ML_MYRANKING", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    public async ValueTask<KvMessage[]> MyRanking(CommandContext ctx)
    {
        var mine = await MlRankings.MyRankingAsync(league, ctx.Request.GetInt32("pid", ctx.Session.Pid), ctx.CancellationToken);
        if (mine is null)
            return [ctx.Fail("ERR_NOTFOUND")];
        var reply = ctx.Ok();
        foreach (var (key, value) in mine.Fields)
            reply.Set(key, value);
        return [reply];
    }

    /// <summary>Ids from a scalar list (gameplayer_id_list=[1,2]) or a list of records.</summary>
    internal static List<int> Ids(object? value) => value switch
    {
        KvArray array => array.Values.Select(v => int.TryParse(v?.ToString(), out var n) ? n : 0).Where(n => n > 0).ToList(),
        IReadOnlyList<KvMessage> list => list.Select(m => m.GetInt32("gameplayer_id", 0)).Where(n => n > 0).ToList(),
        string s => s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x.Trim('[', ']', ' '), out var n) ? n : 0).Where(n => n > 0).ToList(),
        _ => [],
    };
}
