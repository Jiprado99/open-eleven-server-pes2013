// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenEleven.Protocol.Kv;

namespace OpenEleven.Server.MasterLeague;

/// <summary>
/// The Master League Online commands the client sends over HTTP. pes2013.exe pairs each command
/// with a url type (table at 0x0141f07c of the dump); <c>GetUrlList</c> announces them as
/// <c>/ml/&lt;type&gt;</c> and the body of each answer is the reply itself, like /nickname.
/// <list type="table">
/// <item>ML_RANKING, ML_RANKING_BYID: CMD_GET_ML_RANKING(_BYID)</item>
/// <item>ML_DIVISIONRANKING(_BYID): CMD_GET_ML_DIVISIONRANKING(_BYID)</item>
/// <item>ML_RIVAL_RANK: CMD_GET_ML_RIVAL_RANK, _LAST, _STATUS, _BONUS</item>
/// <item>ML_PRICERANKING, ML_GAINRANKING, ML_POPULARRANKING: CMD_GET_ML_MARKET_*RANKING</item>
/// <item>ML_GAMEPLAYERHISTORY: CMD_GET_ML_MARKET_GAMEPLAYERHISTORY</item>
/// <item>ML_MARKET_PLAYER_PARAM_SIZE: CMD_GET_ML_PLAYERPARAMSIZE</item>
/// </list>
/// ponytail: how the client frames the request body is read from the decompilation, not from a
/// capture; <see cref="ParseRequest"/> takes key=value pairs in either the socket or the form
/// layout, and the command name decides between the commands that share a url.
/// </summary>
public static class MlHttp
{
    public static readonly string[] UrlTypes =
    [
        "ML_RANKING", "ML_RANKING_BYID", "ML_DIVISIONRANKING", "ML_DIVISIONRANKING_BYID",
        "ML_PRICERANKING", "ML_GAINRANKING", "ML_POPULARRANKING", "ML_GAMEPLAYERHISTORY", "ML_MARKET_PLAYER_PARAM_SIZE",
    ];

    public static string PathOf(string type) => "/ml/" + type.ToLowerInvariant();

    private const int RankingPage = 100;

    /// <summary>key=value pairs of the request, quoted ("...") or not, separated by commas, ampersands or spaces.</summary>
    public static Dictionary<string, string> ParseRequest(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(Uri.UnescapeDataString(text.Replace('+', ' ')), "([A-Za-z_][A-Za-z0-9_]*)=(?:\"([^\"]*)\"|([^,&\\s]*))"))
            values[m.Groups[1].Value] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        return values;
    }

    public static async Task<string> RenderAsync(MlLeague league, string type, string body, int sessionPid, CancellationToken ct)
    {
        var q = ParseRequest(body);
        var msg = q.GetValueOrDefault("msg") ?? "";
        int Arg(string key, int fallback = 0) => q.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;
        var pid = Arg("pid", sessionPid);

        KvMessage reply = type.ToUpperInvariant() switch
        {
            "ML_MARKET_PLAYER_PARAM_SIZE" => Ok().Set("file_size", 0).Set("uncompressed_file_size", 0),
            "ML_GAMEPLAYERHISTORY" => await HistoryAsync(league, Arg("gameplayer_id"), ct),
            "ML_PRICERANKING" => await MarketRankingAsync(league, MarketOrder.Price, ct),
            "ML_GAINRANKING" => await MarketRankingAsync(league, MarketOrder.Gain, ct),
            "ML_POPULARRANKING" => await MarketRankingAsync(league, MarketOrder.Popular, ct),
            "ML_RANKING" => await MlRankings.RankingAsync(league, q.GetValueOrDefault("rankall_kind"), Arg("start_index"), q.GetValueOrDefault("order"), null, ct),
            "ML_RANKING_BYID" => await MlRankings.RankingAsync(league, q.GetValueOrDefault("rankall_kind"), 0, null, pid, ct),
            "ML_DIVISIONRANKING" => await MlRankings.DivisionRankingAsync(league, Arg("division", -1), Arg("start_index"), null, ct),
            "ML_DIVISIONRANKING_BYID" => await MlRankings.DivisionRankingAsync(league, -1, 0, pid, ct),
            _ => Fail("ERR_NOTFOUND"),
        };

        if (msg.Length > 0 && !reply.Has("msg"))
            reply = new KvMessage().Set("msg", msg).Merge(reply);
        return new KvWriter().Write(reply).TrimEnd('\0');
    }

    public static KvMessage Ok() => new KvMessage().Set("result", "NOERR");

    public static KvMessage Fail(string result) => new KvMessage().Set("result", result);

    private static KvMessage Merge(this KvMessage target, KvMessage source)
    {
        foreach (var (key, value) in source.Fields)
            target.Set(key, value);
        return target;
    }

    /// <summary>owner_num, history_num, price_history[date, market_price, market_price_max, market_price_min], one per day.</summary>
    private static async Task<KvMessage> HistoryAsync(MlLeague league, int id, CancellationToken ct)
    {
        if (id <= 0)
            return Fail("ERR_DATABASE");
        var owners = await league.OwnersAsync(id, ct);
        var points = await league.Db.MlPrices.Where(p => p.GamePlayerId == id).OrderByDescending(p => p.Day).Take(30).ToListAsync(ct);
        points.Reverse();
        long max = 0, min = long.MaxValue;
        var list = new List<KvMessage>();
        foreach (var p in points)
        {
            max = Math.Max(max, p.Price);
            min = Math.Min(min, p.Price);
            list.Add(new KvMessage().Set("date", p.Day).Set("market_price", p.Price).Set("market_price_max", max).Set("market_price_min", min));
        }

        if (list.Count == 0)
        {
            var now = league.Price(id, owners);
            list.Add(new KvMessage().Set("date", league.Today).Set("market_price", now).Set("market_price_max", now).Set("market_price_min", now));
        }

        return Ok().Set("owner_num", owners).SetList("history_num", "price_history", list);
    }

    private enum MarketOrder { Price, Gain, Popular }

    /// <summary>rank_list_num, rank_list[rank, gameplayer_id, market_price, owner_num, market_price_this_week, market_price_last_week], date.</summary>
    private static async Task<KvMessage> MarketRankingAsync(MlLeague league, MarketOrder order, CancellationToken ct)
    {
        var owned = await league.Db.MlTeamPlayers.GroupBy(p => p.GamePlayerId)
            .Select(g => new { Id = g.Key, Owners = g.Count() }).ToListAsync(ct);
        var ownersById = owned.ToDictionary(o => o.Id, o => o.Owners);

        IEnumerable<int> candidates = order switch
        {
            // Every footballer has a price, owned or not.
            MarketOrder.Price => league.Catalog.Players.Select(p => p.Id).Union(ownersById.Keys)
                .OrderByDescending(id => league.Price(id, ownersById.GetValueOrDefault(id))).Take(RankingPage),
            MarketOrder.Popular => ownersById.OrderByDescending(o => o.Value).ThenBy(o => o.Key).Take(RankingPage).Select(o => o.Key),
            _ => ownersById.Keys,
        };

        var rows = new List<(int Id, int Owners, PriceStats Stats)>();
        foreach (var id in candidates)
        {
            var count = ownersById.GetValueOrDefault(id);
            rows.Add((id, count, await league.StatsAsync(id, count, ct)));
        }

        if (order == MarketOrder.Gain)
            rows = rows.OrderByDescending(r => r.Stats.Now - r.Stats.ThisWeek).ThenBy(r => r.Id).Take(RankingPage).ToList();

        var list = rows.Select((r, i) => new KvMessage()
            .Set("rank", i + 1)
            .Set("gameplayer_id", r.Id)
            .Set("market_price", r.Stats.Now)
            .Set("owner_num", r.Owners)
            .Set("market_price_this_week", r.Stats.ThisWeek)
            .Set("market_price_last_week", r.Stats.LastWeek)).ToList();
        return Ok().Set("date", league.Today).SetList("rank_list_num", "rank_list", list);
    }
}
