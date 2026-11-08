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
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Master League Online (PES 2013): entering the mode and the manager's own club. Everything goes
/// to the Menu socket in state PlayerSelected. Live order when a player opens the mode:
/// JOIN_ML_MODE, GET_ML_MYTEAM (ERR_NOTFOUND = no club yet, the client shows the creation screen),
/// CREATE_ML_MYTEAM, GET_ML_MYTEAM, GET_ML_MYTEAM_GAMEPLAYERLIST, GET_ML_MYTEAM_PLAYER_RATING,
/// GET_ML_NEWS_LIST (COMMENT, REGULAR), LEAVE_ML_MODE. See docs/reverse-engineering/pes2013-mlo-plan.md.
/// </summary>
public sealed class MlTeamHandlers(MlLeague league, ChatTextPolicy text)
{
    internal const ServiceRole Roles = ServiceRole.Menu;
    internal const GameProfile Profiles = GameProfile.Pes2013Pc;

    /// <summary>FUN_005c9a90 / FUN_005c9b60 only check for an error.</summary>
    [Command("CMD_JOIN_ML_MODE", Roles = Roles, Profiles = Profiles, RequiredState = SessionState.PlayerSelected)]
    [Command("CMD_LEAVE_ML_MODE", Roles = Roles, Profiles = Profiles, RequiredState = SessionState.PlayerSelected)]
    public ValueTask<KvMessage[]> JoinLeave(CommandContext ctx) => Reply.Of(ctx.Ok());

    /// <summary>The club, as the <c>myteam</c> record; ERR_NOTFOUND when there is none.</summary>
    [Command("CMD_GET_ML_MYTEAM", Roles = Roles, Profiles = Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetMyTeam(CommandContext ctx)
    {
        var pid = ctx.Request.GetInt32("pid", ctx.Session.Pid);
        var team = await league.TeamAsync(pid, ctx.CancellationToken);
        if (team is null)
            return [ctx.Fail("ERR_NOTFOUND")];
        var record = await league.TeamRecordAsync(team, ctx.CancellationToken);
        await league.Db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok().Set("myteam", record)];
    }

    /// <summary>
    /// A new club (request: team_name, team_name_short, base_team_id, check_team_name). The squad is
    /// the game's MASTERLEAGUE DEFAULT; the base team only lends name, badge and kit.
    /// check_team_name=YES only asks whether the name is acceptable.
    /// </summary>
    [Command("CMD_CREATE_ML_MYTEAM", Roles = Roles, Profiles = Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Create(CommandContext ctx)
    {
        if (!TryNames(ctx, out var name, out var shortName, out var error))
            return [ctx.Fail(error)];
        var baseTeam = ctx.Request.GetInt32("base_team_id", 0);
        if (!league.Catalog.HasTeam(baseTeam))
            return [ctx.Fail("ERR_NOTFOUND")];
        if (ctx.Request.GetString("check_team_name") == "YES")
            return [ctx.Ok()];
        if (await league.TeamAsync(ctx.Session.Pid, ctx.CancellationToken) is not null)
            return [ctx.Fail("ERR_ALREADYEXISTS")];

        await league.CreateAsync(ctx.Session.Pid, name, shortName, baseTeam, ctx.CancellationToken);
        league.Event(ctx.Session.Pid, "TEAM_CREATE", text: name);
        await league.Db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>Starts over with a new club of the same name and base, the starting squad and funds.</summary>
    [Command("CMD_RESET_ML_MYTEAM", Roles = Roles, Profiles = Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Reset(CommandContext ctx)
    {
        var team = await league.TeamAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (team is null)
            return [ctx.Fail("ERR_FORBIDDEN")];
        var (name, shortName, baseTeam, type) = (team.Name, team.ShortName, team.BaseTeamId, team.TeamType);
        var created = await league.CreateAsync(ctx.Session.Pid, name, shortName, baseTeam, ctx.CancellationToken);
        created.TeamType = type;
        league.Event(ctx.Session.Pid, "TEAM_RESET", text: name);
        await league.Db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    [Command("CMD_DELETE_ML_MYTEAM", Roles = Roles, Profiles = Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Delete(CommandContext ctx)
    {
        await league.RemoveAsync(ctx.Session.Pid, ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>Request: team_name, team_name_short, check_team_name.</summary>
    [Command("CMD_SET_ML_MYTEAM_NAME", Roles = Roles, Profiles = Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> SetName(CommandContext ctx)
    {
        if (!TryNames(ctx, out var name, out var shortName, out var error))
            return [ctx.Fail(error)];
        var team = await league.TeamAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (team is null)
            return [ctx.Fail("ERR_NOTFOUND")];
        if (ctx.Request.GetString("check_team_name") == "YES")
            return [ctx.Ok()];

        team.Name = name;
        team.ShortName = shortName;
        team.UpdatedAt = league.Now;
        league.Event(team.Pid, "TEAM_NAME", text: name);
        await league.Db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>Request: base_team_id.</summary>
    [Command("CMD_SET_ML_MYTEAM_BASE", Roles = Roles, Profiles = Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> SetBase(CommandContext ctx)
    {
        var team = await league.TeamAsync(ctx.Session.Pid, ctx.CancellationToken);
        var baseTeam = ctx.Request.GetInt32("base_team_id", -1);
        if (team is null || !league.Catalog.HasTeam(baseTeam))
            return [ctx.Fail("ERR_FORBIDDEN")];
        team.BaseTeamId = baseTeam;
        team.UpdatedAt = league.Now;
        await league.Db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>
    /// The client's team type value. ponytail: neither the request key nor the values are known;
    /// any of ml_team_type / team_type / type is taken and echoed in the quick match member.
    /// </summary>
    [Command("CMD_SET_ML_MYTEAM_TYPE", Roles = Roles, Profiles = Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> SetType(CommandContext ctx)
    {
        var team = await league.TeamAsync(ctx.Session.Pid, ctx.CancellationToken);
        var type = ctx.Request.GetString("ml_team_type") ?? ctx.Request.GetString("team_type") ?? ctx.Request.GetString("type");
        if (team is null || string.IsNullOrEmpty(type) || type.Length > 32)
            return [ctx.Fail("ERR_INVALIDARG")];
        team.TeamType = type;
        await league.Db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>Request: gameplayer_id, uniform_number. Swaps with whoever had that number.</summary>
    [Command("CMD_SET_ML_MYTEAM_UNIFORMNUMBER", Roles = Roles, Profiles = Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> SetUniformNumber(CommandContext ctx)
    {
        var id = ctx.Request.GetInt32("gameplayer_id", 0);
        var number = ctx.Request.GetInt32("uniform_number", 0);
        var squad = await league.SquadAsync(ctx.Session.Pid, ctx.CancellationToken);
        var player = squad.FirstOrDefault(p => p.GamePlayerId == id);
        if (player is null || number is < 1 or > 99)
            return [ctx.Fail("ERR_FORBIDDEN")];

        if (squad.FirstOrDefault(p => p.UniformNumber == number && p != player) is { } other)
            other.UniformNumber = player.UniformNumber;
        player.UniformNumber = number;
        await league.Db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>
    /// The squad with prices (parser near all_functions.c:391600): player_list_num and player_list
    /// with injury as a subrecord and contract_price_list as one price per CONTRACT_TYPE.
    /// </summary>
    [Command("CMD_GET_ML_MYTEAM_GAMEPLAYERLIST", Roles = Roles, Profiles = Profiles, RequiredState = SessionState.PlayerSelected)]
    [Command("CMD_GET_ML_MYTEAM_GAMEPLAYERLIST_OLD", Roles = Roles, Profiles = Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetSquad(CommandContext ctx)
    {
        var pid = ctx.Request.GetInt32("pid", ctx.Session.Pid);
        if (await league.TeamAsync(pid, ctx.CancellationToken) is null)
            return [ctx.Fail("ERR_DATANOTFOUND")];
        var squad = await league.SquadAsync(pid, ctx.CancellationToken);
        var owners = await league.OwnersAsync(squad.Select(p => p.GamePlayerId), ctx.CancellationToken);

        var list = new List<KvMessage>();
        foreach (var p in squad)
        {
            var count = owners[p.GamePlayerId];
            var stats = await league.StatsAsync(p.GamePlayerId, count, ctx.CancellationToken);
            list.Add(new KvMessage()
                .Set("gameplayer_id", p.GamePlayerId)
                .Set("uniform_number", p.UniformNumber)
                .Set("tiredness", p.Tiredness)
                .Set("ml_condition", MlRules.Condition(p.Tiredness))
                .Set("injury", MlLeague.Injury(p))
                .Set("contract_term", p.ContractTerm)
                .Set("contract_price_list", new KvArray(league.Rules.ContractPrices(stats.Now).Select(x => (object?)x).ToArray()))
                .Set("now_yellow_card_count", p.YellowCards)
                .Set("now_red_card_count", p.RedCards)
                .Set("owner_num", count)
                .Set("games", p.Games)
                .Set("market_price", stats.Now)
                .Set("market_price_old", stats.ThisWeek)
                .Set("buy_price", p.BuyPrice)
                .Set("market_price_max", stats.Max)
                .Set("market_price_min", stats.Min)
                .Set("overall", league.Catalog.Player(p.GamePlayerId).Overall)
                .Set("goal", p.Goals)
                .Set("assist", p.Assists)
                .Set("is_trade_stop", "NO"));
        }

        return [ctx.Ok().SetList("player_list_num", "player_list", list)];
    }

    /// <summary>
    /// The average match rating of each footballer of the squad: active_user_num (clubs that rated),
    /// rating_num and rating_list[gameplayer_id, rating].
    /// </summary>
    [Command("CMD_GET_ML_MYTEAM_PLAYER_RATING", Roles = Roles, Profiles = Profiles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetRatings(CommandContext ctx)
    {
        var pid = ctx.Request.GetInt32("pid", ctx.Session.Pid);
        var squad = await league.SquadAsync(pid, ctx.CancellationToken);
        var ids = squad.Select(p => p.GamePlayerId).ToList();
        var everywhere = await league.Db.MlTeamPlayers.Where(p => ids.Contains(p.GamePlayerId) && p.RatingCount > 0)
            .GroupBy(p => p.GamePlayerId)
            .Select(g => new { g.Key, Sum = g.Sum(x => x.RatingSum), Count = g.Sum(x => x.RatingCount) })
            .ToListAsync(ctx.CancellationToken);
        var active = await league.Db.MlTeamPlayers.Where(p => p.RatingCount > 0).Select(p => p.Pid).Distinct().CountAsync(ctx.CancellationToken);

        var list = everywhere.Select(r => new KvMessage()
            .Set("gameplayer_id", r.Key)
            .Set("rating", r.Count == 0 ? 0 : (int)Math.Round((double)r.Sum / r.Count))).ToList();
        return [ctx.Ok().Set("active_user_num", active).SetList("rating_num", "rating_list", list)];
    }

    private bool TryNames(CommandContext ctx, out string name, out string shortName, out string error)
    {
        name = (ctx.Request.GetString("team_name") ?? "").Trim();
        shortName = (ctx.Request.GetString("team_name_short") ?? "").Trim();
        error = "ERR_NGWORD";
        if (name.Length is 0 or > 32 || shortName.Length is 0 or > 3)
            return false;
        if (!text.TrySanitize(name, out _, out var censoredName) || censoredName
            || !text.TrySanitize(shortName, out _, out var censoredShort) || censoredShort)
            return false;
        return true;
    }
}
