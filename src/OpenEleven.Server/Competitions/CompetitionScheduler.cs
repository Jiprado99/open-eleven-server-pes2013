// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Competitions;

/// <summary>
/// Runs <see cref="CompetitionEngine"/> every few seconds, so qualifying rounds close, playoff
/// rounds start and results reach the players on time. It warns once about competitions the
/// title's client would drop.
/// </summary>
public sealed class CompetitionScheduler(
    IServiceScopeFactory scopes,
    IOptionsMonitor<ServerOptions> options,
    Hub hub,
    TimeProvider time,
    ILogger<CompetitionScheduler> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WarnAboutConfiguration(options.CurrentValue, log);

        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            var o = options.CurrentValue;
            if (o.Competitions.Count == 0)
                continue;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
                await CompetitionEngine.TickAsync(db, hub, o, time.GetUtcNow().UtcDateTime, log, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Competition schedule step failed; retrying");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Logs the configured values a PES 2013 client does not know (they are replaced when sent).</summary>
    public static void WarnAboutConfiguration(ServerOptions o, ILogger log)
    {
        if (o.GameProfile != GameProfile.Pes2013Pc)
            return;

        foreach (var c in o.Competitions.Where(c => c.Enabled))
        {
            if (!CompetitionCatalog.IsKind2013(c.Kind))
                log.LogWarning("Competition {Id}: Kind {Kind} is not a PES 2013 compe_kind; it is sent as {Sent}",
                    c.Id, c.Kind, CompetitionCatalog.Kind(c, o.GameProfile));
            if (!CompetitionCatalog.EntryLimits2013.Contains(c.EntryLimit))
                log.LogWarning("Competition {Id}: EntryLimit {Limit} is not a PES 2013 value; it is sent as {Sent}",
                    c.Id, c.EntryLimit, CompetitionCatalog.EntryLimit2013(c));
            if (c.PlayoffSize is < 0 or 1 or > CompetitionBracket.Places)
                log.LogWarning("Competition {Id}: PlayoffSize {Size} is outside 0 and 2..16; it is clamped", c.Id, c.PlayoffSize);
        }

        // A repeating competition's n-th run is compe_id Id + n; without an end, the next 1000 are its.
        foreach (var r in o.Competitions.Where(c => c.Enabled && c.RepeatEveryDays > 0))
        {
            var last = r.Id + (r.RepeatCount > 0 ? r.RepeatCount - 1 : 1000);
            foreach (var other in o.Competitions.Where(c => c.Enabled && c != r && c.Id > r.Id && c.Id <= last))
                log.LogWarning("Competition {Id} repeats as ids {Id}..{Last}; competition {Other} uses one of them", r.Id, r.Id, last, other.Id);
        }
    }
}
