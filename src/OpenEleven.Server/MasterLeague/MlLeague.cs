// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.MasterLeague;

/// <summary>
/// The Master League Online state shared by the socket handlers and the HTTP urls: clubs, squads,
/// market prices and their history. One per DI scope (it wraps the scoped <see cref="GameDbContext"/>).
/// </summary>
public sealed class MlLeague(GameDbContext db, MlCatalog catalog, IOptionsMonitor<ServerOptions> options, TimeProvider time)
{
    public GameDbContext Db => db;
    public MlCatalog Catalog => catalog;
    public MlRules Rules { get; } = new(options.CurrentValue.MasterLeague);

    public long Now => time.GetUtcNow().ToUnixTimeSeconds();

    /// <summary>UTC midnight of today, as Unix seconds.</summary>
    public long Today => Now / 86400 * 86400;

    /// <summary>Monday 00:00 UTC of the current week, as Unix seconds.</summary>
    public long WeekStart
    {
        get
        {
            var day = time.GetUtcNow().UtcDateTime.Date;
            var monday = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
            return new DateTimeOffset(monday, TimeSpan.Zero).ToUnixTimeSeconds();
        }
    }

    public Task<MlTeam?> TeamAsync(int pid, CancellationToken ct)
        => db.MlTeams.FirstOrDefaultAsync(t => t.Pid == pid, ct);

    public Task<List<MlTeamPlayer>> SquadAsync(int pid, CancellationToken ct)
        => db.MlTeamPlayers.Where(p => p.Pid == pid).OrderBy(p => p.UniformNumber).ToListAsync(ct);

    /// <summary>How many clubs own each footballer (owner_num).</summary>
    public async Task<Dictionary<int, int>> OwnersAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        var counts = await db.MlTeamPlayers.Where(p => wanted.Contains(p.GamePlayerId))
            .GroupBy(p => p.GamePlayerId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return wanted.ToDictionary(id => id, id => counts.FirstOrDefault(c => c.Key == id)?.Count ?? 0);
    }

    public async Task<int> OwnersAsync(int id, CancellationToken ct)
        => await db.MlTeamPlayers.CountAsync(p => p.GamePlayerId == id, ct);

    public long Price(int gamePlayerId, int owners) => Rules.MarketPrice(catalog.Player(gamePlayerId).Overall, owners);

    public int TeamPower(IEnumerable<MlTeamPlayer> squad) => MlRules.TeamPower(squad.Select(p => catalog.Player(p.GamePlayerId).Overall));

    /// <summary>Starts a new purchase period when the old one has run out.</summary>
    public void RefreshBuyPeriod(MlTeam team)
    {
        var length = Math.Max(1, Rules.Options.BuyPeriodDays) * 86400L;
        if (Now - team.BuyPeriodStart < length)
            return;
        team.BuyPeriodStart = Now / length * length;
        team.BuysLeft = Rules.Options.BuysPerPeriod;
    }

    /// <summary>A new club with the starting squad (CMD_CREATE_ML_MYTEAM, CMD_RESET_ML_MYTEAM).</summary>
    public async Task<MlTeam> CreateAsync(int pid, string name, string shortName, int baseTeamId, CancellationToken ct)
    {
        await RemoveAsync(pid, ct);
        var team = new MlTeam
        {
            Pid = pid,
            Name = name,
            ShortName = shortName,
            BaseTeamId = baseTeamId,
            Funds = Rules.Options.StartingFunds,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        RefreshBuyPeriod(team);
        db.MlTeams.Add(team);

        var used = new HashSet<int>();
        foreach (var member in catalog.StartingSquad(Rules.Options.StartingTeamId))
        {
            var number = member.Number is > 0 and < 100 && used.Add(member.Number) ? member.Number : FreeNumber(used);
            db.MlTeamPlayers.Add(new MlTeamPlayer
            {
                Pid = pid,
                GamePlayerId = member.Id,
                UniformNumber = number,
                ContractTerm = Rules.Options.NewContractMatches * 3,
                JoinedAt = Now,
            });
        }

        await db.SaveChangesAsync(ct);
        var squad = await SquadAsync(pid, ct);
        team.PreviousSquadValue = await SquadValueAsync(squad, ct);
        await db.SaveChangesAsync(ct);
        return team;
    }

    public async Task RemoveAsync(int pid, CancellationToken ct)
    {
        await db.MlTeamPlayers.Where(p => p.Pid == pid).ExecuteDeleteAsync(ct);
        await db.MlChecklist.Where(p => p.Pid == pid).ExecuteDeleteAsync(ct);
        await db.MlTeams.Where(t => t.Pid == pid).ExecuteDeleteAsync(ct);
        db.ChangeTracker.Clear();
    }

    public static int FreeNumber(ISet<int> used)
    {
        for (var n = 1; n < 100; n++)
            if (used.Add(n))
                return n;
        return 99;
    }

    public async Task<long> SquadValueAsync(IReadOnlyCollection<MlTeamPlayer> squad, CancellationToken ct)
    {
        var owners = await OwnersAsync(squad.Select(p => p.GamePlayerId), ct);
        return squad.Sum(p => Price(p.GamePlayerId, owners[p.GamePlayerId]));
    }

    /// <summary>Writes today's price point of the footballers whose demand changed.</summary>
    public async Task RecordPricesAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var owners = await OwnersAsync(ids, ct);
        var today = Today;
        foreach (var (id, count) in owners)
        {
            var point = await db.MlPrices.FirstOrDefaultAsync(p => p.GamePlayerId == id && p.Day == today, ct);
            if (point is null)
                db.MlPrices.Add(point = new MlPricePoint { GamePlayerId = id, Day = today });
            point.Price = Price(id, count);
            point.Owners = count;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Price at the start of this week and the last, and the extremes of the history.</summary>
    public async Task<PriceStats> StatsAsync(int id, int owners, CancellationToken ct)
    {
        var now = Price(id, owners);
        var history = await db.MlPrices.Where(p => p.GamePlayerId == id).OrderBy(p => p.Day).ToListAsync(ct);
        long At(long when) => history.LastOrDefault(p => p.Day < when)?.Price ?? Rules.BasePrice(catalog.Player(id).Overall);
        var all = history.Select(p => p.Price).Append(now).ToList();
        return new PriceStats(now, At(WeekStart), At(WeekStart - 7 * 86400), all.Max(), all.Min());
    }

    public void Event(int pid, string type, int gamePlayerId = 0, int compeId = 0, string text = "")
        => db.MlEvents.Add(new MlEvent { Pid = pid, Type = type, GamePlayerId = gamePlayerId, CompeId = compeId, Text = text, At = Now });

    /// <summary>The <c>myteam</c> record of CMD_GET_ML_MYTEAM (FUN_005ca950: every key optional).</summary>
    public async Task<KvMessage> TeamRecordAsync(MlTeam team, CancellationToken ct)
    {
        RefreshBuyPeriod(team);
        var squad = await SquadAsync(team.Pid, ct);
        return new KvMessage()
            .Set("team_funds", team.Funds)
            .Set("team_name", team.Name)
            .Set("team_name_short", team.ShortName)
            .Set("ml_skill_level", MlRules.SkillLevel(team.Division))
            .Set("base_team_id", team.BaseTeamId)
            .Set("enable_buy_num", team.BuysLeft)
            .Set("point", team.Points)
            .Set("team_power", TeamPower(squad))
            .Set("total_price_old", team.PreviousSquadValue)
            .Set("team_resource", await SquadValueAsync(squad, ct))
            .Set("team_status", "CREATED");
    }

    /// <summary>The injury subrecord the squad lists carry.</summary>
    public static KvMessage Injury(MlTeamPlayer p) => new KvMessage()
        .Set("part", p.InjuryMatches > 0 ? 1 : 0)
        .Set("symptom", p.InjuryMatches > 0 ? 1 : 0)
        .Set("level", p.InjuryMatches > 0 ? 1 : 0)
        .Set("level_no_self_conscious", 0)
        .Set("mcount_left", p.InjuryMatches);
}

public readonly record struct PriceStats(long Now, long ThisWeek, long LastWeek, long Max, long Min);
