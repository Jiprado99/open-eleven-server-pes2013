// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Competitions;

/// <summary>One entrant's line in a competition: who, the team picked, and the totals of the matches played.</summary>
public sealed record CompetitionLine(Player Player, Tally Tally, long UpdatedAt)
{
    public int Points => Tally.Wins * 3 + Tally.Draws;
    public int Difference => Tally.GoalsFor - Tally.GoalsAgainst;
    public int TeamId => Player.CompetitionTeamId;

    /// <summary>The client insists on a non-empty team_name, so a dash stands in for no team.</summary>
    public string TeamName => Player.CompetitionTeamName.Length > 0 ? Player.CompetitionTeamName : "-";

    /// <summary>The standings order: points, goal difference, goals scored.</summary>
    public (int, int, int) Key => (Points, Difference, Tally.GoalsFor);
}

/// <summary>The qualifying round table and the matches of a competition, worked out from the match history.</summary>
public static class CompetitionTable
{
    /// <summary>
    /// The competition's finished matches, oldest first (a void one, nobody's fault, is left out by
    /// <see cref="CommunityStats.Of"/>). <paramref name="qualifierOnly"/> leaves the playoff out.
    /// </summary>
    public static async Task<List<MatchRecord>> MatchesAsync(GameDbContext db, int compeId, bool qualifierOnly, CancellationToken ct)
    {
        var q = db.Matches.AsNoTracking().Where(m => m.CompeId == compeId && m.Completed);
        if (qualifierOnly)
            q = q.Where(m => m.PlayoffMatchId == 0);
        return await q.OrderBy(m => m.PlayedAt).ToListAsync(ct);
    }

    /// <summary>
    /// Everyone who entered or played the competition, with their totals from
    /// <paramref name="matches"/>, best first (points, goal difference, goals scored, then pid).
    /// </summary>
    public static async Task<List<CompetitionLine>> LinesAsync(GameDbContext db, int compeId, List<MatchRecord> matches, CancellationToken ct)
    {
        var pids = matches.SelectMany(m => new[] { m.HomePid, m.AwayPid }).ToHashSet();
        pids.UnionWith(await db.Players.AsNoTracking().Where(p => p.CompetitionId == compeId).Select(p => p.Pid).ToListAsync(ct));
        pids.UnionWith(await db.CompetitionEntries.AsNoTracking().Where(e => e.CompeId == compeId).Select(e => e.Pid).ToListAsync(ct));
        var found = await db.Players.AsNoTracking().Where(p => pids.Contains(p.Pid)).ToListAsync(ct);

        return found.Select(p => new CompetitionLine(p,
                CommunityStats.Of(matches, m => m.HomePid == p.Pid ? true : m.AwayPid == p.Pid ? false : null),
                matches.Where(m => m.HomePid == p.Pid || m.AwayPid == p.Pid).Select(m => RankSystem.Unix(m.PlayedAt)).DefaultIfEmpty(0).Max()))
            .OrderByDescending(l => l.Points).ThenByDescending(l => l.Difference).ThenByDescending(l => l.Tally.GoalsFor).ThenBy(l => l.Player.Pid)
            .ToList();
    }

    /// <summary>The qualifying round table.</summary>
    public static async Task<List<CompetitionLine>> StandingsAsync(GameDbContext db, int compeId, CancellationToken ct)
        => await LinesAsync(db, compeId, await MatchesAsync(db, compeId, qualifierOnly: true, ct), ct);

    /// <summary>1 plus the entrants strictly ahead: ties share a place.</summary>
    public static int RankOf(IReadOnlyList<CompetitionLine> table, CompetitionLine line)
        => 1 + table.Count(o => o.Key.CompareTo(line.Key) > 0);

    /// <summary>Qualifying matches a player has played in the competition.</summary>
    public static Task<int> QualifierMatchesAsync(GameDbContext db, int compeId, int pid, CancellationToken ct)
        => db.Matches.CountAsync(m => m.CompeId == compeId && m.Completed && m.PlayoffMatchId == 0 && !(m.Disconnected && m.DisconnectedPid == null)
                                      && (m.HomePid == pid || m.AwayPid == pid), ct);
}
