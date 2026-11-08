// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Data;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// The titles ("nicknames") a PES 2013 player shows next to the name. The client keeps the picked
/// <c>nickname_id</c> in the profile (CMD_SET_NICKNAME, or the profile record of CMD_SET_PLAYERPROFILE),
/// asks which ids are open (CMD_GET_UNLOCKEDNICKNAMELIST) and fetches the texts with
/// CMD_GET_NICKNAME_COMPE over the NICKNAME url. Id 0 is the client's default nickname.
/// </summary>
public sealed class NicknameHandlers(IPlayerRepository players)
{
    /// <summary>
    /// The nickname catalogue served to the client. Every id is unlocked: the server has no
    /// achievement system to earn them with.
    /// ponytail: the texts are placeholders, the real catalogue is not in pes2013.exe's strings (the
    /// client takes it from the server); the key names (nickname_list, nickname_id, nickname,
    /// nickname_desc) are.
    /// </summary>
    public static readonly IReadOnlyList<(int Id, string Name, string Description)> Catalogue =
    [
        (1, "Rookie", "Just getting started."),
        (2, "Challenger", "Takes on anyone."),
        (3, "Playmaker", "Creates chances for the team."),
        (4, "Goal Machine", "Always on the scoresheet."),
        (5, "Iron Wall", "Hard to score against."),
        (6, "Fair Player", "Plays by the rules."),
        (7, "Veteran", "Has seen it all."),
        (8, "Champion", "Winner of many matches."),
    ];

    public static bool IsKnown(int id) => id == 0 || Catalogue.Any(n => n.Id == id);

    /// <summary>The CMD_GET_NICKNAME_COMPE reply, as the body of the NICKNAME url.</summary>
    public static string RenderCatalogue()
    {
        var list = Catalogue.Select(n => new KvMessage()
            .Set("nickname_id", n.Id)
            .Set("nickname", n.Name)
            .Set("nickname_desc", n.Description)).ToList();
        return new KvWriter().Write(new KvMessage()
            .Set("result", "NOERR")
            .Set("nickname_list_num", list.Count)
            .Set("nickname_list", list)).TrimEnd('\0');
    }

    [Command("CMD_SET_NICKNAME", Roles = ServiceRole.All, Profiles = GameProfiles.Since2012,
        RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> SetNickname(CommandContext ctx)
    {
        var id = ctx.Request.GetInt32("nickname_id", 0);
        if (!IsKnown(id))
            return [ctx.Fail("ERR_PARAM")];

        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (player is null || player.AccountId != ctx.Session.AccountId)
            return [ctx.Fail("ERR_DATABASE")];

        player.NicknameId = id;
        await players.SaveAsync(ctx.CancellationToken);
        ctx.Session.Card = ctx.Session.Card with { NicknameId = id };
        return [ctx.Ok()];
    }

    [Command("CMD_GET_UNLOCKEDNICKNAMELIST", Roles = ServiceRole.All, Profiles = GameProfiles.Since2012,
        RequiredState = SessionState.PlayerSelected)]
    public ValueTask<KvMessage[]> GetUnlocked(CommandContext ctx)
        => Reply.Of(ctx.Ok()
            .Set("nickname_id_list_num", Catalogue.Count)
            .Set("nickname_id_list", new KvArray(Catalogue.Select(n => (object?)n.Id).ToArray())));
}
