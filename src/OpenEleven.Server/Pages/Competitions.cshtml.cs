// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Server.Competitions;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.Pages;

/// <summary>
/// The online competitions: every listed one with its stage, and for one of them the qualifying
/// table, the playoff bracket and the champion.
/// </summary>
[OutputCache(Duration = 30, VaryByQueryKeys = ["id"])]
public sealed class CompetitionsModel(GameDbContext db, IOptionsMonitor<ServerOptions> options, TimeProvider time) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "id")] public int Id { get; set; }

    public sealed record Row(CompetitionOptions Cup, string Stage, int Entrants, string Champion);

    public sealed record Side(int Pid, string Name, string Team, int Goals, bool Won);

    public sealed record Game(int Round, int Slot, Side Home, Side Away, bool Finished, bool Penalties, string EndKind);

    public IReadOnlyList<Row> List { get; private set; } = [];
    public CompetitionOptions? Cup { get; private set; }
    public string Stage { get; private set; } = "";
    public IReadOnlyList<CompetitionLine> Table { get; private set; } = [];
    public IReadOnlySet<int> Qualifiers { get; private set; } = new HashSet<int>();
    public IReadOnlyList<Game> Bracket { get; private set; } = [];
    public string Champion { get; private set; } = "";
    public DateTime Now { get; private set; }

    /// <summary>The stage shown for a competition at <paramref name="now"/>.</summary>
    public static string StageOf(CompetitionOptions c, CompetitionProgress? p, DateTime now)
        => p is { Finished: true } || now >= c.End ? "Finished"
            : now < c.EntryStart ? "Scheduled"
            : now < c.MatchStart ? "Entry open"
            : now < c.MatchEnd ? "Qualifying round"
            : CompetitionCatalog.PlayoffRounds(c) > 0 ? (p?.Round is > 0 and var r ? RoundName(r) : "Playoff")
            : "Finished";

    public static string RoundName(int round) => round switch
    {
        1 => "Round of 16",
        2 => "Quarter-finals",
        3 => "Semi-finals",
        _ => "Final",
    };

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Now = time.GetUtcNow().UtcDateTime;
        var o = options.CurrentValue;
        var all = CompetitionCatalog.All(o, Now).OrderByDescending(c => c.EntryStart).ToList();
        var ids = all.Select(c => c.Id).ToList();
        var progress = await db.CompetitionProgress.AsNoTracking().Where(p => ids.Contains(p.CompeId)).ToDictionaryAsync(p => p.CompeId, ct);
        var champions = progress.Values.Select(p => p.ChampionPid).Where(p => p != 0).ToList();
        var names = await db.Players.AsNoTracking().Where(p => champions.Contains(p.Pid)).ToDictionaryAsync(p => p.Pid, p => p.Name, ct);
        var counts = await db.CompetitionEntries.AsNoTracking().Where(e => ids.Contains(e.CompeId))
            .GroupBy(e => e.CompeId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        List = all.Select(c => new Row(c, StageOf(c, progress.GetValueOrDefault(c.Id), Now), counts.GetValueOrDefault(c.Id),
            progress.GetValueOrDefault(c.Id) is { ChampionPid: > 0 } p ? names.GetValueOrDefault(p.ChampionPid, "") : "")).ToList();

        if (Id == 0)
            return Page();
        Cup = CompetitionCatalog.Find(o, Id);
        if (Cup is null)
            return NotFound();

        var mine = progress.GetValueOrDefault(Id) ?? await db.CompetitionProgress.AsNoTracking().FirstOrDefaultAsync(p => p.CompeId == Id, ct);
        Stage = StageOf(Cup, mine, Now);
        Table = await CompetitionTable.StandingsAsync(db, Id, ct);
        Qualifiers = (mine?.Qualifiers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();

        var bracket = await CompetitionBracket.LoadAsync(db, Id, ct);
        var pids = bracket.SelectMany(m => new[] { m.HomePid, m.AwayPid }).Append(mine?.ChampionPid ?? 0).Where(p => p != 0).Distinct().ToList();
        var players = await db.Players.AsNoTracking().Where(p => pids.Contains(p.Pid)).ToDictionaryAsync(p => p.Pid, ct);
        string Name(int pid) => pid == 0 ? "" : players.GetValueOrDefault(pid)?.Name ?? $"#{pid}";
        string Team(int pid) => players.GetValueOrDefault(pid) is { } p && p.CompetitionId == Id ? p.CompetitionTeamName : "";

        Bracket = bracket.Where(m => m.Round >= CompetitionCatalog.FirstPlayoffRound(Cup))
            .Select(m => new Game(m.Round, m.Slot,
                new Side(m.HomePid, Name(m.HomePid), Team(m.HomePid), m.HomeScore, m.Finished && m.WinPid == m.HomePid && m.HomePid != 0),
                new Side(m.AwayPid, Name(m.AwayPid), Team(m.AwayPid), m.AwayScore, m.Finished && m.WinPid == m.AwayPid && m.AwayPid != 0),
                m.Finished, m.Penalties, m.EndKind)).ToList();
        Champion = mine is { ChampionPid: > 0 } ? Name(mine.ChampionPid) : "";
        return Page();
    }
}
