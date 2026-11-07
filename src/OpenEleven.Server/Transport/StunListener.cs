// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace OpenEleven.Server.Transport;

/// <summary>
/// STUN Binding responder on the two classic ports (3478 primary, 3479 alternate), answering both
/// generations of client from the same sockets:
/// <list type="bullet">
/// <item>PES 2013's NTL library speaks RFC 5389: its request carries the magic cookie 2112A442 and it only
/// reads its address back from XOR-MAPPED-ADDRESS (a classic reply left reflexive_addr all zeros).</item>
/// <item>PES 6 speaks RFC 3489: no cookie, and it expects MAPPED-ADDRESS plus SOURCE-ADDRESS and
/// CHANGED-ADDRESS, and honours CHANGE-REQUEST for its NAT test. This mirrors the reference stun.py.</item>
/// </list>
/// Both games fix the port at 3478, so one server has to serve both.
/// </summary>
/// <remarks>
/// ponytail: experimental. Only Binding requests are answered (no TURN, no auth). Every datagram is
/// logged in hex so the real traffic can be read from server.log. A port that is already taken (for
/// example by stun.py) is logged and skipped. The RFC 3489 side has been checked against the reference
/// server's bytes, not yet against a live PES 6 client.
/// </remarks>
public sealed class StunListener(
    int[] ports, IPAddress publicAddress, ILogger<StunListener> log, IPAddress? bindAddress = null) : BackgroundService
{
    private const uint MagicCookie = 0x2112A442;

    private readonly Dictionary<int, UdpClient> _sockets = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var port in ports)
        {
            try
            {
                _sockets[port] = new UdpClient(new IPEndPoint(bindAddress ?? IPAddress.Any, port));
                log.LogInformation("STUN: listening on {Bind} UDP {Port}, naming {Public} as the server",
                    bindAddress?.ToString() ?? "all interfaces", port, publicAddress);
            }
            catch (SocketException ex)
            {
                log.LogError("STUN: cannot listen on UDP {Port}: {Error}", port, ex.SocketErrorCode);
            }
        }

        try
        {
            await Task.WhenAll(_sockets.Select(s => ListenAsync(s.Key, s.Value, stoppingToken)));
        }
        finally
        {
            foreach (var socket in _sockets.Values)
                socket.Dispose();
        }
    }

    private async Task ListenAsync(int port, UdpClient udp, CancellationToken ct)
    {
        // The classic CHANGE-REQUEST asks for the answer to come from the other port.
        var otherPort = ports.Length > 1 ? (port == ports[0] ? ports[1] : ports[0]) : port;

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // ICMP port unreachable from an earlier reply surfaces here on Windows.
                log.LogDebug("STUN: receive on {Port}: {Error}", port, ex.SocketErrorCode);
                continue;
            }

            var data = received.Buffer;
            log.LogInformation("STUN: {Length} bytes on UDP {Port} from {Remote}: {Hex}",
                data.Length, port, received.RemoteEndPoint, Convert.ToHexString(data));

            if (BuildResponse(data, received.RemoteEndPoint, publicAddress, port, otherPort, out var changePort) is not { } reply)
                continue;

            var sender = changePort && _sockets.TryGetValue(otherPort, out var other) ? other : udp;
            var target = ReplyTarget(data, received.RemoteEndPoint);
            await sender.SendAsync(reply, target, ct);
            log.LogInformation("STUN: answered {Target} on UDP {Port} ({Style})",
                target, port, IsRfc5389(data) ? "RFC 5389" : "RFC 3489");
        }
    }

    /// <summary>True when the request carries the magic cookie, which only RFC 5389 clients send.</summary>
    internal static bool IsRfc5389(byte[] request)
        => request.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(4)) == MagicCookie;

    /// <summary>
    /// The Binding success response in the style the requester speaks, or null when the datagram is not a
    /// Binding request. <paramref name="changePort"/> is set when a classic CHANGE-REQUEST wants the answer
    /// from the alternate port (the change-IP and change-port bits are both answered that way).
    /// </summary>
    internal static byte[]? BuildResponse(
        byte[] request, IPEndPoint remote, IPAddress publicAddress, int receivedPort, int otherPort, out bool changePort)
    {
        changePort = false;
        if (request.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(request) != 0x0001
            || remote.AddressFamily != AddressFamily.InterNetwork)
            return null;

        var ip = remote.Address.GetAddressBytes();
        var port = (ushort)remote.Port;
        var body = new List<byte>();

        // MAPPED-ADDRESS (0x0001) goes to everyone.
        body.AddRange(Address(0x0001, ip, port));

        if (IsRfc5389(request))
        {
            // XOR-MAPPED-ADDRESS: port and address XORed with the cookie.
            var cookie = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(cookie, MagicCookie);
            var xored = new byte[4];
            for (var i = 0; i < 4; i++)
                xored[i] = (byte)(ip[i] ^ cookie[i]);
            body.AddRange(Address(0x0020, xored, (ushort)(port ^ (MagicCookie >> 16))));
        }
        else
        {
            // RFC 3489: SOURCE-ADDRESS and CHANGED-ADDRESS, and the CHANGE-REQUEST flags.
            var self = publicAddress.GetAddressBytes();
            body.AddRange(Address(0x0004, self, (ushort)receivedPort));
            body.AddRange(Address(0x0005, self, (ushort)otherPort));
            changePort = (ChangeRequestFlags(request) & 0x6) != 0;
        }

        var response = new byte[20 + body.Count];
        BinaryPrimitives.WriteUInt16BigEndian(response, 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), (ushort)body.Count);
        request.AsSpan(4, 16).CopyTo(response.AsSpan(4));   // cookie + transaction id (or the 16-byte id), echoed
        body.CopyTo(response.AsSpan(20));
        return response;
    }

    /// <summary>An 8-byte address attribute: type, length 8, a zero byte, family 1, port, IPv4.</summary>
    private static byte[] Address(ushort type, byte[] ip, ushort port)
    {
        var attr = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(attr.AsSpan(0), type);
        BinaryPrimitives.WriteUInt16BigEndian(attr.AsSpan(2), 8);
        attr[5] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(attr.AsSpan(6), port);
        ip.CopyTo(attr, 8);
        return attr;
    }

    /// <summary>The value of the CHANGE-REQUEST attribute (0x0003), or 0 when the request has none.</summary>
    private static uint ChangeRequestFlags(byte[] request)
        => FindAttribute(request, 0x0003) is { Length: >= 4 } value
            ? BinaryPrimitives.ReadUInt32BigEndian(value)
            : 0;

    /// <summary>
    /// Where the answer goes. A classic (RFC 3489) request may carry RESPONSE-ADDRESS (0x0002), the
    /// endpoint it really listens on: the client's STUN socket and its P2P socket can differ, and a reply
    /// to the datagram's source port is then never read (the request is retried forever). Only the port
    /// is honoured, and only on the sender's own IP, so a spoofed request cannot aim the reply at a third host.
    /// </summary>
    internal static IPEndPoint ReplyTarget(byte[] request, IPEndPoint remote)
    {
        if (request.Length < 20 || IsRfc5389(request)
            || FindAttribute(request, 0x0002) is not { Length: >= 8 } value || value[1] != 0x01)
            return remote;

        var port = BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(2));
        var address = new IPAddress(value.AsSpan(4, 4));
        return port != 0 && address.Equals(remote.Address) ? new IPEndPoint(remote.Address, port) : remote;
    }

    /// <summary>The value bytes of the first attribute of <paramref name="type"/>, or null.</summary>
    private static byte[]? FindAttribute(byte[] request, ushort type)
    {
        var length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2)), request.Length - 20);
        var i = 20;
        while (i + 4 <= 20 + length)
        {
            var size = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(i + 2));
            if (i + 4 + size > request.Length)
                return null;
            if (BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(i)) == type)
                return request.AsSpan(i + 4, size).ToArray();
            i += 4 + size + ((4 - size % 4) % 4);
        }

        return null;
    }
}
