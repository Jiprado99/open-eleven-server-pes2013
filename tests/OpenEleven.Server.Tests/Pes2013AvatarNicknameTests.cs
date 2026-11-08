// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Handlers;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

public class Pes2013AvatarNicknameTests
{
    [Fact]
    public async Task The_url_list_announces_the_AVATAR_and_NICKNAME_urls_pes2013_requests_pictures_through()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2013Pc");
        var urls = Assert.Single(await harness.DispatchAsync(harness.NewSession(ServiceRole.Gate, SessionState.Connected), "CMD_GET_URLLIST"));

        var list = urls.GetList("ulist");
        Assert.EndsWith("/avatar", Assert.Single(list, u => u.GetString("type") == "AVATAR").GetString("url"));
        Assert.EndsWith("/nickname", Assert.Single(list, u => u.GetString("type") == "NICKNAME").GetString("url"));
    }

    [Fact]
    public async Task The_AVATAR_url_serves_the_stored_picture_and_size_0_when_there_is_none()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2013Pc");
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        db.Avatars.Add(new PlayerAvatar { Pid = 7, Data = [1, 2, 3, 4], Md5 = "ABCD" });
        await db.SaveChangesAsync();

        var got = new KvReader().Parse(await AvatarHandlers.RenderHttpAsync(db, "CMD_GET_AVATAR_HTTP pid=7", default));
        Assert.Equal("NOERR", got.GetString("result"));
        Assert.Equal(4, got.GetInt32("avatar_data_size"));
        Assert.Equal("01020304", got.GetString("avatar_data"));
        Assert.Equal("ABCD", got.GetString("avatar_md5"));

        var none = new KvReader().Parse(await AvatarHandlers.RenderHttpAsync(db, "pid=8&rqid=1", default));
        Assert.Equal(0, none.GetInt32("avatar_data_size"));
        Assert.False(none.Has("avatar_data"));
    }

    [Fact]
    public void The_nickname_catalogue_lists_every_id_with_its_text()
    {
        var reply = new KvReader().Parse(NicknameHandlers.RenderCatalogue());
        var list = reply.GetList("nickname_list");
        Assert.Equal(list.Count, reply.GetInt32("nickname_list_num"));
        Assert.All(list, n => Assert.True(n.Has("nickname_id") && n.Has("nickname") && n.Has("nickname_desc")));
        Assert.True(NicknameHandlers.IsKnown(0));
        Assert.True(NicknameHandlers.IsKnown(list[0].GetInt32("nickname_id")));
        Assert.False(NicknameHandlers.IsKnown(9999));
    }

    [Fact]
    public async Task A_picked_nickname_shows_in_room_entries_and_unknown_ids_are_refused()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2013Pc");
        var session = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        session.Pid = 1;
        session.PlayerName = "marcos1";
        session.Card = session.Card with { NicknameId = 3 };

        Assert.Equal(3, ListWatchEntries.RoomPlayerEntry(session, default!, GameProfile.Pes2013Pc).GetInt32("nickname_id"));

        var refused = Assert.Single(await harness.DispatchAsync(session, "CMD_SET_NICKNAME", withFields: r => r.Set("nickname_id", 9999)));
        Assert.Equal("ERR_PARAM", refused.GetString("result"));
    }
}
