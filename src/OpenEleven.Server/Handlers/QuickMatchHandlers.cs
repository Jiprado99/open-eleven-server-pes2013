// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Logging;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Quick match. In pes2011.exe the room FSM (FUN_005ed6a0) hands "start quick match" to the client's
/// matching engine, which talks MSG_QUICK_* to us and turns the outcome into its own
/// CMD_START_QUICKMATCH result (FUN_00fffdf0), so CMD_START_QUICKMATCH never reaches the wire:
/// <list type="number">
/// <item>MSG_QUICK_REQ_MATCHING_START → ack + MSG_QUICK_MATCHING_START_RES; the client waits 180 s.</item>
/// <item>Opponent found → MSG_QUICK_MATCH_MEMBER_NOTICE; each client pings the others and answers
/// MSG_QUICK_MATCH_MEMBER_REPLY.</item>
/// <item>All replied → MSG_QUICK_MATCHING_RES; 20 s P2P pre-connect, then MSG_QUICK_ACCEPT_MATCH_OFFER.</item>
/// <item>All accepted → the server puts them in a room and sends MSG_QUICK_ACCEPT_MATCH_OFFER_RES; the
/// room tail (CMD_SET_GAMEMEMBERENV, CMD_SET_GAMEMEMBER, in-match commands) is the lobby one.</item>
/// </list>
/// Quick match starts from the menu, before any lobby or block, hence RequiredState PlayerSelected.
/// <para>
/// pes2013.exe speaks the same dialogue with an _NTL suffix on every message (its NTL library,
/// FUN_010e4db0..FUN_010e8270): the request carries type, player_num, game_time, is_rating,
/// has_guestplayer, pass_level, is_pass_level, is_coach_mode and is_team_power; type is one of
/// OC_RANK, OC_FREE, COMPE, ML_RANK, ML_COMPE (Master League Online plays ML_RANK); the member
/// notice adds derby_income and the ML club of each member (FUN_010e75d0).
/// </para>
/// </summary>
public sealed class QuickMatchHandlers(
    IPlayerRepository players,
    MasterLeague.MlLeague league,
    OpenEleven.Data.GameDbContext db,
    Microsoft.Extensions.Options.IOptionsMonitor<ServerOptions> options,
    TimeProvider time,
    ILogger<QuickMatchHandlers> log)
{
    private const ServiceRole Roles = ServiceRole.Menu;

    /// <summary>
    /// Saved quick match options (FUN_00600200). All four keys are required, or the option screen
    /// ends in its error state; the three flags are enum 0x4c (NO|YES), opp_lang enum 0x1c.
    /// </summary>
    [Command("CMD_GET_QUICKMATCHOPTION", Roles = Roles)]
    public async ValueTask<KvMessage[]> GetOption(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        var saved = (player?.QuickMatchOptions ?? "").Split(',');
        var keys = KeysFor(ctx.Profile);
        string At(int i, string fallback) => saved.Length == keys.Length && saved[i] != "" ? saved[i] : fallback;

        // PES 2013's keys are all NO|YES flags (no opp_lang), so nothing defaults to ANY there.
        var reply = ctx.Ok();
        for (var i = 0; i < keys.Length; i++)
            reply.Set(keys[i], At(i, i == 0 && ctx.Profile != GameProfile.Pes2013Pc ? "ANY" : "NO"));
        return [reply];
    }

    /// <summary>The options screen saves the same four keys (request built by FUN_005fff90).</summary>
    [Command("CMD_SET_QUICKMATCHOPTION", Roles = Roles)]
    public async ValueTask<KvMessage[]> SetOption(CommandContext ctx)
    {
        if (await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken) is { } player)
        {
            player.QuickMatchOptions = string.Join(',', KeysFor(ctx.Profile).Select(k => ctx.Request.GetString(k) ?? ""));
            await players.SaveAsync(ctx.CancellationToken);
        }

        return [ctx.Ok()];
    }

    private static readonly string[] OptionKeys = ["opp_lang", "oc_is_rating_rank", "oc_is_rating_free", "ml_is_division"];

    /// <summary>
    /// pes2013.exe's CMD_GET/SET_QUICKMATCHOPTION name three keys instead (is_rating, is_pass_level,
    /// is_team_power, read from the exe's strings; their values are assumed NO|YES like the others).
    /// ponytail: unconfirmed value type; the quick-match search itself still uses the 2011 options.
    /// </summary>
    private static readonly string[] Options2013 = ["is_rating", "is_pass_level", "is_team_power"];

    private static string[] KeysFor(GameProfile profile) => profile == GameProfile.Pes2013Pc ? Options2013 : OptionKeys;

    [Command("CMD_SEND_QUICK_GAMEBEGINNING_HOPE", Roles = Roles)]
    [Command("CMD_CANCEL_QUICK_GAMEBEGINNING_HOPE", Roles = Roles)]
    public ValueTask<KvMessage[]> Ack(CommandContext ctx) => Reply.Of(ctx.Ok());

    /// <summary>Players searching right now, per mode (FUN_005dad00).</summary>
    [Command("CMD_GET_QUICKMATCHNUMBERS", Roles = Roles)]
    public ValueTask<KvMessage[]> GetNumbers(CommandContext ctx)
    {
        var q = ctx.Hub.QuickMatch;
        return Reply.Of(ctx.Ok()
            .Set("rank_1vs1_num", q.Searching("OC_Rank", 2))
            .Set("rank_2vs2_num", q.Searching("OC_Rank", 4))
            .Set("free_2vs2_num", q.Searching("OC_Free", 4)));
    }

    /// <summary>
    /// Start searching. Request: <c>type</c> (enum 0x40 OC_Rank|OC_Free|...), <c>player_num</c>,
    /// <c>game_time</c> (GT05|GT10), <c>opp_lang</c>, <c>frrate</c>, ... Players are paired on type,
    /// player count, game time and frame rate.
    /// </summary>
    [Command("MSG_QUICK_REQ_MATCHING_START", Roles = Roles,
        RequiredState = SessionState.PlayerSelected)]
    [Command("MSG_QUICK_REQ_MATCHING_START_NTL", Roles = Roles, Profiles = GameProfile.Pes2013Pc,
        RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> RequestMatchingStart(CommandContext ctx)
    {
        var ntl = ctx.Msg.EndsWith("_NTL", StringComparison.Ordinal);
        // The NTL request is flat; accept the setting as a record too (qmatchsetting).
        var r = ctx.Request.GetValue("qmatchsetting") as KvMessage ?? ctx.Request;
        var type = r.GetString("type") ?? (ntl ? "OC_RANK" : "OC_Rank");
        var playerNum = Math.Clamp(r.GetInt32("player_num", 2), 2, 4);
        var gameTime = r.GetString("game_time") ?? "GT10";
        var frrate = r.GetString("frrate") ?? "FRAME_60";

        // A Master League match needs a club on both sides.
        if (type is "ML_RANK" or "ML_COMPE" && await league.TeamAsync(ctx.Session.Pid, ctx.CancellationToken) is null)
            return [ctx.Fail("ERR_QMATCH_STARTMATCHING")];

        // The saved options: opp_lang, then the rating filters for Rank and Free.
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);

        // "To Next Match" of a competition pairs two entrants of that competition while its qualifying round is on.
        var compeId = 0;
        if (type is "COMPE" or "ML_COMPE")
        {
            if (await QualifierEntryAsync(player, ctx.CancellationToken) is not { } cup)
                return [ctx.Fail("ERR_QMATCH_STARTMATCHING")];
            compeId = cup.Id;
        }

        var saved = (player?.QuickMatchOptions ?? "").Split(',');
        string Saved(int i) => saved.Length == KeysFor(ctx.Profile).Length && i < saved.Length ? saved[i] : "";
        var seeker = new QuickSeeker(ctx.Session, ctx.Rqid, type, playerNum, gameTime, frrate,
            $"{type}|{playerNum}|{gameTime}|{frrate}{(ntl ? "|NTL" : "")}{(compeId != 0 ? $"|C{compeId}" : "")}")
        {
            Lang = player?.Lang ?? "",
            // PES 2013 has no opponent language option; its is_rating flag asks for a similar rating.
            OppLang = ntl ? "ANY" : r.GetString("opp_lang") ?? (Saved(0) is { Length: > 0 } lang ? lang : "ANY"),
            Rating = player?.Rating ?? 0,
            RatingFilter = ntl ? r.GetString("is_rating") == "YES" : Saved(type == "OC_Free" ? 2 : 1) == "YES",
            Ntl = ntl,
        };

        ctx.Session.Push(ctx.Ok());
        ctx.Session.Push(MatchingStartResponse(seeker.Name("MSG_QUICK_MATCHING_START_RES"), ctx.Rqid));
        log.LogInformation("pid {Pid} searching for a quick match ({Key})", ctx.Session.Pid, seeker.Key);

        if (ctx.Hub.QuickMatch.Search(seeker) is { } group)
            await SendMemberNoticeAsync(ctx, group);
        return [];
    }

    /// <summary>
    /// The competition a qualifying match is played for: the one the player entered, while its
    /// qualifying round is on, with a team picked (an ML club for an ML competition) and matches
    /// left to play. Null otherwise.
    /// </summary>
    private async Task<CompetitionOptions?> QualifierEntryAsync(OpenEleven.Data.Entities.Player? player, CancellationToken ct)
    {
        if (player is null || Competitions.CompetitionCatalog.Find(options.CurrentValue, player.CompetitionId) is not { } cup
            || !Competitions.CompetitionCatalog.QualifierOpen(cup, time.GetUtcNow().UtcDateTime))
            return null;
        if (!cup.MasterLeague && player.CompetitionTeamId == 0)
            return null;
        if (cup.MaxQualifierMatches > 0
            && await Competitions.CompetitionTable.QualifierMatchesAsync(db, cup.Id, player.Pid, ct) >= cup.MaxQualifierMatches)
            return null;
        return cup;
    }

    /// <summary>A client finished pinging the others; once all have, the match is offered.</summary>
    [Command("MSG_QUICK_MATCH_MEMBER_REPLY", Roles = Roles,
        RequiredState = SessionState.PlayerSelected)]
    [Command("MSG_QUICK_MATCH_MEMBER_REPLY_NTL", Roles = Roles, Profiles = GameProfile.Pes2013Pc,
        RequiredState = SessionState.PlayerSelected)]
    public ValueTask<KvMessage[]> MemberReply(CommandContext ctx)
    {
        ctx.Session.Push(ctx.Ok());
        if (ctx.Hub.QuickMatch.GroupOf(ctx.Session) is { } group && group.Replied(ctx.Session.Pid))
            foreach (var m in group.Members)
                m.Session.Push(KvMessage.Ok(m.Name("MSG_QUICK_MATCHING_RES"), m.Rqid));
        return Reply.None();
    }

    /// <summary>
    /// After the pre-connect: <c>result</c> NOERR accepts, ERR_PING_REFUSAL turns the match down.
    /// Once everyone accepted, the room is created and MSG_QUICK_ACCEPT_MATCH_OFFER_RES lists the
    /// members (<c>member[i].pid|matching_order|frrate</c>; pids must be the notice's).
    /// </summary>
    [Command("MSG_QUICK_ACCEPT_MATCH_OFFER", Roles = Roles,
        RequiredState = SessionState.PlayerSelected)]
    [Command("MSG_QUICK_ACCEPT_MATCH_OFFER_NTL", Roles = Roles, Profiles = GameProfile.Pes2013Pc,
        RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> AcceptMatchOffer(CommandContext ctx)
    {
        ctx.Session.Push(ctx.Ok());
        var quick = ctx.Hub.QuickMatch;
        if (quick.GroupOf(ctx.Session) is not { } group)
            return [];

        if (ctx.Request.GetString("result") != "NOERR")
        {
            log.LogInformation("pid {Pid} refused the quick match ({Result})", ctx.Session.Pid, ctx.Request.GetString("result"));
            quick.Leave(ctx.Session);
            group.TellCancelled(ctx.Session);
            return [];
        }

        if (!group.Accepted(ctx.Session.Pid))
            return [];

        quick.Complete(group);
        var owner = group.Members[0];
        var room = ctx.Hub.CreateQuickMatchRoom(
            group.Members.Select(m => m.Session).ToArray(),
            RoomMatchType(owner.Type),
            new KvMessage().Set("type", owner.Type).Set("game_time", owner.GameTime).Set("frrate", owner.Frrate));

        // "To Next Match" pairs two entrants of one competition; its results belong to that competition.
        if (owner.Type is "COMPE" or "ML_COMPE")
        {
            var entered = new List<int>();
            foreach (var m in group.Members)
                entered.Add((await players.GetAsync(m.Session.Pid, ctx.CancellationToken))?.CompetitionId ?? 0);
            if (entered.Distinct().Count() == 1)
                room.CompeId = entered[0];
        }

        var list = group.Members.Select((m, i) => new KvMessage()
            .Set("pid", m.Session.Pid)
            .Set("matching_order", i)
            .Set("frrate", m.Frrate)).ToArray();
        foreach (var m in group.Members)
            m.Session.Push(KvMessage.Ok(m.Name("MSG_QUICK_ACCEPT_MATCH_OFFER_RES"), m.Rqid).Set("member", list));
        ctx.Hub.PublishQuickMatchRoom(room);
        return [];
    }

    /// <summary>
    /// The room's match type: ranked (rating and points move), free, or a Master League match
    /// (settled by MasterLeague.MlMatchSettlement). PES 2011 sends OC_Rank, PES 2013 OC_RANK.
    /// </summary>
    public static string RoomMatchType(string quickType) => quickType switch
    {
        "OC_Rank" or "OC_RANK" => "OC_RANKING",
        "ML_RANK" or "ML_COMPE" => quickType,
        _ => "OC_FREE",
    };

    /// <summary>
    /// The client rejects this event as ERR_CLIENT_SVRFORMAT unless all six tuning fields
    /// are present, so they are always written together. pes2013.exe (FUN_010e4340) reads
    /// result, tryCount, ping and pingAdd of the _NTL variant.
    /// </summary>
    private static KvMessage MatchingStartResponse(string msg, int rqid)
        => KvMessage.Ok(msg, rqid)
            .Set("tryCount", 3)
            .Set("ping", 100)
            .Set("pingAdd", 50)
            .Set("bandTryCount", 3)
            .Set("band", 128)
            .Set("bandAdd", 64);

    /// <summary>The player stopped searching (FUN_005e9400); NOERR is what confirms it.</summary>
    [Command("CMD_CANCEL_STARTQUICKMATCH", Roles = Roles)]
    [Command("CMD_CANCEL_STARTQUICKMATCH_NTL", Roles = Roles, Profiles = GameProfile.Pes2013Pc)]
    [Command("CMD_CANCEL_DECIDE_QUICKMATCH", Roles = Roles)]
    public ValueTask<KvMessage[]> Cancel(CommandContext ctx)
    {
        if (ctx.Hub.QuickMatch.Leave(ctx.Session) is { } group)
            group.TellCancelled(ctx.Session);
        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// MSG_QUICK_MATCH_MEMBER_NOTICE, one per recipient because the endpoints are per pair
    /// (Hub.EndpointsFor). pes2012.exe FUN_01105B90 rejects the notice as ERR_CLIENT_SVRFORMAT when
    /// any member key is missing; pes2011.exe FUN_00fffdf0 the same for any but matching_order, and
    /// it reads none of the 2012-only keys. YES/NO fields are compared to "YES".
    /// </summary>
    private async Task SendMemberNoticeAsync(CommandContext ctx, QuickGroup group)
    {
        var profiles = new Dictionary<int, Data.Entities.Player?>();
        foreach (var m in group.Members)
            profiles[m.Session.Pid] = await players.GetAsync(m.Session.Pid, ctx.CancellationToken);

        log.LogInformation("Quick match formed: pids {Pids}", string.Join(", ", group.Members.Select(m => m.Session.Pid)));
        if (group.Members[0].Ntl)
        {
            await SendNtlMemberNoticeAsync(ctx, group, profiles);
            return;
        }

        foreach (var viewer in group.Members)
        {
            var entries = group.Members.Select((m, i) =>
            {
                var s = m.Session;
                var p = profiles[s.Pid];
                var at = ctx.Hub.EndpointsFor(viewer.Session, s);
                return new KvMessage()
                    .Set("matching_order", i)
                    .Set("is_owner", i == 0 ? "YES" : "NO")
                    .Set("pid", s.Pid)
                    .Set("pname", s.PlayerName)
                    .Set("has_guestplayer", "NO")
                    .Set("is_begin", "NO")
                    .Set("ex_ip", at.ExternalIp)
                    .Set("ex_port", at.ExternalPort)
                    .Set("in_ip", at.InternalIp)
                    .Set("in_port", at.InternalPort)
                    .Set("manner", p?.Manner ?? 3)
                    .Set("nickname_id", p?.NicknameId ?? 0)
                    .Set("from_country", p?.Country ?? 0)
                    .Set("from_area", p?.Area ?? 0)
                    .Set("player_name", s.PlayerName)
                    .Set("fd_pic_url", "")
                    .Set("fd_selfreport_level", p?.SelfReportLevel ?? "")
                    .Set("fd_beginnermark", p?.BeginnerMark == true ? "YES" : "NO")
                    .Set("fd_position_want", p?.PositionWant ?? "")
                    .Set("fd_desired_position", p?.DesiredPositionMask ?? 0)
                    .Set("division", RankSystem.Label(p?.Division ?? default, ctx.Profile))
                    .Set("rating", p?.Rating ?? 0)
                    .Set("ml_division", "ML_DIVISION_5")
                    .Set("ml_point", 0)
                    // FUN_01105B90 requires every one of these keys; a single missing key is
                    // reported as ERR_CLIENT_SVRFORMAT and the client cancels the match.
                    .Set("ml_team_name", "")
                    .Set("ml_base_team_id", 0)
                    .Set("ml_team_type", "")
                    .Set("is_chat", "NO")
                    .Set("is_nice", "NO")
                    .Set("is_restricted", "NO");
            }).ToArray();

            viewer.Session.Push(KvMessage.Ok("MSG_QUICK_MATCH_MEMBER_NOTICE", viewer.Rqid)
                .Set("is_old_member", "NO")
                .SetList("member_num", "member", entries));
        }
    }

    /// <summary>
    /// MSG_QUICK_MATCH_MEMBER_NOTICE_NTL (pes2013.exe FUN_010e75d0): result, member_num, derby_income,
    /// is_old_member and member[pid, matching_order, is_owner, pname, nickname_id, manner, ntl_nat_type,
    /// host_addr, reflexive_addr, relayed_addr, has_guestplayer, is_begin, from_country, from_area,
    /// is_restricted, rating, ml_rating, ml_team_name, ml_base_team_id, ml_team_type, team_power].
    /// derby_income is paid when the Master League clubs share their base team.
    /// ponytail: the NTL addresses are the ones the client last sent with CMD_SEND_IPANDPORT_NTL;
    /// whether it sends them before a quick match from the menu is not confirmed.
    /// </summary>
    private async Task SendNtlMemberNoticeAsync(CommandContext ctx, QuickGroup group, Dictionary<int, Data.Entities.Player?> profiles)
    {
        var clubs = new Dictionary<int, (Data.Entities.MlTeam? Team, int Power)>();
        foreach (var m in group.Members)
        {
            var team = await league.TeamAsync(m.Session.Pid, ctx.CancellationToken);
            clubs[m.Session.Pid] = (team, team is null ? 0 : league.TeamPower(await league.SquadAsync(team.Pid, ctx.CancellationToken)));
        }

        var bases = clubs.Values.Where(c => c.Team is not null).Select(c => c.Team!.BaseTeamId).ToList();
        var derby = group.Members[0].Type is "ML_RANK" or "ML_COMPE" && bases.Count > 1 && bases.Distinct().Count() == 1
            ? league.Rules.Options.DerbyIncome : 0;

        foreach (var viewer in group.Members)
        {
            var entries = group.Members.Select((m, i) =>
            {
                var s = m.Session;
                var p = profiles[s.Pid];
                var (team, power) = clubs[s.Pid];
                return new KvMessage()
                    .Set("pid", s.Pid)
                    .Set("matching_order", i)
                    .Set("is_owner", i == 0 ? "YES" : "NO")
                    .Set("pname", s.PlayerName)
                    .Set("nickname_id", p?.NicknameId ?? 0)
                    .Set("manner", p?.Manner ?? 3)
                    .Set("ntl_nat_type", s.NtlNatType)
                    .Set("host_addr", NtlAddress.HostFor(viewer.Session, s) ?? NtlAddress.Zero())
                    .Set("reflexive_addr", s.NtlReflexiveAddr ?? NtlAddress.Zero())
                    .Set("relayed_addr", s.NtlRelayedAddr ?? NtlAddress.Zero())
                    .Set("has_guestplayer", "NO")
                    .Set("is_begin", "NO")
                    .Set("from_country", p?.Country ?? 0)
                    .Set("from_area", p?.Area ?? 0)
                    .Set("is_restricted", "NO")
                    .Set("rating", p?.Rating ?? 0)
                    .Set("ml_rating", team?.Rating ?? 0)
                    .Set("ml_team_name", team?.Name ?? "")
                    .Set("ml_base_team_id", team?.BaseTeamId ?? 0)
                    .Set("ml_team_type", team?.TeamType ?? "")
                    .Set("team_power", power);
            }).ToList();

            viewer.Session.Push(KvMessage.Ok("MSG_QUICK_MATCH_MEMBER_NOTICE_NTL", viewer.Rqid)
                .Set("derby_income", derby)
                .Set("is_old_member", "NO")
                .SetList("member_num", "member", entries));
        }
    }
}
