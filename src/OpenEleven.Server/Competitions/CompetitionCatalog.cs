// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.Competitions;

/// <summary>
/// The competitions the server runs: the configured ones, a repeating one expanded into its
/// occurrences, with the values each title's client accepts.
/// </summary>
public static class CompetitionCatalog
{
    /// <summary>How long a finished occurrence of a repeating competition stays listed (past competitions).</summary>
    public static readonly TimeSpan ArchiveWindow = TimeSpan.FromDays(60);

    /// <summary>
    /// pes2013.exe compe_kind (enum 0x0f, the same names as 0x17 for the trophy). A name outside it
    /// makes the client drop the whole competition record. RESERVED_CUP01..100 follow.
    /// </summary>
    public static readonly string[] Kinds2013 =
    [
        "UEFA_Champions_League", "Copa_Santander_Libertadores", "Copa_Santander_Libertadores_2013",
        "International_Club_Championship", "International_Cup_Online", "KONAMI_CHAMPIONSHIP", "Facebook_Cup",
        "J_League_Cup", "WEPES_CLUB_OPEN", "WEPES_NATIONAL_OPEN", "WEPES_CHAMPIONSHIP", "Novice_Cup",
        "ML_UEFA_Champions_League", "ML_Open", "ML_Open_Coach_Mode", "ML_High_Class_Cup", "ML_High_Class_Cup_Coach_Mode",
        "ML_Middle_Class_Cup", "ML_Middle_Class_Cup_Coach_Mode", "ML_Normal_Class_Cup", "ML_Normal_Class_Cup_Coach_Mode",
        "ML_Novice_Cup", "ML_Novice_Cup_Coach_Mode", "ML_Facebook_Cup",
    ];

    /// <summary>pes2013.exe entry_limit, enum 0x0e.</summary>
    public static readonly string[] EntryLimits2013 = ["SKILLLEVEL", "FACEBOOK", "TEAMPOWER", "J_LEAGUE", "J_LEAGUE_OPEN"];

    /// <summary>Skill levels in the order of entry_limit_skill_level (enum 0x61).</summary>
    public static readonly string[] SkillLevels = ["BEGINNER", "AMATEUR", "STANDARD", "PROFESSIONAL", "TOPPLAYER", "SUPERSTAR"];

    public static bool IsKind2013(string kind)
    {
        if (Kinds2013.Contains(kind))
            return true;
        if (!kind.StartsWith("RESERVED_CUP", StringComparison.Ordinal))
            return false;
        var digits = kind[12..];
        return int.TryParse(digits, out var n) && n is >= 1 and <= 100 && digits == (n < 100 ? n.ToString("00") : "100");
    }

    /// <summary>
    /// The kind as the title's client knows it. PES 2011's default KONAMI_CUP_ONLINE does not exist
    /// in PES 2013, whose normal list asks for KONAMI_CHAMPIONSHIP (FUN_005b2f60).
    /// </summary>
    public static string Kind(CompetitionOptions c, GameProfile profile)
        => profile == GameProfile.Pes2013Pc && !IsKind2013(c.Kind)
            ? c.MasterLeague ? "ML_Open" : "KONAMI_CHAMPIONSHIP"
            : c.Kind;

    /// <summary>The cup the trophy is drawn as.</summary>
    public static string Trophy(CompetitionOptions c, GameProfile profile)
        => c.Trophy.Length > 0 && (profile != GameProfile.Pes2013Pc || IsKind2013(c.Trophy)) ? c.Trophy : Kind(c, profile);

    /// <summary>The PES 2013 entry limit: a PES 2011 one (DIVISION, WEEKRANK) becomes SKILLLEVEL, a Master League one TEAMPOWER.</summary>
    public static string EntryLimit2013(CompetitionOptions c)
        => EntryLimits2013.Contains(c.EntryLimit) ? c.EntryLimit
            : c.MasterLeague && (c.MinTeamPower > 0 || c.MaxTeamPower > 0) ? "TEAMPOWER" : "SKILLLEVEL";

    /// <summary>Whether a player of <paramref name="skillLevel"/> may enter.</summary>
    public static bool SkillAllowed(CompetitionOptions c, string skillLevel)
        => c.SkillLevels.Length == 0 || c.SkillLevels.Contains(skillLevel, StringComparer.OrdinalIgnoreCase);

    /// <summary>Rounds of a 16-place bracket the qualifiers fill: 4 for 9-16, 3 for 5-8, 2 for 3-4, 1 for 2.</summary>
    public static int PlayoffRounds(CompetitionOptions c)
    {
        var size = c.PlayoffPlaces;
        return size < 2 ? 0 : (int)Math.Ceiling(Math.Log2(size));
    }

    /// <summary>The first of the four bracket rounds that has a real match (the earlier ones are all byes).</summary>
    public static int FirstPlayoffRound(CompetitionOptions c) => CompetitionBracket.Rounds - PlayoffRounds(c) + 1;

    /// <summary>When a bracket round starts; a round ends when the next one starts.</summary>
    public static DateTime RoundStart(CompetitionOptions c, int round)
        => c.PlayoffStartAt.AddMinutes((double)c.PlayoffRoundMinutes * Math.Max(0, round - FirstPlayoffRound(c)));

    public static DateTime RoundEnd(CompetitionOptions c, int round) => RoundStart(c, round + 1);

    /// <summary>When the last playoff round ends (the qualifying round's end when there is no playoff).</summary>
    public static DateTime PlayoffEnd(CompetitionOptions c)
        => PlayoffRounds(c) == 0 ? c.MatchEnd : RoundEnd(c, CompetitionBracket.Rounds);

    /// <summary>Every competition that is listed now: the configured ones and the current, next and recent occurrences of the repeating ones.</summary>
    public static IReadOnlyList<CompetitionOptions> All(ServerOptions options, DateTime now)
    {
        var list = new List<CompetitionOptions>();
        foreach (var c in Configured(options))
        {
            if (c.RepeatEveryDays <= 0)
            {
                list.Add(Normalize(c, options.GameProfile));
                continue;
            }

            var period = TimeSpan.FromDays(c.RepeatEveryDays);
            var first = Math.Max(0, (int)Math.Ceiling((now - ArchiveWindow - Normalize(c, options.GameProfile).End) / period));
            var last = (int)Math.Floor((now + period - c.EntryStart) / period);
            if (c.RepeatCount > 0)
                last = Math.Min(last, c.RepeatCount - 1);
            for (var n = first; n <= last; n++)
                list.Add(Normalize(c.Repeat(n), options.GameProfile));
        }

        return list;
    }

    /// <summary>The competition with this compe_id, an old occurrence of a repeating one included.</summary>
    public static CompetitionOptions? Find(ServerOptions options, int id)
    {
        if (id == 0)
            return null;

        var configured = Configured(options).ToList();
        if (configured.FirstOrDefault(c => c.Id == id) is { } exact)
            return Normalize(exact, options.GameProfile);

        var repeating = configured.Where(c => c.RepeatEveryDays > 0 && c.Id < id
                                              && (c.RepeatCount <= 0 || id - c.Id < c.RepeatCount))
            .OrderByDescending(c => c.Id).FirstOrDefault();
        return repeating is null ? null : Normalize(repeating.Repeat(id - repeating.Id), options.GameProfile);
    }

    private static IEnumerable<CompetitionOptions> Configured(ServerOptions options)
        => options.Competitions.Where(c => c.Enabled && c.Name.Length > 0);

    /// <summary>
    /// A copy with the title's playoff size when none is set (16 for PES 2013, none before), which
    /// ends no earlier than its playoff.
    /// </summary>
    private static CompetitionOptions Normalize(CompetitionOptions c, GameProfile profile)
    {
        var copy = c.Repeat(0);
        copy.Id = c.Id;
        copy.Occurrence = c.Occurrence;
        // The configuration binder turns "...Z" into local time; every comparison here is in UTC.
        (copy.EntryStart, copy.EntryEnd, copy.MatchStart, copy.MatchEnd, copy.End)
            = (Utc(copy.EntryStart), Utc(copy.EntryEnd), Utc(copy.MatchStart), Utc(copy.MatchEnd), Utc(copy.End));
        copy.PlayoffStart = copy.PlayoffStart is { } start ? Utc(start) : null;
        copy.PlayoffSize ??= profile == GameProfile.Pes2013Pc ? CompetitionBracket.Places : 0;
        if (copy.End < PlayoffEnd(copy))
            copy.End = PlayoffEnd(copy);
        return copy;
    }

    /// <summary>
    /// Status (enum 0x0e in 2011, 0x13 in 2013), from the configured times: STARTED while the
    /// qualifying round or the playoff is played.
    /// </summary>
    public static string Status(CompetitionOptions c, DateTime now)
        => now < c.EntryStart ? "CREATED"
            : now < c.EntryEnd && now < c.MatchStart ? "ENTRY"
            : now < c.MatchStart ? "ENTRY_CLOSED"
            : now < PlayoffEnd(c) ? "STARTED"
            : now < c.End ? "MATCH_CLOSED"
            : "FINISHED";

    /// <summary>The qualifying round is being played.</summary>
    public static bool QualifierOpen(CompetitionOptions c, DateTime now) => now >= c.MatchStart && now < c.MatchEnd;

    public static long Unix(DateTime utc) => new DateTimeOffset(Utc(utc)).ToUnixTimeSeconds();

    /// <summary>A time as UTC: a local one converted, an unspecified one taken as UTC already.</summary>
    public static DateTime Utc(DateTime t)
        => t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
