// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Pages;
using OpenEleven.Server.State;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Tests;

/// <summary>The /tools page and the fixed list of helper scripts it offers.</summary>
public class ToolsPageTests
{
    [Theory]
    [InlineData("..\\appsettings.json")]
    [InlineData("../conf/server.yaml")]
    [InlineData("OpenEleven.Server.dll")]
    [InlineData("")]
    public void Only_the_published_names_can_be_downloaded(string name)
        => Assert.Null(ToolDownloads.Find(name));

    [Fact]
    public void Every_offered_script_is_copied_next_to_the_binary()
    {
        foreach (var name in ToolDownloads.Files.Keys)
            Assert.True(ToolDownloads.Find(name) is { } path && new FileInfo(path).Length > 0, $"{name} is missing from <app>/tools");
    }

    [Fact]
    public async Task The_page_exists_only_for_pes_2013_and_lists_the_server_address_in_the_hosts_block()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var catalog = harness.Services.GetRequiredService<ServerCatalog>();

        Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(new ToolsModel(GameProfile.Pes2013Pc, catalog).OnGet());
        Assert.IsType<NotFoundResult>(new ToolsModel(GameProfile.Pes2010Pc, catalog).OnGet());

        var hosts = new ToolsModel(GameProfile.Pes2013Pc, catalog).HostsBlock.Split('\n');
        Assert.Equal(5, hosts.Length);
        Assert.All(hosts, line => Assert.StartsWith(catalog.ServerAddress + "  ", line));
        Assert.Contains(hosts, line => line.EndsWith("pes13pcstun-e.winning-eleven.net"));
    }
}
