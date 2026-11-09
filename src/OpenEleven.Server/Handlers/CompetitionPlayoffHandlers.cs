// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Competitions;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// The competition playoff and what follows it (pes2013.exe): the screens' watches, the bracket,
/// the playoff match, its results, the champion's commendation and the champions' list.
/// </summary>
/// <remarks>
/// The playoff screen (FUN_00679c40) arms CMD_WATCH_COMPLG_MATCH_START, _MATCH_START_TIME and
/// CMD_WATCH_SERVERMATCHEND, asks for the bracket (CMD_GET_COMPLG_TOURNAMENT) and then for the
/// round's match table (CMD_GET_COMPLG_PLAYOFF_MATCH_NTL), finds its own pid in it and plays the
/// other side over P2P. The server puts both in one room as it does for a quick match, so the
/// shared room tail reports the result (MatchResults → CompetitionBracket); the scheduler then
/// answers CMD_WATCH_COMPLG_END_MATCH.
/// ponytail: the flow is read from the client; no real playoff has been played on this server yet.
/// </remarks>
public sealed class CompetitionPlayoffHandlers(
    GameDbContext db,
    IPlayerRepository players,
    IOptionsMonitor<ServerOptions> options,
    TimeProvider time)
{
    private const ServiceRole Roles = ServiceRole.Menu;

    /// <summary>FUN_005ba080 takes at most 80 champions.</summary>
    private const int MaxTitleholders = 80;

    private ServerOptions O => options.CurrentValue;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static string MatchCount(int round) => $"MATCH_{Math.Clamp(round, 1, CompetitionBracket.Rounds)}";

    // ---- Watches: parked until the scheduler has something to say ----

    /// <summary>
    /// The qualifying round screen's watch on the end of the competition for this player. If the
    /// player's part is already decided when it is armed, the answer goes out at once (once per
    /// connection), so a late arrival is not left waiting.
    /// </summary>
    [Command("CMD_WATCH_COMPLG_END", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> WatchEnd(CommandContext ctx)
    {
        ctx.Session.CompeEndWatchRqid = ctx.Rqid;
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (player is null || CompetitionCatalog.Find(O, player.CompetitionId) is not { } c)
            return [];

        var progress = await db.CompetitionProgress.AsNoTracking().FirstOrDefaultAsync(p => p.CompeId == c.Id, ctx.CancellationToken);
        if (progress is not { QualifierClosed: true })
            return [];

        var bracket = await CompetitionBracket.LoadAsync(db, c.Id, ctx.CancellationToken);
        var drawn = bracket.Any(m => m.HomePid == player.Pid || m.AwayPid == player.Pid);
        var stillIn = CompetitionBracket.MatchOf(bracket, player.Pid) is not null
                      || (drawn && CompetitionBracket.CurrentRound(bracket) != 0 && !bracket.Any(m => m.Finished && m.WinPid != player.Pid && (m.HomePid == player.Pid || m.AwayPid == player.Pid)));
        var (kind, disqualified) = progress.Finished || (drawn && !stillIn)
            ? ("END_PLAYOFF", bracket.Any(m => m.DisqualifiedPid == player.Pid))
            : drawn ? ("FINAL_ADVANCEMENT", false) : ("SECOND_ROUND_EXIT", false);

        var key = $"{c.Id}:{kind}";
        if (ctx.Session.CompeEndAnswered == key)
            return [];
        ctx.Session.CompeEndAnswered = key;
        return [ctx.Ok().Set("end_kind", kind).Set("is_disqualified", disqualified ? "YES" : "NO")];
    }

    [Command("CMD_WATCH_COMPLG_ABORT", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public ValueTask<KvMessage[]> WatchAbort(CommandContext ctx)
    {
        ctx.Session.CompeAbortWatchRqid = ctx.Rqid;
        return Reply.None();
    }

    /// <summary>The player's playoff round: answered at once when it is already on and the player has a match in it.</summary>
    [Command("CMD_WATCH_COMPLG_MATCH_START", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> WatchMatchStart(CommandContext ctx)
    {
        ctx.Session.CompeMatchStartWatchRqid = ctx.Rqid;
        var (c, bracket, match) = await MyMatchAsync(ctx);
        return c is not null && match is not null && Now >= CompetitionCatalog.RoundStart(c, match.Round)
            ? [ctx.Ok().Set("is_match_start", "TRUE")]
            : [];
    }

    [Command("CMD_WATCH_COMPLG_MATCH_START_TIME", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public ValueTask<KvMessage[]> WatchMatchStartTime(CommandContext ctx)
    {
        ctx.Session.CompeMatchStartTimeWatchRqid = ctx.Rqid;
        return Reply.None();
    }

    [Command("CMD_WATCH_COMPLG_END_MATCH", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public ValueTask<KvMessage[]> WatchEndMatch(CommandContext ctx)
    {
        ctx.Session.CompeEndMatchWatchRqid = ctx.Rqid;
        return Reply.None();
    }

    /// <summary>The time the server settles the player's playoff round by; answered at once while the round is on.</summary>
    [Command("CMD_WATCH_SERVERMATCHEND", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> WatchServerMatchEnd(CommandContext ctx)
    {
        ctx.Session.ServerMatchEndWatchRqid = ctx.Rqid;
        var (c, _, match) = await MyMatchAsync(ctx);
        return c is not null && match is not null && Now >= CompetitionCatalog.RoundStart(c, match.Round)
            ? [ctx.Ok().Set("limit_time", CompetitionCatalog.Unix(CompetitionCatalog.RoundEnd(c, match.Round)))]
            : [];
    }

    // ---- The playoff ----

    /// <summary>The competition the player entered, its bracket, and the player's undecided match in the round being played.</summary>
    private async Task<(CompetitionOptions? Cup, List<CompetitionPlayoffMatch> Bracket, CompetitionPlayoffMatch? Match)> MyMatchAsync(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (player is null || CompetitionCatalog.Find(O, player.CompetitionId) is not { } c)
            return (null, [], null);
        var bracket = await CompetitionBracket.LoadAsync(db, c.Id, ctx.CancellationToken);
        return (c, bracket, CompetitionBracket.MatchOf(bracket, player.Pid));
    }

    /// <summary>Notes that a playoff player turned up for their match (so a no-show is told from a slow start). The caller saves.</summary>
    private void ShowedUp(CompetitionPlayoffMatch m, int pid)
    {
        var now = RankSystem.Unix(Now);
        if (m.HomePid == pid && m.HomeShowedAt == 0)
            m.HomeShowedAt = now;
        else if (m.AwayPid == pid && m.AwayShowedAt == 0)
            m.AwayShowedAt = now;
    }

    /// <summary>"Ready to play" before the playoff match (request: has_guestplayer); any reply lets the client go on (FUN_005b1c90).</summary>
    [Command("CMD_GET_COMPLG_PLAYOFF_PLAY_OK", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> PlayOk(CommandContext ctx)
    {
        var (c, _, match) = await MyMatchAsync(ctx);
        if (c is not null && match is not null && Now >= CompetitionCatalog.RoundStart(c, match.Round))
        {
            ShowedUp(match, ctx.Session.Pid);
            await db.SaveChangesAsync(ctx.CancellationToken);
        }

        return [ctx.Ok()];
    }

    /// <summary>
    /// The round's match table (FUN_005c0520): match_count (enum 0x11, the round), match_table_num
    /// (at most 8) and match_table[home, away], each side name, pid, team_id, host_addr,
    /// reflexive_addr, relayed_addr, ntl_nat_type, team_name and ml_team_type (FUN_005bc050). The
    /// client looks for its own pid and plays the other side (FUN_00679c40 case 7); a side of pid 0
    /// is a bye. Asking for it while the round is on means turning up: the player goes into the
    /// match's room.
    /// </summary>
    [Command("CMD_GET_COMPLG_PLAYOFF_MATCH_NTL", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    [Command("CMD_GET_COMPLG_PLAYOFF_MATCH", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> PlayoffMatch(CommandContext ctx)
    {
        var (c, bracket, match) = await MyMatchAsync(ctx);
        if (c is null)
            return [ctx.Fail("ERR_COMPE_NOTENTRY")];
        if (bracket.Count == 0 || !bracket.Any(m => m.HomePid == ctx.Session.Pid || m.AwayPid == ctx.Session.Pid))
            return [ctx.Fail("ERR_COMPE_NOT_PLAYOFF_ENTRY")];

        var round = CompetitionBracket.CurrentRound(bracket);
        if (round == 0)
            return [ctx.Fail("ERR_COMPE_STATUS")];

        if (match is not null && Now >= CompetitionCatalog.RoundStart(c, round))
        {
            ShowedUp(match, ctx.Session.Pid);
            await db.SaveChangesAsync(ctx.CancellationToken);
            var home = match.HomePid == ctx.Session.Pid;
            ctx.Hub.JoinPlayoffRoom(ctx.Session, match.Id, c.Id, match.HomePid, home, c.Name,
                c.MasterLeague ? "ML_COMPE" : "OC_FREE",
                new KvMessage().Set("type", c.MasterLeague ? "ML_COMPE" : "COMPE").Set("compe_id", c.Id).Set("round", round));
        }

        var profiles = await ProfilesAsync(bracket.SelectMany(m => new[] { m.HomePid, m.AwayPid }), ctx.CancellationToken);
        var mlTypes = c.MasterLeague ? await db.MlTeams.AsNoTracking().Where(t => profiles.Keys.Contains(t.Pid))
            .ToDictionaryAsync(t => t.Pid, t => t.TeamType, ctx.CancellationToken) : [];

        KvMessage Side(int pid, int teamId)
        {
            var p = profiles.GetValueOrDefault(pid);
            var session = pid == 0 ? null : ctx.Hub.SessionsForPid(pid).FirstOrDefault(s => s.NtlHostAddr is not null) ?? ctx.Hub.SessionsForPid(pid).FirstOrDefault();
            return new KvMessage()
                .Set("name", p?.Name ?? "")
                .Set("pid", pid)
                .Set("team_id", teamId)
                .Set("host_addr", session is null ? NtlAddress.Zero() : NtlAddress.HostFor(ctx.Session, session) ?? NtlAddress.Zero())
                .Set("reflexive_addr", session?.NtlReflexiveAddr ?? NtlAddress.Zero())
                .Set("relayed_addr", session?.NtlRelayedAddr ?? NtlAddress.Zero())
                .Set("ntl_nat_type", session?.NtlNatType ?? 0)
                .Set("team_name", TeamName(p, c.Id))
                .Set("ml_team_type", mlTypes.GetValueOrDefault(pid, ""));
        }

        var table = bracket.Where(m => m.Round == round).Take(8)
            .Select(m => new KvMessage().Set("match_id", m.Id).Set("home", Side(m.HomePid, m.HomeTeamId)).Set("away", Side(m.AwayPid, m.AwayTeamId)))
            .ToList();
        return [ctx.Ok().Set("match_count", MatchCount(round)).SetList("match_table_num", "match_table", table)];
    }

    /// <summary>
    /// The bracket (FUN_005bf5f0): trophy (enum 0x17), match_count (enum 0x11, the round being
    /// played), player_list_num (at most 16), cup_file_name, player_list[name, pid, team_id,
    /// is_restricted, rating, skill_level, last_result, npid, nickname_id, team_name] and
    /// game_result_1..4 (8, 4, 2 and 1 matches, each is_end, home_pid, away_pid, win_pid,
    /// home_score and away_score records). Every round up to match_count must be complete; later
    /// rounds are sent too, with the sides not known yet as 0.
    /// </summary>
    [Command("CMD_GET_COMPLG_TOURNAMENT", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetTournament(CommandContext ctx)
    {
        if (CompetitionCatalog.Find(O, ctx.Request.GetInt32("compe_id", 0)) is not { } c)
            return [ctx.Fail("ERR_COMPE_NOTFOUND")];
        var bracket = await CompetitionBracket.LoadAsync(db, c.Id, ctx.CancellationToken);
        if (bracket.Count == 0)
            return [ctx.Fail("ERR_COMPE_STATUS")];

        var round = CompetitionBracket.CurrentRound(bracket);
        var profiles = await ProfilesAsync(Places(bracket), ctx.CancellationToken);
        var list = Places(bracket).Where(pid => pid != 0).Select(pid =>
        {
            var p = profiles.GetValueOrDefault(pid);
            return new KvMessage()
                .Set("name", p?.Name ?? "").Set("pid", pid).Set("team_id", TeamOf(bracket, pid))
                .Set("is_restricted", "FALSE").Set("rating", p?.Rating ?? 0)
                .Set("skill_level", ListWatchEntries.SkillLevel2013(p?.SelfReportLevel))
                .Set("last_result", LastRound(bracket, pid)).Set("npid", "").Set("nickname_id", p?.NicknameId ?? 0)
                .Set("team_name", TeamName(p, c.Id));
        }).ToList();

        var reply = ctx.Ok()
            .Set("trophy", CompetitionCatalog.Trophy(c, ctx.Profile))
            .Set("match_count", MatchCount(round == 0 ? CompetitionBracket.Rounds : round))
            .Set("cup_file_name", c.CupFileName)
            .SetList("player_list_num", "player_list", list);
        return [GameResults(reply, bracket)];
    }

    /// <summary>
    /// The playoff's final results (FUN_005c0950): compe_name, trophy, entry_count, my_result (enum
    /// 0x16), my_pre_rank (the qualifying place), my_team_id, team_name, banner_file_name,
    /// player_list[pid, last_result, team_id, npid, name, team_name] and all four game_result lists.
    /// </summary>
    [Command("CMD_GET_COMPLG_PLAYOFF_RESULTS", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetPlayoffResults(CommandContext ctx)
    {
        if (CompetitionCatalog.Find(O, ctx.Request.GetInt32("compe_id", 0)) is not { } c)
            return [ctx.Fail("ERR_COMPE_NOTFOUND")];
        var bracket = await CompetitionBracket.LoadAsync(db, c.Id, ctx.CancellationToken);
        if (bracket.Count == 0)
            return [ctx.Fail("ERR_COMPE_STATUS")];

        var me = ctx.Session.Pid;
        var table = await CompetitionTable.StandingsAsync(db, c.Id, ctx.CancellationToken);
        var mine = table.FirstOrDefault(l => l.Player.Pid == me);
        var profiles = await ProfilesAsync(Places(bracket), ctx.CancellationToken);
        var list = Places(bracket).Where(pid => pid != 0).Select(pid => new KvMessage()
            .Set("pid", pid).Set("last_result", LastRound(bracket, pid)).Set("team_id", TeamOf(bracket, pid))
            .Set("npid", "").Set("name", profiles.GetValueOrDefault(pid)?.Name ?? "")
            .Set("team_name", TeamName(profiles.GetValueOrDefault(pid), c.Id))).ToList();

        var reply = ctx.Ok()
            .Set("compe_name", c.Name)
            .Set("trophy", CompetitionCatalog.Trophy(c, ctx.Profile))
            .Set("entry_count", list.Count)
            .Set("my_result", CompetitionBracket.ResultOf(bracket, me).Result)
            .Set("my_pre_rank", mine is null ? 0 : CompetitionTable.RankOf(table, mine))
            .Set("my_team_id", TeamOf(bracket, me) is var t && t != 0 ? t : mine?.TeamId ?? 0)
            .Set("team_name", TeamName(mine?.Player, c.Id))
            .Set("banner_file_name", c.BannerFileName)
            .SetList("player_list_num", "player_list", list);
        return [GameResults(reply, bracket)];
    }

    /// <summary>
    /// The champion's commendation screen (FUN_005b8230): compe_name, trophy, name, team_id, pid,
    /// npid, cup_file_name and team_name of the competition's winner.
    /// </summary>
    [Command("CMD_GET_COMPLG_COMMENDATION_DATA", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetCommendation(CommandContext ctx)
    {
        var id = ctx.Request.GetInt32("compe_id", 0);
        if (CompetitionCatalog.Find(O, id) is not { } c)
            return [ctx.Fail("ERR_COMPE_NOTFOUND")];
        var progress = await db.CompetitionProgress.AsNoTracking().FirstOrDefaultAsync(p => p.CompeId == id, ctx.CancellationToken);
        if (progress is not { Finished: true, ChampionPid: > 0 })
            return [ctx.Fail("ERR_COMPE_STATUS")];

        var champion = await players.GetAsync(progress.ChampionPid, ctx.CancellationToken);
        return [Champion(ctx.Ok(), progress, champion?.Name ?? "").Set("compe_name", c.Name).Set("cup_file_name", c.CupFileName)];
    }

    /// <summary>
    /// The champions of past competitions, newest first. PES 2013 (FUN_005ba080, request
    /// is_ml_compe) reads titleholder_num (at most 80) and titleholder_list[start_time, team_id,
    /// pid, compe_name, name, trophy, npid, team_name, banner_file_name, cup_file_name]. PES 2011
    /// (FUN_00561a10) reads one champion at the top level, and a reply without one as "no
    /// champion yet".
    /// </summary>
    [Command("CMD_GET_COMPLG_TITLEHOLDER", Roles = Roles)]
    public async ValueTask<KvMessage[]> GetTitleHolder(CommandContext ctx)
    {
        var ml = ctx.Request.GetString("is_ml_compe") == "YES";
        var finished = await db.CompetitionProgress.AsNoTracking()
            .Where(p => p.Finished && p.ChampionPid > 0 && (ctx.Profile != GameProfile.Pes2013Pc || p.MasterLeague == ml))
            .OrderByDescending(p => p.FinishedAt).Take(MaxTitleholders).ToListAsync(ctx.CancellationToken);
        var names = await ProfilesAsync(finished.Select(p => p.ChampionPid), ctx.CancellationToken);

        if (ctx.Profile != GameProfile.Pes2013Pc)
            return finished.FirstOrDefault() is { } last
                ? [Champion(ctx.Ok(), last, names.GetValueOrDefault(last.ChampionPid)?.Name ?? "").Set("compe_name", last.Name)]
                : [ctx.Ok()];

        var list = finished.Select(p =>
        {
            var c = CompetitionCatalog.Find(O, p.CompeId);
            return Champion(new KvMessage(), p, names.GetValueOrDefault(p.ChampionPid)?.Name ?? "")
                .Set("start_time", c is null ? p.FinishedAt : CompetitionCatalog.Unix(c.EntryStart))
                .Set("compe_name", p.Name)
                .Set("banner_file_name", c?.BannerFileName ?? "")
                .Set("cup_file_name", c?.CupFileName ?? "");
        }).ToList();
        return [ctx.Ok().SetList("titleholder_num", "titleholder_list", list)];
    }

    /// <summary>The champion's keys every champion screen shares.</summary>
    private static KvMessage Champion(KvMessage m, CompetitionProgress p, string name)
        => m.Set("trophy", p.Trophy).Set("name", name).Set("team_id", p.ChampionTeamId).Set("pid", p.ChampionPid)
            .Set("npid", "").Set("team_name", p.ChampionTeamName.Length > 0 ? p.ChampionTeamName : "-");

    /// <summary>
    /// The playoff matches being played, for watching (FUN_005beb40: match_count, match_list_num at
    /// most 8, match_list[match_id, game_phase (enum 0x23), home, away], each side pid,
    /// is_restricted, rating, team_id, name, skill_level, npid, team_name and score). Sent once as
    /// it is when asked.
    /// ponytail: no ADD/UPDATE/DEL pushes follow; the client asks again when it reopens the screen.
    /// </summary>
    [Command("CMD_REQWATCHL_COMPLG_MATCHLIST", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> WatchMatchList(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        var c = player is null ? null : CompetitionCatalog.Find(O, ctx.Request.GetInt32("compe_id", player.CompetitionId));
        var bracket = c is null ? [] : await CompetitionBracket.LoadAsync(db, c.Id, ctx.CancellationToken);
        var round = CompetitionBracket.CurrentRound(bracket);
        var live = c is null || round == 0 || Now < CompetitionCatalog.RoundStart(c, round) ? []
            : bracket.Where(m => m.Round == round && !m.Finished && m.HomeShowedAt != 0 && m.AwayShowedAt != 0).Take(8).ToList();
        var profiles = await ProfilesAsync(live.SelectMany(m => new[] { m.HomePid, m.AwayPid }), ctx.CancellationToken);

        KvMessage Side(int pid, int teamId, int goals)
        {
            var p = profiles.GetValueOrDefault(pid);
            return new KvMessage()
                .Set("pid", pid).Set("is_restricted", "FALSE").Set("rating", p?.Rating ?? 0).Set("team_id", teamId)
                .Set("name", p?.Name ?? "").Set("skill_level", ListWatchEntries.SkillLevel2013(p?.SelfReportLevel))
                .Set("npid", "").Set("team_name", TeamName(p, c?.Id ?? 0)).Set("score", Score("", goals));
        }

        var list = live.Select(m =>
        {
            var room = ctx.Hub.Rooms.FirstOrDefault(r => r.PlayoffMatchId == m.Id);
            var (home, away) = room is null ? (0, 0)
                : MatchResults.Lineup(room).Home == m.HomePid ? (room.HomeGoals, room.AwayGoals) : (room.AwayGoals, room.HomeGoals);
            return new KvMessage().Set("match_id", m.Id).Set("game_phase", GamePhase(room?.Phase))
                .Set("home", Side(m.HomePid, m.HomeTeamId, home)).Set("away", Side(m.AwayPid, m.AwayTeamId, away));
        }).ToList();

        return [ctx.Ok().Set("list_type", "ADD").Set("match_count", MatchCount(round == 0 ? CompetitionBracket.Rounds : round))
            .SetList(ctx.Request.GetString("listnum_name") ?? "match_list_num", ctx.Request.GetString("list_name") ?? "match_list", list)];
    }

    /// <summary>The room's phase as enum 0x23 (ENTRY, 1ST, BREAK1, 2ND, ... PK, END).</summary>
    private static string GamePhase(string? phase)
        => phase is "1ST" or "BREAK1" or "2ND" or "BREAK2" or "EX1ST" or "BREAK3" or "EX2ND" or "BREAK4" or "PK" or "END" ? phase : "ENTRY";

    // ---- Shared pieces ----

    /// <summary>The 16 places of the bracket in drawing order (round 1 slot 0 home, away, slot 1 ...); 0 for an empty place.</summary>
    private static IEnumerable<int> Places(IReadOnlyList<CompetitionPlayoffMatch> bracket)
        => bracket.Where(m => m.Round == 1).OrderBy(m => m.Slot).SelectMany(m => new[] { m.HomePid, m.AwayPid });

    private static int TeamOf(IReadOnlyList<CompetitionPlayoffMatch> bracket, int pid)
        => bracket.Where(m => m.Round == 1).Select(m => m.HomePid == pid ? m.HomeTeamId : m.AwayPid == pid ? m.AwayTeamId : 0).FirstOrDefault(t => t != 0);

    /// <summary>
    /// The last round the player reached (1..4, 5 for the champion).
    /// ponytail: last_result's meaning is not known; it is a plain number in both parsers.
    /// </summary>
    private static int LastRound(IReadOnlyList<CompetitionPlayoffMatch> bracket, int pid)
    {
        if (CompetitionBracket.Champion(bracket) == pid)
            return CompetitionBracket.Rounds + 1;
        return bracket.Where(m => m.HomePid == pid || m.AwayPid == pid).Select(m => m.Round).DefaultIfEmpty(0).Max();
    }

    /// <summary>The four game_result lists, each complete (FUN_005bbc70 reads exactly 8, 4, 2, 1 entries).</summary>
    private static KvMessage GameResults(KvMessage reply, IReadOnlyList<CompetitionPlayoffMatch> bracket)
    {
        for (var round = 1; round <= CompetitionBracket.Rounds; round++)
        {
            var matches = Enumerable.Range(0, CompetitionBracket.Matches(round)).Select(slot =>
            {
                var m = CompetitionBracket.At(bracket, round, slot) ?? new CompetitionPlayoffMatch { Round = round, Slot = slot };
                return new KvMessage()
                    .Set("is_end", m.Finished ? "TRUE" : "FALSE")
                    .Set("home_pid", m.HomePid).Set("away_pid", m.AwayPid).Set("win_pid", m.WinPid)
                    .Set("home_score", Score(m.HomePeriods, m.HomeScore)).Set("away_score", Score(m.AwayPeriods, m.AwayScore));
            }).ToList();
            reply.Set($"game_result_{round}", matches);
        }

        return reply;
    }

    /// <summary>Goals per period (score_1st, _2nd, _ex1st, _ex2nd, _pk; FUN_005c2e70).</summary>
    private static KvMessage Score(string periods, int total)
    {
        var p = periods.Length > 0 ? MatchSheet.Parse(periods) : [total, 0, 0, 0, 0];
        return new KvMessage().Set("score_1st", p[0]).Set("score_2nd", p[1]).Set("score_ex1st", p[2]).Set("score_ex2nd", p[3]).Set("score_pk", p[4]);
    }

    private async Task<Dictionary<int, Player>> ProfilesAsync(IEnumerable<int> pids, CancellationToken ct)
    {
        var ids = pids.Where(p => p != 0).Distinct().ToList();
        return await db.Players.AsNoTracking().Where(p => ids.Contains(p.Pid)).ToDictionaryAsync(p => p.Pid, ct);
    }

    /// <summary>The team the player picked for the competition; a dash when it is not on the player any more.</summary>
    private static string TeamName(Player? p, int compeId)
        => p is not null && p.CompetitionId == compeId && p.CompetitionTeamName.Length > 0 ? p.CompetitionTeamName : "-";
}
