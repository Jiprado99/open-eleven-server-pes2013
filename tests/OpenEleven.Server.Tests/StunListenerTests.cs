// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Buffers.Binary;
using System.Net;
using OpenEleven.Server.Transport;

namespace OpenEleven.Server.Tests;

/// <summary>
/// The STUN responder serves PES 2013 (RFC 5389, reads XOR-MAPPED-ADDRESS) and PES 6 (RFC 3489,
/// MAPPED/SOURCE/CHANGED) from the same port, told apart by the request's magic cookie.
/// </summary>
public class StunListenerTests
{
    private static readonly IPAddress Public = IPAddress.Parse("26.166.146.4");
    private static readonly IPEndPoint Client = new(IPAddress.Parse("26.50.68.240"), 5730);

    private static byte[] Request(byte[] transaction16, byte[]? attributes = null)
    {
        attributes ??= [];
        var request = new byte[20 + attributes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(request, 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), (ushort)attributes.Length);
        transaction16.CopyTo(request, 4);
        attributes.CopyTo(request, 20);
        return request;
    }

    private static readonly byte[] Rfc5389Id = [0x21, 0x12, 0xA4, 0x42, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    // The 16 random bytes the PES 6 client sent in the reference server's sniff.log.
    private static readonly byte[] Rfc3489Id =
        [0x32, 0xdd, 0x4a, 0x27, 0xd3, 0xf4, 0x50, 0x70, 0x2b, 0x9a, 0x98, 0x2b, 0x7e, 0x4e, 0xb4, 0x73];

    private static Dictionary<ushort, byte[]> Attributes(byte[] response)
    {
        var result = new Dictionary<ushort, byte[]>();
        var i = 20;
        while (i + 4 <= response.Length)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(i));
            var size = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(i + 2));
            result[type] = response.AsSpan(i + 4, size).ToArray();
            i += 4 + size;
        }

        return result;
    }

    [Fact]
    public void Rfc5389_request_gets_xor_mapped_address()
    {
        var reply = StunListener.BuildResponse(Request(Rfc5389Id), Client, Public, 3478, 3479, out var changePort);

        Assert.NotNull(reply);
        Assert.False(changePort);
        Assert.Equal(0x0101, BinaryPrimitives.ReadUInt16BigEndian(reply));
        Assert.Equal(Rfc5389Id, reply[4..20]);

        var attrs = Attributes(reply);
        var xor = attrs[0x0020];
        var port = BinaryPrimitives.ReadUInt16BigEndian(xor.AsSpan(2)) ^ 0x2112;
        var ip = new byte[] { (byte)(xor[4] ^ 0x21), (byte)(xor[5] ^ 0x12), (byte)(xor[6] ^ 0xA4), (byte)(xor[7] ^ 0x42) };
        Assert.Equal(Client.Port, port);
        Assert.Equal(Client.Address, new IPAddress(ip));
        Assert.False(attrs.ContainsKey(0x0004));
        Assert.False(attrs.ContainsKey(0x0005));
    }

    [Fact]
    public void Rfc3489_request_gets_the_classic_reply_without_xor_mapped_address()
    {
        var reply = StunListener.BuildResponse(Request(Rfc3489Id), Client, Public, 3478, 3479, out var changePort);

        Assert.NotNull(reply);
        Assert.False(changePort);
        Assert.Equal(Rfc3489Id, reply[4..20]);

        var attrs = Attributes(reply);
        Assert.False(attrs.ContainsKey(0x0020));

        // MAPPED-ADDRESS is the sender; SOURCE and CHANGED name the server's two ports.
        Assert.Equal(Client.Port, BinaryPrimitives.ReadUInt16BigEndian(attrs[0x0001].AsSpan(2)));
        Assert.Equal(Client.Address, new IPAddress(attrs[0x0001][4..8]));
        Assert.Equal(3478, BinaryPrimitives.ReadUInt16BigEndian(attrs[0x0004].AsSpan(2)));
        Assert.Equal(3479, BinaryPrimitives.ReadUInt16BigEndian(attrs[0x0005].AsSpan(2)));
        Assert.Equal(Public, new IPAddress(attrs[0x0004][4..8]));
    }

    [Theory]
    [InlineData(0x2u, true)]
    [InlineData(0x4u, true)]
    [InlineData(0x0u, false)]
    public void Classic_change_request_asks_for_the_alternate_port(uint flags, bool expected)
    {
        // CHANGE-REQUEST: type 0x0003, length 4, the flags.
        var attribute = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(attribute, 0x0003);
        BinaryPrimitives.WriteUInt16BigEndian(attribute.AsSpan(2), 4);
        BinaryPrimitives.WriteUInt32BigEndian(attribute.AsSpan(4), flags);

        StunListener.BuildResponse(Request(Rfc3489Id, attribute), Client, Public, 3478, 3479, out var changePort);

        Assert.Equal(expected, changePort);
    }

    [Fact]
    public void Datagrams_that_are_not_binding_requests_are_ignored()
    {
        Assert.Null(StunListener.BuildResponse(new byte[10], Client, Public, 3478, 3479, out _));

        var notBinding = Request(Rfc5389Id);
        BinaryPrimitives.WriteUInt16BigEndian(notBinding, 0x0003);
        Assert.Null(StunListener.BuildResponse(notBinding, Client, Public, 3478, 3479, out _));
    }
}
