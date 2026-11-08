// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.IO.Compression;
using System.Text;

namespace OpenEleven.Server.MasterLeague;

/// <summary>
/// Reads the footballers and squads out of a PES 2013 installation.
/// <list type="bullet">
/// <item><c>img\dtXX.img</c> are AFS archives: "AFS\0", entry count at +4, then (offset, size) pairs
/// from +8. Entries are usually WESYS-compressed: a 16-byte header <c>00 01 01 "WESYS"</c> (packed
/// size at +8, unpacked size at +12) and a zlib stream.</item>
/// <item>Kitserver's afs2fs replaces entries with <c>kitserver13\&lt;dir&gt;\img\dtXX.img\unnamed_N.bin</c>
/// for each <c>img.dir</c> of its config.txt; a later dir wins, as in the game.</item>
/// <item>dt0f entry 1975: 28-byte header, then 228-byte footballers: +4 id, +8 Japanese name (46),
/// +54 name (46, UTF-8), +100 shirt name (16); +125 is 0 for goalkeepers, and the 21 bytes at
/// +126..+130, +132..+141, +143..+148 are the abilities (low 7 bits; bit 7 is another flag).</item>
/// <item>dt0f entry 1980: 32-byte header, then 164-byte squads: +0 team id, +4 32 × u32 footballer
/// ids (0 = empty), +132 32 shirt numbers (0xFF = empty).</item>
/// </list>
/// The overall is the mean of the ten best abilities, which puts Messi at 95, Xavi at 92 and the
/// bulk of the database between 70 and 85.
/// ponytail: the game's own overall is position-weighted and not recovered; nationality, age and
/// position are not decoded yet.
/// </summary>
public static class GameDataReader
{
    public const int PlayersEntry = 1975;
    public const int SquadsEntry = 1980;
    private const int PlayerSize = 228;
    private const int SquadSize = 164;
    private static readonly int[] AbilityOffsets =
        [.. Enumerable.Range(126, 5), .. Enumerable.Range(132, 10), .. Enumerable.Range(143, 6)];

    public static MlCatalogData Read(string gameDirectory)
    {
        var players = ParsePlayers(ReadEntry(gameDirectory, "dt0f.img", PlayersEntry));
        var squads = ParseSquads(ReadEntry(gameDirectory, "dt0f.img", SquadsEntry));
        var names = ReadTeamNames(Path.Combine(gameDirectory, "kitserver13", "teamlist.txt"));
        return new MlCatalogData
        {
            Players = players,
            Teams = squads.Select(s => s with { Name = names.GetValueOrDefault(s.Id, "") }).ToList(),
        };
    }

    /// <summary>One entry of an AFS archive, with Kitserver's replacement applied and unpacked.</summary>
    public static byte[] ReadEntry(string gameDirectory, string archive, int index)
    {
        byte[]? data = null;
        foreach (var dir in KitserverDirs(gameDirectory))
        {
            var file = Path.Combine(gameDirectory, "kitserver13", dir, "img", archive, $"unnamed_{index}.bin");
            if (File.Exists(file))
                data = File.ReadAllBytes(file);
        }

        if (data is null)
        {
            using var afs = File.OpenRead(Path.Combine(gameDirectory, "img", archive));
            var head = new byte[8];
            afs.ReadExactly(head);
            if (head[0] != 'A' || head[1] != 'F' || head[2] != 'S')
                throw new InvalidDataException($"{archive} is not an AFS archive");
            if (index >= BitConverter.ToInt32(head, 4))
                throw new InvalidDataException($"{archive} has no entry {index}");
            afs.Position = 8 + index * 8;
            var slot = new byte[8];
            afs.ReadExactly(slot);
            afs.Position = BitConverter.ToUInt32(slot, 0);
            data = new byte[BitConverter.ToInt32(slot, 4)];
            afs.ReadExactly(data);
        }

        return Unpack(data);
    }

    public static byte[] Unpack(byte[] data)
    {
        if (data.Length < 16 || data[3] != 'W' || data[4] != 'E' || data[5] != 'S' || data[6] != 'Y' || data[7] != 'S')
            return data;
        using var zlib = new ZLibStream(new MemoryStream(data, 16, data.Length - 16), CompressionMode.Decompress);
        using var output = new MemoryStream(BitConverter.ToInt32(data, 12));
        zlib.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>The img.dir entries of Kitserver's config.txt, in order (none without Kitserver).</summary>
    public static List<string> KitserverDirs(string gameDirectory)
    {
        var config = Path.Combine(gameDirectory, "kitserver13", "config.txt");
        if (!File.Exists(config))
            return [];
        return File.ReadLines(config)
            .Select(l => l.Trim().TrimStart('﻿'))
            .Where(l => l.StartsWith("img.dir", StringComparison.OrdinalIgnoreCase) && l.Contains('='))
            .Select(l => l[(l.IndexOf('=') + 1)..].Trim().Trim('"'))
            .Where(d => d.Length > 0 && !d.Contains(".."))
            .ToList();
    }

    public static List<MlFootballer> ParsePlayers(byte[] data)
    {
        var list = new List<MlFootballer>();
        for (var o = 28; o + PlayerSize <= data.Length; o += PlayerSize)
        {
            var id = BitConverter.ToInt32(data, o + 4);
            var name = Text(data, o + 54, 46);
            if (id <= 0 || name.Length == 0)
                continue;
            var abilities = AbilityOffsets.Select(i => data[o + i] & 0x7F).ToArray();
            var overall = (int)Math.Round(abilities.OrderByDescending(a => a).Take(10).Average());
            list.Add(new MlFootballer(id, name, Math.Clamp(overall, 40, 99), data[o + 125] == 0));
        }

        return list;
    }

    public static List<MlSquad> ParseSquads(byte[] data)
    {
        var list = new List<MlSquad>();
        for (var o = 32; o + SquadSize <= data.Length; o += SquadSize)
        {
            var members = new List<MlSquadMember>();
            for (var i = 0; i < 32; i++)
            {
                var id = BitConverter.ToInt32(data, o + 4 + i * 4);
                if (id > 0)
                    members.Add(new MlSquadMember(id, data[o + 132 + i] == 0xFF ? 0 : data[o + 132 + i]));
            }

            list.Add(new MlSquad(BitConverter.ToInt32(data, o), "", members));
        }

        return list;
    }

    /// <summary>Kitserver's teamlist.txt: "id:  140 (0x008c) MASTERLEAGUE DEFAULT".</summary>
    private static Dictionary<int, string> ReadTeamNames(string file)
    {
        var names = new Dictionary<int, string>();
        if (!File.Exists(file))
            return names;
        foreach (var line in File.ReadLines(file))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^id:\s*(\d+)\s*\(0x[0-9a-fA-F]+\)\s*(.*)$");
            if (m.Success)
                names[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value.Trim();
        }

        return names;
    }

    private static string Text(byte[] data, int offset, int length)
    {
        var end = Array.IndexOf(data, (byte)0, offset, length);
        return Encoding.UTF8.GetString(data, offset, (end < 0 ? offset + length : end) - offset).Trim();
    }
}

public sealed record MlFootballer(int Id, string Name, int Overall, bool Goalkeeper);

public sealed record MlSquadMember(int Id, int Number);

public sealed record MlSquad(int Id, string Name, List<MlSquadMember> Members);

public sealed class MlCatalogData
{
    public List<MlFootballer> Players { get; set; } = [];
    public List<MlSquad> Teams { get; set; } = [];
}
