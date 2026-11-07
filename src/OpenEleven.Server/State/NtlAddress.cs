// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using OpenEleven.Protocol.Kv;

namespace OpenEleven.Server.State;

/// <summary>
/// The {address_family, sa_data} records of PES 2013's NTL report, and the two corrections the server
/// makes to them. <c>sa_data</c> is 28 hex characters: port (2 bytes, big endian), two zero bytes,
/// the IPv4 address (4 bytes), six zero bytes. Observed: <c>166200001A3244F0000000000000</c> is
/// 26.50.68.240:5730.
/// </summary>
public static class NtlAddress
{
    private const string Empty = "0000000000000000000000000000";

    public static bool TryParse(KvMessage? record, out IPAddress address, out int port)
    {
        address = IPAddress.Any;
        port = 0;
        var hex = record?.GetString("sa_data");
        if (hex is not { Length: >= 16 }
            || !int.TryParse(hex.AsSpan(0, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out port)
            || !uint.TryParse(hex.AsSpan(8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ip))
            return false;

        address = new IPAddress([(byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip]);
        return port > 0 && !address.Equals(IPAddress.Any);
    }

    public static KvMessage Build(IPAddress address, int port, int family = 1)
    {
        var ip = address.GetAddressBytes();
        return new KvMessage()
            .Set("address_family", family)
            .Set("sa_data", $"{port:X4}0000{Convert.ToHexString(ip)}000000000000");
    }

    public static KvMessage Zero() => new KvMessage().Set("address_family", 1).Set("sa_data", Empty);

    /// <summary>Loopback, link-local and the RFC 1918 ranges: addresses a peer on another network cannot reach.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = address.GetAddressBytes();
        return b[0] is 10 or 127
               || (b[0] == 172 && b[1] is >= 16 and <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>
    /// The reflexive endpoint to store for a session. The client's STUN request can fail (the game sends it
    /// from its LAN address and a VPN drops it), which leaves the record empty; the address the server sees
    /// on the TCP connection is then the best public address there is. The port is the client's own local
    /// port from <c>host_addr</c>, which is the one its STUN socket and P2P socket share.
    /// </summary>
    public static KvMessage? FillReflexive(KvMessage? reflexive, KvMessage? host, IPAddress remote)
    {
        if (TryParse(reflexive, out _, out _))
            return reflexive;
        if (host is null || !TryParse(host, out _, out var port) || remote.AddressFamily != AddressFamily.InterNetwork
            || IPAddress.IsLoopback(remote))
            return reflexive;
        return Build(remote, port, host.GetInt32("address_family", 1));
    }

    /// <summary>
    /// The host (local) endpoint to show <paramref name="viewer"/>. A private host address is only useful to a
    /// peer behind the same NAT; to everyone else it is unreachable, and on a common LAN range
    /// (192.168.1.x) it may even point at the viewer's own neighbour. When the two players do not share a
    /// public address the peer's reflexive endpoint is offered in its place.
    /// </summary>
    public static KvMessage? HostFor(Session? viewer, Session peer)
    {
        if (peer.NtlHostAddr is null || !TryParse(peer.NtlHostAddr, out var host, out _) || !IsPrivate(host))
            return peer.NtlHostAddr;
        if (!TryParse(peer.NtlReflexiveAddr, out var peerPublic, out _) || IsPrivate(peerPublic))
            return peer.NtlHostAddr;

        var sameNat = viewer is not null
                      && TryParse(viewer.NtlReflexiveAddr, out var viewerPublic, out _)
                      && viewerPublic.Equals(peerPublic);
        return sameNat ? peer.NtlHostAddr : peer.NtlReflexiveAddr;
    }
}
