// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using OpenEleven.Protocol.Kv;

namespace OpenEleven.Server.MasterLeague;

/// <summary>
/// Rival Rank (ML_RIVAL_RANK url): a weekly table of the Master League matches (ML_RANK) played
/// by the clubs of one division. The top three of last week's table collect a bonus once
/// (CMD_GET_ML_RIVAL_RANK_BONUS); the winner gets a RIVAL_RANK_CHAMP event.
/// <list type="bullet">
/// <item>_STATUS: is_first_login, is_rank_entry, has_rank_last, status = ENTRY | PENALTY | INACTIVE | BEGINNER | LESS_RIVAL.</item>
/// <item>RANK / _LAST: rival_player_num, price_list (the bonuses), limit_time (end of the week),
/// rank_list[pid, rank, match_record{win_num, draw_num, lose_num}, name, nick_name_id].</item>
/// <item>_BONUS: income, post_team_funds; ERR_NO_WIN_POINT when there is nothing to collect.</item>
/// </list>
/// ponytail: the original rules are unknown. BEGINNER = fewer than 5 ML matches ever, LESS_RIVAL =
/// fewer than 2 clubs in the division, INACTIVE = no ML match this week, PENALTY is never used;
/// a week's division is the club's current one.
/// </summary>
public static class MlRivalRank
{
    public const int BeginnerMatches = 5;
    private const int Week = 7 * 86400;

    private sealed record Line(int Pid, string Name, int Nickname, int Wins, int Draws, int Losses)
    {
        public int Points => Wins * 3 + Draws;
        public int Played => Wins + Draws + Losses;
    }

    /// <summary>The week's ML_RANK table of one division (clubs with at least one match).</summary>
    private static async Task<List<Line>> TableAsync(MlLeague league, int division, long weekStart, CancellationToken ct)
    {
        var from = DateTimeOffset.FromUnixTimeSeconds(weekStart).UtcDateTime;
        var to = from.AddDays(7);
        var members = await league.Db.MlTeams.AsNoTracking().Where(t => t.Division == division).Select(t => t.Pid).ToListAsync(ct);
        var matches = await league.Db.Matches.AsNoTracking()
            .Where(m => m.Completed && !m.Disconnected && m.PlayedAt >= from && m.PlayedAt < to && m.Settings.Contains("\"ML_RANK\"")
                        && (members.Contains(m.HomePid) || members.Contains(m.AwayPid)))
            .ToListAsync(ct);
        var players = await league.Db.Players.AsNoTracking().Where(p => members.Contains(p.Pid))
            .ToDictionaryAsync(p => p.Pid, p => (p.Name, p.NicknameId), ct);

        var tally = new Dictionary<int, (int W, int D, int L)>();
        void Add(int pid, int scored, int conceded)
        {
            if (!members.Contains(pid))
                return;
            var t = tally.GetValueOrDefault(pid);
            tally[pid] = scored > conceded ? (t.W + 1, t.D, t.L) : scored == conceded ? (t.W, t.D + 1, t.L) : (t.W, t.D, t.L + 1);
        }

        foreach (var m in matches)
        {
            Add(m.HomePid, m.HomeScore, m.AwayScore);
            Add(m.AwayPid, m.AwayScore, m.HomeScore);
        }

        return tally.Select(t => new Line(t.Key, players.GetValueOrDefault(t.Key).Name ?? "", players.GetValueOrDefault(t.Key).NicknameId,
                t.Value.W, t.Value.D, t.Value.L))
            .OrderByDescending(l => l.Points).ThenByDescending(l => l.Wins).ThenBy(l => l.Played).ThenBy(l => l.Pid).ToList();
    }

    public static async Task<KvMessage> StatusAsync(MlLeague league, int pid, CancellationToken ct)
    {
        var team = await league.TeamAsync(pid, ct);
        if (team is null)
            return MlHttp.Fail("ERR_NOTFOUND");
        var now = await TableAsync(league, team.Division, league.WeekStart, ct);
        var last = await TableAsync(league, team.Division, league.WeekStart - Week, ct);
        var rivals = await league.Db.MlTeams.CountAsync(t => t.Division == team.Division, ct);
        var entered = now.Any(l => l.Pid == pid);

        var status = team.Wins + team.Draws + team.Losses < BeginnerMatches ? "BEGINNER"
            : rivals < 2 ? "LESS_RIVAL"
            : entered ? "ENTRY"
            : "INACTIVE";
        return MlHttp.Ok()
            .Set("is_first_login", entered || last.Any(l => l.Pid == pid) ? "FALSE" : "TRUE")
            .Set("is_rank_entry", entered ? "TRUE" : "FALSE")
            .Set("has_rank_last", last.Any(l => l.Pid == pid) ? "TRUE" : "FALSE")
            .Set("status", status);
    }

    public static async Task<KvMessage> RankAsync(MlLeague league, int pid, bool lastWeek, CancellationToken ct)
    {
        var team = await league.TeamAsync(pid, ct);
        if (team is null)
            return MlHttp.Fail("ERR_NOTFOUND");
        var weekStart = lastWeek ? league.WeekStart - Week : league.WeekStart;
        var table = await TableAsync(league, team.Division, weekStart, ct);
        var list = table.Select((l, i) => new KvMessage()
            .Set("pid", l.Pid)
            .Set("rank", i + 1)
            .Set("match_record", new KvMessage().Set("win_num", l.Wins).Set("draw_num", l.Draws).Set("lose_num", l.Losses))
            .Set("name", l.Name)
            .Set("nick_name_id", l.Nickname)).ToList();

        // The key table has no separate count for rank_list: rival_player_num is it.
        return MlHttp.Ok()
            .Set("price_list", new KvArray(league.Rules.Options.RivalRankBonusOrDefault().Select(b => (object?)b).ToArray()))
            .Set("limit_time", weekStart + Week)
            .SetList("rival_player_num", "rank_list", list);
    }

    public static async Task<KvMessage> BonusAsync(MlLeague league, int pid, CancellationToken ct)
    {
        var team = await league.TeamAsync(pid, ct);
        if (team is null)
            return MlHttp.Fail("ERR_NOTFOUND");
        var lastWeek = league.WeekStart - Week;
        if (team.RivalBonusWeek >= lastWeek)
            return MlHttp.Fail("ERR_NO_WIN_POINT");

        var place = (await TableAsync(league, team.Division, lastWeek, ct)).FindIndex(l => l.Pid == pid);
        var bonuses = league.Rules.Options.RivalRankBonusOrDefault();
        if (place < 0 || place >= bonuses.Length)
            return MlHttp.Fail("ERR_NO_WIN_POINT");

        team.RivalBonusWeek = lastWeek;
        team.Funds += bonuses[place];
        if (place == 0)
            league.Event(pid, "RIVAL_RANK_CHAMP");
        await league.Db.SaveChangesAsync(ct);
        return MlHttp.Ok().Set("income", bonuses[place]).Set("post_team_funds", team.Funds);
    }
}
