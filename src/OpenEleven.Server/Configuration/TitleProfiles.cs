// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Reflection;

namespace OpenEleven.Server.Configuration;

/// <summary>Maps a configured <see cref="GameProfile"/> to its per-title profile assembly.</summary>
public static class TitleProfiles
{
    /// <summary>
    /// The display name used in log output for a title (e.g. "PES 2011 (PC)").
    /// </summary>
    public static string DisplayName(GameProfile profile) => profile switch
    {
        GameProfile.Pes2010Pc => "PES 2010 (PC)",
        GameProfile.Pes2011Pc => "PES 2011 (PC)",
        GameProfile.Pes2012Pc => "PES 2012 (PC)",
        GameProfile.Pes2013Pc => "PES 2013 (PC)",
        _ => profile.ToString(),
    };

    /// <summary>
    /// The team ids a title's team-select screen accepts (useable_team_id[]). Team ids differ per title.
    /// </summary>
    public static int[] UseableTeams(GameProfile profile) => profile switch
    {
        GameProfile.Pes2010Pc => Pes2010Teams,
        GameProfile.Pes2011Pc => Pes2011Teams,
        GameProfile.Pes2012Pc => Pes2012Teams,
        GameProfile.Pes2013Pc => Pes2013Teams,
        _ => [],
    };

    /// <summary>
    /// The title's list with the operator's changes applied: <see cref="TeamOptions.Include"/> first (so a
    /// mod's team survives the cut), then the built-in ids, minus <see cref="TeamOptions.Exclude"/>,
    /// without duplicates and at most <see cref="MaxUseableTeams"/> (what the client keeps).
    /// </summary>
    public static int[] UseableTeams(GameProfile profile, TeamOptions? teams)
    {
        var builtIn = UseableTeams(profile);
        if (teams is null || (teams.Include.Length == 0 && teams.Exclude.Length == 0))
            return builtIn;

        return teams.Include.Concat(builtIn)
            .Where(id => id > 0 && !teams.Exclude.Contains(id))
            .Distinct()
            .Take(MaxUseableTeams)
            .ToArray();
    }

    /// <summary>The client keeps at most this many ids from useable_team_id[] (FUN_00513610, FUN_0053bfd0).</summary>
    public const int MaxUseableTeams = 400;

    /// <summary>
    /// Every team of retail PES 2011: categories 8 (national), 0x15 (club) and 0x1f (classic),
    /// read from pes2011.exe's team table (DAT_01940b64). CMD_SET_CURRENTPLAYER's
    /// useable_team_id[] (FUN_00513610, max 400) is the whitelist the team-select screen checks
    /// (FUN_00b02360); with it empty no default team exists and the match is abandoned.
    /// </summary>
    private static readonly int[] Pes2011Teams =
    [
        21, 9, 1170, 27, 24, 13, 15, 5, 18, 8, 14, 28, 22, 1, 1164, 12, 1551, 10, 2, 16, 19, 6, 26, 31, 3, 1550,
        20, 23, 7, 17, 11, 29, 30, 4, 1040, 1041, 36, 1051, 34, 1058, 1059, 1067, 32, 35, 38, 37, 1081, 33, 1099,
        42, 43, 40, 1118, 39, 50, 1128, 45, 47, 44, 51, 48, 46, 49, 1129, 57, 1002, 54, 55, 1010, 52, 1013, 1012,
        1022, 1026, 56, 53, 1029, 1031, 1032, 1184, 1141,
        101, 107, 201, 176, 1761, 202, 102, 177, 178, 103, 173, 100, 106, 395, 396, 179, 399, 105, 400, 208, 2318,
        180, 115, 1329, 405, 182, 213, 414, 181, 113, 112, 215, 415, 217, 114, 218, 418, 219, 221, 1528, 319, 186,
        187, 320, 321, 1362, 188, 124, 323, 119, 120, 122, 236, 121, 327, 238, 123, 125, 240, 190, 243, 116, 242,
        344, 117, 339, 244, 245, 349, 246, 247, 118, 248, 250, 251, 252, 355, 255, 357, 258, 108, 111, 259, 362,
        363, 1592, 366, 172, 109, 260, 261, 263, 264, 265, 196, 110, 267, 268, 174, 269, 1203, 1205, 175, 1207,
        1733, 185, 127, 270, 133, 198, 1212, 1215, 191, 1974, 192, 193, 1750, 1746, 1747, 135, 1941, 1218, 131,
        132, 1223, 1583, 1706, 273, 197, 130, 1232, 134, 139, 138, 1252, 2293, 2297, 2298, 2308, 2344, 2345, 2346,
        2347, 2348, 2349, 2350, 2351, 2352, 2353, 2354, 2355, 2356, 2357, 2240, 2241, 2242, 2243, 2244, 2245, 2246,
        2247, 2248, 2249, 2250, 2251, 2252, 2253, 2254, 2255, 199, 200, 1700, 1701, 1778, 1781, 1774, 1927, 1923,
        1238, 1929, 1241, 1244, 2282, 2283, 1940, 1247, 274, 1248, 3040, 1255, 2191, 2209, 1256, 2284, 2285, 2210,
        2196, 2286, 2211, 1260, 2198, 2214, 2287, 2288, 2215, 1262, 2289, 2290, 2205, 2291, 2218,
        3000, 3002, 3020, 3004, 3028, 3005, 3041, 3018, 3009, 3042, 3006, 3021, 3045, 3013, 3033, 3044, 3035, 3026,
    ];

    /// <summary>
    /// Every team of retail PES 2012, read from the running game's 23 team groups (a {count, ushort
    /// list} table: 81 national teams, then the leagues and other clubs), de-duplicated in game order.
    /// CMD_SET_CURRENTPLAYER's useable_team_id[] (FUN_0053bfd0, max 400) is the whitelist the default
    /// team pick draws from; with it empty both clients forfeit the moment the host starts the match.
    /// </summary>
    private static readonly int[] Pes2012Teams =
    [
        21, 9, 1170, 27, 24, 13, 15, 5, 18, 8, 14, 28, 22, 1, 1164, 12, 1551, 10, 2, 16, 19, 6, 26, 31, 3, 1550,
        20, 23, 7, 17, 11, 29, 30, 4, 1040, 1041, 36, 1051, 34, 1058, 1059, 1067, 32, 35, 38, 37, 1081, 33,
        1099, 42, 43, 40, 1113, 39, 50, 1128, 45, 47, 44, 51, 48, 46, 49, 1129,
        57, 1002, 54, 55, 1010, 52, 1011, 1013, 1012, 1026, 56, 53, 1029, 1031, 1032, 1184, 1141,
        902, 903, 904, 905, 906, 908, 907,
        101, 107, 176, 202, 102, 177, 178, 103, 173, 100, 106, 388, 1327, 395, 396, 1909, 179, 399, 400, 208,
        209, 180, 115, 1329, 405, 1328, 2376, 213, 414, 181, 113, 215, 415, 217, 114, 218, 418, 219, 221, 1528,
        234, 186, 320, 321, 1362, 188, 124, 323, 119, 120, 122, 236, 121, 327, 2377, 238, 123, 125, 241, 190,
        243, 116, 242, 344, 117, 339, 244, 245, 349, 246, 247, 118, 254, 248, 250, 251, 252, 355,
        258, 172, 259, 108, 362, 1765, 366, 260, 261, 263, 370, 194, 264, 109, 196, 265, 363, 110, 267, 268,
        1972, 2367, 191, 1974, 2385, 2387, 1976, 1944, 2368, 1978, 192, 1979, 193, 2366, 1804, 1981,
        2293, 2297, 2298, 2308, 2344, 2345, 2346, 2347, 2348, 2349, 2350, 2351, 2352, 2353, 2354, 2355, 2356, 2357,
        2240, 2241, 2242, 2243, 2244, 2245, 2246, 2247, 2248, 2249, 2250, 2251, 2252, 2253, 2254, 2255, 199, 200,
        1195, 1197, 1203, 175, 1207, 1733, 128, 127, 270, 133, 198, 1212, 1215, 1695, 1967, 1217, 1941, 1218,
        131, 132, 1583, 1706, 273, 197, 130, 1945, 1232, 134, 139, 138,
        1264, 1771, 1781, 1236, 1238, 1924, 1240, 1244, 2283, 2358, 2359, 1247, 274, 1249, 1250, 1252, 1254,
        1256, 2360, 2191, 2361, 2285, 2284, 2286, 2211, 1907, 1260, 2213, 2198, 2287, 2362, 2200, 2363, 1262,
        1263, 2205, 2291, 2218,
        141, 142, 869, 1408, 1409, 1796, 1797, 1798, 1799, 1800, 1801, 1802, 1803, 1810, 1811, 1812, 1813,
        1709, 1710, 1711, 1712, 1713, 2311, 2312, 2313, 2314, 2315, 2316, 2364,
    ];

    /// <summary>
    /// The teams of PES 2013 with the PESEdit 2013 Patch 6.0 installed, taken from the team list the
    /// game itself writes (Kitserver's teamlist.txt, 461 entries) and the kit map of its GDB. Chosen
    /// in this order, up to the 400 ids the client keeps (see the 2011 and 2012 tables):
    /// national teams, classic teams, clubs that have a kit in the GDB, then the other clubs by id.
    /// Left out: ids the game lists as (null), the Master League placeholders and fictional teams
    /// (MASTERLEAGUE*, ML NEWFACE*, TERZAMOC, CHEQUEBERO, NAHUANOZEM), and the 18 clubs that did
    /// not fit, which are mostly lower-division slots (ids 4000+) that PESEDIT fills per league.
    /// Server:Teams:Include / Exclude adjust the list without a rebuild.
    /// </summary>
    private static readonly int[] Pes2013Teams =
    [
        // National teams.
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 26, 27, 28, 29, 30,
        31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 1009,
        1010, 1011, 1012, 1013, 1015, 1017, 1022, 1026, 1031, 1032, 1040, 1041, 1051, 1058, 1059, 1067, 1083, 1099,
        1113, 1128, 1129, 1141, 1164, 1170, 1184, 1550, 1551,
        // Classic teams.
        902, 903, 904, 905, 906, 907, 908,
        // Clubs with a kit in the GDB, the teams a player is most likely to pick.
        100, 101, 102, 103, 105, 106, 107, 108, 109, 110, 111, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123,
        124, 125, 127, 130, 131, 133, 134, 135, 136, 137, 139, 172, 173, 174, 177, 178, 179, 181, 184, 186, 190, 191,
        192, 193, 194, 195, 196, 197, 198, 199, 200, 207, 209, 210, 213, 215, 218, 234, 240, 242, 250, 252, 258, 259,
        260, 261, 263, 265, 266, 268, 269, 273, 274, 320, 321, 327, 333, 362, 370, 388, 391, 395, 396, 399, 400, 418,
        1207, 1208, 1212, 1217, 1218, 1232, 1244, 1245, 1246, 1247, 1248, 1249, 1250, 1252, 1254, 1255, 1327, 1695,
        1746, 1804, 1909, 1921, 1924, 1931, 1932, 1935, 1944, 1972, 1974, 1976, 1978, 1979, 1981, 1986, 2000, 2001,
        2002, 2003, 2004, 2005, 2006, 2007, 2008, 2009, 2010, 2011, 2012, 2013, 2014, 2015, 2016, 2017, 2018, 2019,
        2020, 2021, 2022, 2023, 2024, 2025, 2026, 2027, 2028, 2029, 2030, 2031, 2032, 2033, 2034, 2240, 2241, 2242,
        2243, 2244, 2245, 2246, 2247, 2248, 2249, 2250, 2251, 2252, 2253, 2254, 2293, 2294, 2295, 2296, 2297, 2298,
        2308, 2344, 2345, 2346, 2347, 2348, 2349, 2350, 2351, 2352, 2353, 2354, 2355, 2356, 2357, 2367, 2368, 2376,
        2383, 2387, 2388, 2451, 2453, 4001, 4002, 4004, 4005, 4006, 4007, 4009, 4012, 4018, 4019, 4020, 4021, 4022,
        4023, 4024, 4025, 4026, 4027, 4028, 4029, 4030, 4031, 4032, 4034, 4035, 4037, 4039, 4040, 4041, 4042, 4043,
        4044, 4045, 4046, 4047, 4049, 4050, 4051, 4052, 4053, 4054, 4055, 4056, 4057, 4058, 4059, 4060, 4061, 4062,
        4063, 4064, 4065, 4066, 4067, 4068, 4069, 4070, 4071, 4072, 4073, 4074, 4075, 4076, 4077, 4078, 4079, 4080,
        4081, 4082, 4083, 4084, 4085, 4086, 4087,
        // Remaining clubs in id order, up to the 400-entry limit.
        175, 188, 217, 219, 221, 238, 241, 243, 244, 245, 246, 247, 248, 251, 254, 255, 256, 270, 323, 328, 349, 355,
        366, 414, 415, 420, 1203, 1253, 1260, 1329, 1330, 1528, 1765, 1796, 1929,
    ];

    /// <summary>
    /// Every team of retail PES 2010 in categories 8 (national), 0x16 (club) and 0x20 (classic),
    /// read from the running game's team table (DAT_018bd3cc). CMD_SET_CURRENTPLAYER's
    /// useable_team_id[] (FUN_0072bb20) is the whitelist (FUN_00766230) that the default-team pick
    /// (FUN_00903c10) draws from; with it empty a side has no team (FUN_00b73b20 returns -1) and
    /// the match is abandoned with CMD_SEND_ABNORMALEND right after Start Match.
    /// </summary>
    private static readonly int[] Pes2010Teams =
    [
        21, 9, 1170, 27, 24, 13, 15, 5, 18, 8, 14, 28, 22, 1, 1164, 12, 1551, 10, 2, 16, 19, 6, 26, 31, 3, 1550,
        20, 23, 7, 17, 11, 29, 30, 4, 1041, 36, 1051, 34, 1058, 1059, 1067, 32, 35, 38, 37, 1081, 33, 1099,
        42, 43, 40, 1118, 39, 50, 1128, 45, 47, 44, 51, 48, 46, 49, 1129, 57, 1002, 54, 55, 1010, 52, 1013, 1012,
        1022, 1026, 56, 53, 1029, 1031, 1032, 1184,
        101, 107, 201, 176, 202, 378, 102, 177, 178, 1589, 103, 173, 100, 206, 395, 396, 179, 105, 400, 208, 180,
        115, 1763, 409, 215, 212, 182, 213, 414, 181, 113, 112, 415, 217, 114, 218, 418, 219, 221, 1528, 234, 319,
        186, 320, 321, 188, 124, 323, 119, 120, 122, 325, 121, 327, 238, 123, 125, 240, 241, 190, 243, 116, 242,
        117, 244, 245, 349, 246, 247, 118, 248, 351, 250, 251, 252, 355, 254, 255, 357, 258, 108, 111, 259, 362,
        363, 172, 109, 260, 261, 263, 264, 265, 373, 110, 266, 267, 375, 268, 174, 269, 1197, 1203, 1205, 175,
        1832, 1207, 1733, 270, 133, 198, 2258, 1212, 1215, 2261, 1695, 191, 1974, 192, 193, 1750, 1746, 2270,
        1747, 2272, 2263, 2275, 2265, 271, 135, 1218, 1219, 131, 1221, 132, 1223, 1583, 1227, 1582, 2106, 1706,
        2257, 273, 197, 130, 1809, 2274, 1232, 1805, 134, 139, 138, 1252, 199, 200, 1533, 1534, 1535, 1536, 1537,
        1538, 1539, 1540, 1541, 1542, 1543, 1544, 1545, 1546, 1547, 1548, 1549, 1552,
        3017, 3000, 3003, 3020, 3004, 3022, 3028, 3029, 3018, 3009, 3030, 3031, 3011, 3032, 3035, 3036, 3027,
    ];

    /// <summary>
    /// The simple assembly name of the profile project that carries this title's deltas.
    /// </summary>
    public static string AssemblyName(GameProfile profile) => profile switch
    {
        GameProfile.Pes2010Pc => "OpenEleven.Profiles.Pes2010Pc",
        GameProfile.Pes2011Pc => "OpenEleven.Profiles.Pes2011Pc",
        GameProfile.Pes2012Pc => "OpenEleven.Profiles.Pes2012Pc",
        GameProfile.Pes2013Pc => "OpenEleven.Profiles.Pes2013Pc",
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown game profile."),
    };

    /// <summary>
    /// Resolves the profile assembly for a title among the assemblies already loaded,
    /// or loads it by name. Returns null when the profile project is not referenced by
    /// the host (the server then runs on the shared core alone, which serves the
    /// PES 2010 behaviour every title shares today).
    /// </summary>
    public static Assembly? TryLoadAssembly(GameProfile profile)
    {
        var name = AssemblyName(profile);

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.Ordinal));
        if (loaded is not null)
            return loaded;

        try
        {
            return Assembly.Load(new AssemblyName(name));
        }
        catch (Exception)
        {
            return null;
        }
    }
}