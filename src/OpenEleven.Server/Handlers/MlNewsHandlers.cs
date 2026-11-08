// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.MasterLeague;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Master League Online news and club events. Every club event (TEAM_CREATE, TEAM_NAME, TEAM_RESET,
/// BUY, SELL, COMPE, RIVAL_RANK_CHAMP) is an <see cref="MlEvent"/>; the manager may add a comment to
/// it (CMD_SET_ML_NEWS_COMMENT_*), and the news screen shows the league's events (REGULAR) and the
/// commented ones (COMMENT). Keys: news_list_num, news_list[news_type, news_kind, text, comment, pid,
/// name]; event_num, event_list[event_date, event_type] (key tables of the dump).
/// ponytail: news_kind and text are the event type and its subject (a footballer's or club's name);
/// the client's own news wording for them is not known.
/// </summary>
public sealed class MlNewsHandlers(MlLeague league, ChatTextPolicy text)
{
    private const int MaxNews = 50;

    /// <summary>Request: lang, news_type = COMMENT | REGULAR.</summary>
    [Command("CMD_GET_ML_NEWS_LIST", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    public async ValueTask<KvMessage[]> GetNews(CommandContext ctx)
    {
        var type = ctx.Request.GetString("news_type") ?? "REGULAR";
        var query = league.Db.MlEvents.AsNoTracking();
        if (type == "COMMENT")
            query = query.Where(e => e.Comment != "");
        var events = await query.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Take(MaxNews).ToListAsync(ctx.CancellationToken);
        var pids = events.Select(e => e.Pid).Distinct().ToList();
        var names = await league.Db.Players.Where(p => pids.Contains(p.Pid)).ToDictionaryAsync(p => p.Pid, p => p.Name, ctx.CancellationToken);

        var list = events.Select(e => new KvMessage()
            .Set("news_type", type)
            .Set("news_kind", e.Type)
            .Set("text", Subject(e))
            .Set("comment", e.Comment)
            .Set("pid", e.Pid)
            .Set("name", names.GetValueOrDefault(e.Pid, ""))).ToList();
        return [ctx.Ok().SetList("news_list_num", "news_list", list)];
    }

    /// <summary>The club's events, newest first (request: pid, lang).</summary>
    [Command("CMD_GET_ML_EVENTLIST", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    [Command("CMD_GET_ML_MYTEAM_EVENTLIST", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    public async ValueTask<KvMessage[]> GetEvents(CommandContext ctx)
    {
        var pid = ctx.Request.GetInt32("pid", ctx.Session.Pid);
        var events = await league.Db.MlEvents.AsNoTracking().Where(e => e.Pid == pid)
            .OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Take(MaxNews).ToListAsync(ctx.CancellationToken);
        var list = events.Select(e => new KvMessage()
            .Set("event_date", e.At)
            .Set("event_type", e.Type)
            .Set("gameplayer_id", e.GamePlayerId)
            .Set("compe_id", e.CompeId)
            .Set("text", Subject(e))).ToList();
        return [ctx.Ok().SetList("event_num", "event_list", list)];
    }

    /// <summary>
    /// The league's own event calendar. ponytail: the keys (title, item_list[item_name, item_text])
    /// are known but not their meaning; an empty calendar is served.
    /// </summary>
    [Command("CMD_GET_ML_REGULAREVENTLIST", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    public ValueTask<KvMessage[]> GetRegularEvents(CommandContext ctx)
        => Reply.Of(ctx.Ok().SetList("event_num", "event_list", []));

    /// <summary>
    /// A comment on the latest event of the command's kind (request: comment, and gameplayer_id or
    /// compe_id where they apply). ERR_NGWORD when the text trips the blocked terms.
    /// </summary>
    [Command("CMD_SET_ML_NEWS_COMMENT", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    [Command("CMD_SET_ML_NEWS_COMMENT_TEAM_CREATE", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    [Command("CMD_SET_ML_NEWS_COMMENT_TEAM_RESET", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    [Command("CMD_SET_ML_NEWS_COMMENT_BUY", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    [Command("CMD_SET_ML_NEWS_COMMENT_SELL", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    [Command("CMD_SET_ML_NEWS_COMMENT_COMPE_CHAMP", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    [Command("CMD_SET_ML_NEWS_COMMENT_RIVAL_RANK_CHAMP", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    public async ValueTask<KvMessage[]> SetComment(CommandContext ctx)
    {
        var comment = ctx.Request.GetString("comment") ?? "";
        if (comment.Length > 0 && (!text.TrySanitize(comment, out _, out var censored) || censored))
            return [ctx.Fail("ERR_NGWORD")];

        var kind = ctx.Msg switch
        {
            "CMD_SET_ML_NEWS_COMMENT_TEAM_CREATE" => "TEAM_CREATE",
            "CMD_SET_ML_NEWS_COMMENT_TEAM_RESET" => "TEAM_RESET",
            "CMD_SET_ML_NEWS_COMMENT_BUY" => "BUY",
            "CMD_SET_ML_NEWS_COMMENT_SELL" => "SELL",
            "CMD_SET_ML_NEWS_COMMENT_COMPE_CHAMP" => "COMPE",
            "CMD_SET_ML_NEWS_COMMENT_RIVAL_RANK_CHAMP" => "RIVAL_RANK_CHAMP",
            _ => null,
        };
        var gamePlayer = ctx.Request.GetInt32("gameplayer_id", 0);
        var compe = ctx.Request.GetInt32("compe_id", 0);

        var query = league.Db.MlEvents.Where(e => e.Pid == ctx.Session.Pid);
        if (kind is not null)
            query = query.Where(e => e.Type == kind);
        if (gamePlayer > 0)
            query = query.Where(e => e.GamePlayerId == gamePlayer);
        if (compe > 0)
            query = query.Where(e => e.CompeId == compe);
        var target = await query.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).FirstOrDefaultAsync(ctx.CancellationToken);

        if (target is null)
        {
            if (kind == "COMPE")
                return [ctx.Fail("ERR_COMPE_NOTFOUND")];
            league.Event(ctx.Session.Pid, kind ?? "COMMENT", gamePlayer, compe);
            await league.Db.SaveChangesAsync(ctx.CancellationToken);
            target = await league.Db.MlEvents.Where(e => e.Pid == ctx.Session.Pid).OrderByDescending(e => e.Id).FirstAsync(ctx.CancellationToken);
        }

        target.Comment = comment.Length > 256 ? comment[..256] : comment;
        await league.Db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>CMD_GET_ML_COMPE_PRIZE: the prize of the club's last finished ML competition (paid once).</summary>
    [Command("CMD_GET_ML_COMPE_PRIZE", Roles = MlTeamHandlers.Roles, Profiles = MlTeamHandlers.Profiles)]
    public async ValueTask<KvMessage[]> GetCompePrize(CommandContext ctx)
    {
        var prize = await MlCompetitions.PrizeAsync(league, ctx.Session.Pid, ctx.CancellationToken);
        var reply = ctx.Ok();
        foreach (var (key, value) in prize.Fields)
            reply.Set(key, value);
        return [reply];
    }

    private string Subject(MlEvent e)
        => e.Text.Length > 0 ? e.Text : e.GamePlayerId > 0 ? league.Catalog.Player(e.GamePlayerId).Name : "";
}
