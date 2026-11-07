// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Net;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;
using OpenEleven.Server.Transport;

namespace OpenEleven.Server.Tests;

/// <summary>
/// The corrections for a client whose STUN result is missing or whose host address is its LAN one.
/// The sa_data values are the ones captured in server.log.
/// </summary>
public class NtlAddressTests
{
    private static KvMessage Record(string saData) => new KvMessage().Set("address_family", 1).Set("sa_data", saData);

    private static Session NewSession(string host, string reflexive) => new()
    {
        Id = Guid.NewGuid(),
        Remote = new IPEndPoint(IPAddress.Loopback, 1),
        Role = ServiceRole.Menu,
        NtlHostAddr = Record(host),
        NtlReflexiveAddr = Record(reflexive),
    };

    [Fact]
    public void Parses_and_builds_the_captured_layout()
    {
        Assert.True(NtlAddress.TryParse(Record("166200001A3244F0000000000000"), out var ip, out var port));
        Assert.Equal(IPAddress.Parse("26.50.68.240"), ip);
        Assert.Equal(5730, port);
        Assert.Equal("166200001A3244F0000000000000", NtlAddress.Build(ip, port).GetString("sa_data"));
        Assert.False(NtlAddress.TryParse(NtlAddress.Zero(), out _, out _));
    }

    [Fact]
    public void Empty_reflexive_is_filled_from_the_tcp_address_with_the_host_port()
    {
        var filled = NtlAddress.FillReflexive(NtlAddress.Zero(), Record("16620000C0A80132000000000000"), IPAddress.Parse("26.165.241.98"));

        Assert.Equal("166200001AA5F162000000000000", filled!.GetString("sa_data"));
    }

    [Fact]
    public void A_reflexive_the_client_reported_is_kept()
    {
        var reported = Record("166200001AA5F162000000000000");

        Assert.Same(reported, NtlAddress.FillReflexive(reported, Record("16620000C0A80132000000000000"), IPAddress.Parse("1.2.3.4")));
    }

    [Fact]
    public void Private_host_is_replaced_by_the_public_endpoint_for_other_networks()
    {
        var lan = NewSession("16620000C0A80132000000000000", "166200001AA5F162000000000000");
        var other = NewSession("166200001A3244F0000000000000", "166200001A3244F0000000000000");

        Assert.Equal("166200001AA5F162000000000000", NtlAddress.HostFor(other, lan)!.GetString("sa_data"));
    }

    [Fact]
    public void Private_host_is_kept_for_a_peer_behind_the_same_nat()
    {
        var a = NewSession("16620000C0A80132000000000000", "166200001AA5F162000000000000");
        var b = NewSession("16620000C0A80133000000000000", "166200001AA5F162000000000000");

        Assert.Equal("16620000C0A80132000000000000", NtlAddress.HostFor(b, a)!.GetString("sa_data"));
    }

    [Fact]
    public void Public_host_is_never_touched()
    {
        var vpn = NewSession("166200001A77BA6D000000000000", "166200001A77BA6D000000000000");

        Assert.Same(vpn.NtlHostAddr, NtlAddress.HostFor(null, vpn));
    }

    [Fact]
    public void Classic_reply_goes_to_response_address_on_the_senders_own_ip_only()
    {
        // 40 bytes captured: RESPONSE-ADDRESS 26.165.241.98:5739 and CHANGE-REQUEST 6; sent from port 10987.
        var request = Convert.FromHexString(
            "000100145873494410E569C81752DB87DE3AAFA0000200080001166B1AA5F1620003000400000006");
        var sender = new IPEndPoint(IPAddress.Parse("26.165.241.98"), 10987);

        Assert.Equal(new IPEndPoint(sender.Address, 5739), StunListener.ReplyTarget(request, sender));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("9.9.9.9"), 10987),
            StunListener.ReplyTarget(request, new IPEndPoint(IPAddress.Parse("9.9.9.9"), 10987)));
    }
}
