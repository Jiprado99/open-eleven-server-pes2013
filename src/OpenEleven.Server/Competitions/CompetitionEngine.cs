// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Handlers;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Competitions;

/// <summary>
/// Moves competitions along their times. Run every few seconds by <see cref="CompetitionScheduler"/>:
/// <list type="number">
/// <item>The qualifying round closes at MatchEnd: the best of its table (with the matches asked
/// for) are drawn into the bracket by place, everyone is told whether they went through.</item>
/// <item>Each playoff round starts at its time: its players are told (MATCH_START) and get the
/// time the server settles it by. Who has not turned up after ShowUpMinutes loses to whoever did;
/// a match still undecided when the round ends goes to the higher seed.</item>
/// <item>A decided match is told to both players; the loser is out.</item>
/// <item>The final decides the champion; a competition without a playoff ends with the table's leader.</item>
/// </list>
/// </summary>
public static class CompetitionEngine
{
    public static async Task TickAsync(GameDbContext db, Hub hub, ServerOptions options, DateTime now, ILogger log, CancellationToken ct = default)
    {
        foreach (var c in CompetitionCatalog.All(options, now).Where(c => now >= c.MatchEnd))
        {
            var progress = await db.CompetitionProgress.FirstOrDefaultAsync(p => p.CompeId == c.Id, ct);
            if (progress is { Finished: true })
                continue;
            if (progress is null)
            {
                progress = new CompetitionProgress { CompeId = c.Id };
                db.CompetitionProgress.Add(progress);
            }

            (progress.Name, progress.Trophy, progress.MasterLeague) = (c.Name, CompetitionCatalog.Trophy(c, options.GameProfile), c.MasterLeague);
            if (!progress.QualifierClosed)
            {
                await CloseQualifierAsync(db, hub, options, c, progress, now, log, ct);
                // The playoff step reads the bracket back from the database.
                await db.SaveChangesAsync(ct);
            }
            if (progress.QualifierClosed && !progress.Finished)
                await RunPlayoffAsync(db, hub, options, c, progress, now, log, ct);
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task CloseQualifierAsync(GameDbContext db, Hub hub, ServerOptions options, CompetitionOptions c,
        CompetitionProgress progress, DateTime now, ILogger log, CancellationToken ct)
    {
        var table = await CompetitionTable.StandingsAsync(db, c.Id, ct);
        var eligible = table.Where(l => l.Tally.Matches >= Math.Max(1, c.MinQualifierMatches)).ToList();
        var size = c.PlayoffPlaces;
        var qualifiers = size >= 2 && eligible.Count >= 2 ? eligible.Take(size).ToList() : [];

        progress.QualifierClosed = true;
        progress.Qualifiers = string.Join(',', qualifiers.Select(l => l.Player.Pid));
        log.LogInformation("Competition {CompeId} qualifying round closed: {Qualified} of {Entrants} go through",
            c.Id, qualifiers.Count, table.Count);

        if (qualifiers.Count == 0)
        {
            // No playoff: the qualifying table is the final one.
            foreach (var l in table)
                CompetitionWatches.End(hub, l.Player.Pid, "END_PLAYOFF");
            await FinishAsync(db, options, c, progress, eligible.FirstOrDefault()?.Player.Pid ?? 0, now, log, ct);
            return;
        }

        CompetitionBracket.Draw(db, c.Id, qualifiers.Select(l => l.Player).ToList());
        var through = qualifiers.Select(l => l.Player.Pid).ToHashSet();
        var firstRound = CompetitionCatalog.RoundStart(c, CompetitionCatalog.FirstPlayoffRound(c));
        foreach (var l in table)
        {
            var pid = l.Player.Pid;
            CompetitionWatches.End(hub, pid, through.Contains(pid) ? "FINAL_ADVANCEMENT" : "SECOND_ROUND_EXIT");
            if (through.Contains(pid))
                CompetitionWatches.MatchStartTime(hub, pid, now < firstRound ? firstRound : null);
        }

        if (options.GameProfile == GameProfile.Pes2013Pc)
            foreach (var l in qualifiers)
                Mail(db, l.Player, "CUP_WR_PREPASS", "cup.prepass", c.Name, CompetitionTable.RankOf(table, l));
    }

    private static async Task RunPlayoffAsync(GameDbContext db, Hub hub, ServerOptions options, CompetitionOptions c,
        CompetitionProgress progress, DateTime now, ILogger log, CancellationToken ct)
    {
        var bracket = await CompetitionBracket.LoadAsync(db, c.Id, ct);
        var unix = RankSystem.Unix(now);

        // A round may close and the next open in the same tick (the server was down, or the times are past).
        for (var guard = 0; guard <= CompetitionBracket.Rounds; guard++)
        {
            var round = CompetitionBracket.CurrentRound(bracket);
            if (round == 0)
                break;

            var start = CompetitionCatalog.RoundStart(c, round);
            if (now < start)
                break;

            var playing = bracket.Where(m => m.Round == round && !m.Finished).ToList();
            if (progress.Round < round)
            {
                progress.Round = round;
                foreach (var pid in playing.SelectMany(m => new[] { m.HomePid, m.AwayPid }).Where(p => p != 0))
                {
                    CompetitionWatches.MatchStartTime(hub, pid, null);
                    CompetitionWatches.MatchStart(hub, pid);
                    CompetitionWatches.ServerMatchEnd(hub, pid, CompetitionCatalog.RoundEnd(c, round));
                }

                log.LogInformation("Competition {CompeId} playoff round {Round} started ({Matches} matches)", c.Id, round, playing.Count);
            }

            if (now >= start.AddMinutes(c.ShowUpMinutes))
                foreach (var m in playing.Where(m => !m.Finished))
                {
                    var (home, away) = (m.HomeShowedAt != 0, m.AwayShowedAt != 0);
                    if (home && away)
                        continue;

                    // Who turned up goes through; nobody turning up puts both out.
                    var winner = home ? m.HomePid : away ? m.AwayPid : 0;
                    m.DisqualifiedPid = home ? m.AwayPid : away ? m.HomePid : 0;
                    CompetitionBracket.Finish(bracket, m, winner, "TIMEOUT");
                    log.LogInformation("Competition {CompeId} playoff match {Round}/{Slot}: no-show, pid {Winner} goes through", c.Id, m.Round, m.Slot, winner);
                }

            if (now >= CompetitionCatalog.RoundEnd(c, round))
                foreach (var m in playing.Where(m => !m.Finished))
                {
                    // Both turned up but no result came: the higher seed goes through.
                    CompetitionBracket.Finish(bracket, m, m.HomePid, "TIMEOUT");
                    log.LogInformation("Competition {CompeId} playoff match {Round}/{Slot} undecided at the end of the round: pid {Winner} goes through", c.Id, m.Round, m.Slot, m.HomePid);
                }

            CompetitionBracket.SettleByes(bracket);
            if (CompetitionBracket.CurrentRound(bracket) == round)
                break;
        }

        // Tell both players of every match decided since the last tick; the loser is out.
        foreach (var m in bracket.Where(m => m.Finished && !m.Notified))
        {
            foreach (var pid in new[] { m.HomePid, m.AwayPid }.Where(p => p != 0))
            {
                CompetitionWatches.EndMatch(hub, pid, m);
                if (pid != m.WinPid)
                    CompetitionWatches.End(hub, pid, "END_PLAYOFF", m.DisqualifiedPid == pid);
            }

            m.Notified = true;
        }

        if (CompetitionBracket.CurrentRound(bracket) == 0)
        {
            var champion = CompetitionBracket.Champion(bracket);
            if (champion != 0)
                CompetitionWatches.End(hub, champion, "END_PLAYOFF");
            await FinishAsync(db, options, c, progress, champion, now, log, ct, bracket);
        }
    }

    private static async Task FinishAsync(GameDbContext db, ServerOptions options, CompetitionOptions c, CompetitionProgress progress,
        int championPid, DateTime now, ILogger log, CancellationToken ct, IReadOnlyList<CompetitionPlayoffMatch>? bracket = null)
    {
        progress.Finished = true;
        progress.FinishedAt = RankSystem.Unix(now);
        progress.ChampionPid = championPid;
        var champion = championPid == 0 ? null : await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Pid == championPid, ct);
        if (champion is not null)
        {
            // The team the champion played the competition with; still on the player unless they entered another since.
            var final = bracket is null ? null : CompetitionBracket.At(bracket, CompetitionBracket.Rounds, 0);
            progress.ChampionTeamId = final is null ? champion.CompetitionTeamId
                : final.WinPid == final.HomePid ? final.HomeTeamId : final.AwayTeamId;
            progress.ChampionTeamName = champion.CompetitionId == c.Id ? champion.CompetitionTeamName : "";
        }

        log.LogInformation("Competition {CompeId} finished: champion pid {Champion}", c.Id, championPid);
        if (options.GameProfile != GameProfile.Pes2013Pc || progress.Mailed)
            return;
        progress.Mailed = true;

        var all = await CompetitionTable.LinesAsync(db, c.Id, await CompetitionTable.MatchesAsync(db, c.Id, qualifierOnly: false, ct), ct);
        var topScorer = all.Where(l => l.Tally.GoalsFor > 0).MaxBy(l => l.Tally.GoalsFor);
        var topAssist = all.Where(l => l.Tally.Assists > 0).MaxBy(l => l.Tally.Assists);
        foreach (var l in all)
        {
            var result = bracket is null ? CompetitionResult.None : CompetitionBracket.ResultOf(bracket, l.Player.Pid);
            if (l.Player.Pid == championPid)
                Mail(db, l.Player, "CUP_WR_FINALWIN", "cup.win", c.Name);
            else if (result.PrizeIndex >= 0)
                Mail(db, l.Player, "CUP_WR_FINALPRIZE", "cup.result", c.Name, ResultText(result.Result, l.Player.Lang));
            else
                Mail(db, l.Player, "CUP_NORMALEND", "cup.end", c.Name);

            if (l == topScorer)
                Mail(db, l.Player, "CUP_WR_GOAL", "cup.goal", c.Name, l.Tally.GoalsFor);
            if (l == topAssist)
                Mail(db, l.Player, "CUP_WR_ASSIST", "cup.assist", c.Name, l.Tally.Assists);
        }
    }

    private static void Mail(GameDbContext db, Player to, string type, string key, params object[] args)
        => MailHandlers.AddSystemMail(db, to.Pid, type,
            ServerText.Get(key + ".subject", to.Lang, args), ServerText.Get(key + ".body", to.Lang, args));

    private static string ResultText(string result, string? lang) => ServerText.Get("cup.place." + result, lang);
}
