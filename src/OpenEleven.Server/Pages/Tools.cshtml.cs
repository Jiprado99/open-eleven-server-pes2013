// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Pages;

/// <summary>
/// Client-side helpers for PES 2013 players who connect over a VPN such as Radmin: the hosts-file
/// entries and the launcher scripts. Only PES 2013 has the problem they work around, so other
/// titles get a 404 rather than a page about a game they are not running.
/// </summary>
public sealed class ToolsModel(GameProfile game, ServerCatalog catalog) : PageModel
{
    /// <summary>The host names PES 2013 and its NTL library look up, all of which the server answers.</summary>
    private static readonly string[] HostNames =
    [
        "ntl.konamionline.com",
        "info.service.konamionline.com",
        "pes13-pc-gate.winning-eleven.net",
        "pes13pcstun-e.winning-eleven.net",
        "resic06.konami",
    ];

    public string ServerAddress => catalog.ServerAddress;

    public IReadOnlyDictionary<string, string> Files => ToolDownloads.Files;

    /// <summary>The block to paste into the hosts file.</summary>
    public string HostsBlock => string.Join('\n', HostNames.Select(h => $"{ServerAddress}  {h}"));

    public IActionResult OnGet() => game == GameProfile.Pes2013Pc ? Page() : NotFound();
}
