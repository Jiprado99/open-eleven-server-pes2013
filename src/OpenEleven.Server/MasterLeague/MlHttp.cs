// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Runtime.CompilerServices;
using System.Text;
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
/// <item>ML_MARKET_PLAYER_PARAM_SIZE: CMD_GET_ML_PLAYERPARAMSIZE; ML_MARKET_PLAYER_PARAM: the file itself (<see cref="PlayerParamAsync"/>)</item>
/// </list>
/// ponytail: how the client frames the request body is read from the decompilation, not from a
/// capture; <see cref="ParseRequest"/> takes key=value pairs in either the socket or the form
/// layout, and the command name decides between the commands that share a url.
/// </summary>
public static class MlHttp
{
    public static readonly string[] UrlTypes =
    [
        "ML_RANKING", "ML_RANKING_BYID", "ML_DIVISIONRANKING", "ML_DIVISIONRANKING_BYID", "ML_RIVAL_RANK",
        "ML_PRICERANKING", "ML_GAINRANKING", "ML_POPULARRANKING", "ML_GAMEPLAYERHISTORY", "ML_MARKET_PLAYER_PARAM", "ML_MARKET_PLAYER_PARAM_SIZE",
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
            "ML_MARKET_PLAYER_PARAM_SIZE" => await ParamSizeAsync(league, ct),
            "ML_GAMEPLAYERHISTORY" => await HistoryAsync(league, Arg("gameplayer_id"), ct),
            "ML_PRICERANKING" => await MarketRankingAsync(league, MarketOrder.Price, ct),
            "ML_GAINRANKING" => await MarketRankingAsync(league, MarketOrder.Gain, ct),
            "ML_POPULARRANKING" => await MarketRankingAsync(league, MarketOrder.Popular, ct),
            "ML_RANKING" => await MlRankings.RankingAsync(league, q.GetValueOrDefault("rankall_kind"), Arg("start_index"), q.GetValueOrDefault("order"), null, ct),
            "ML_RANKING_BYID" => await MlRankings.RankingAsync(league, q.GetValueOrDefault("rankall_kind"), 0, null, pid, ct),
            "ML_DIVISIONRANKING" => await MlRankings.DivisionRankingAsync(league, Arg("division", -1), Arg("start_index"), null, ct),
            "ML_DIVISIONRANKING_BYID" => await MlRankings.DivisionRankingAsync(league, -1, 0, pid, ct),
            "ML_RIVAL_RANK" => msg switch
            {
                "CMD_GET_ML_RIVAL_RANK_STATUS" => await MlRivalRank.StatusAsync(league, pid, ct),
                "CMD_GET_ML_RIVAL_RANK_BONUS" => await MlRivalRank.BonusAsync(league, pid, ct),
                "CMD_GET_ML_RIVAL_RANK_LAST" => await MlRivalRank.RankAsync(league, pid, lastWeek: true, ct),
                _ => await MlRivalRank.RankAsync(league, pid, lastWeek: false, ct),
            },
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

    /// <summary>
    /// The market's footballer list (ML_MARKET_PLAYER_PARAM). The client downloads it after
    /// CMD_GET_ML_PLAYERPARAMSIZE and its market search walks it line by line (pes2013.exe
    /// FUN_0065fa20 / FUN_00662ab0): a footballer missing from it never shows up, so with no file
    /// the market is empty. Plain text, one footballer per line, separated by blanks:
    /// <c>gameplayer_id market_price trade_stop_end condition is_trade_stop owner_num contract_price</c>.
    /// A line whose id is 0 or whose last value is -1 is skipped; is_trade_stop≠0 hides him from
    /// the search. file_size = uncompressed_file_size means "not compressed" (FUN_0065fc00).
    /// ponytail: the meaning of the 3rd to 5th values comes from where the client copies them
    /// (trade_stop date, ml_condition 80..400, is_trade_stop), not from a capture; condition is
    /// always NORMAL (240) and nobody is ever trade-stopped.
    /// </summary>
    public static async Task<byte[]> PlayerParamAsync(MlLeague league, CancellationToken ct, bool rebuild = false)
    {
        // CMD_GET_ML_PLAYERPARAMSIZE and the download are two requests: the client sizes its
        // buffer from the first, so the second must send the same bytes even if a club buys
        // someone in between. The size request rebuilds; the download reuses that for a while.
        if (!rebuild && ParamFiles.TryGetValue(league.Catalog, out var cached) && league.Now - cached.BuiltAt < ParamFileLifetime)
            return cached.Bytes;

        var owners = (await league.Db.MlTeamPlayers.GroupBy(p => p.GamePlayerId)
            .Select(g => new { Id = g.Key, Owners = g.Count() }).ToListAsync(ct)).ToDictionary(o => o.Id, o => o.Owners);
        var text = new StringBuilder();
        foreach (var player in league.Catalog.Players.Where(p => p.Id > 0).OrderBy(p => p.Id))
        {
            var count = owners.GetValueOrDefault(player.Id);
            var price = league.Price(player.Id, count);
            text.Append(player.Id).Append(' ').Append(price).Append(" 0 240 0 ").Append(count).Append(' ')
                .Append(league.Rules.Salary(price)).Append('\n');
        }

        var bytes = Encoding.ASCII.GetBytes(text.ToString());
        ParamFiles.AddOrUpdate(league.Catalog, new ParamFile(bytes, league.Now));
        return bytes;
    }

    private const long ParamFileLifetime = 600;
    private sealed record ParamFile(byte[] Bytes, long BuiltAt);
    private static readonly ConditionalWeakTable<MlCatalog, ParamFile> ParamFiles = new();

    private static async Task<KvMessage> ParamSizeAsync(MlLeague league, CancellationToken ct)
    {
        var size = (await PlayerParamAsync(league, ct, rebuild: true)).Length;
        return Ok().Set("file_size", size).Set("uncompressed_file_size", size);
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
