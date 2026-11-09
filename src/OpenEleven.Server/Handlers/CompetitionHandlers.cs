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
/// Online competitions (PES 2011 KONAMI CUP ONLINE, PES 2013 Online Competition): browsing, the
/// detail page, entry and withdrawal, the team, and the qualifying round's screens. The playoff
/// is <see cref="CompetitionPlayoffHandlers"/>. Competitions come from config
/// (<see cref="CompetitionCatalog"/>); a player's entry is <c>Players.CompetitionId</c>.
/// </summary>
/// <remarks>
/// The client parsers drop a whole reply when one required key is missing or one enum name is
/// unknown, so every key below is read by them. pes2011.exe: list FUN_0055cdb0, detail
/// FUN_0055daf0, kinds FUN_00565da0, my entry FUN_0055c9d0. pes2013.exe: list FUN_005b31e0,
/// detail FUN_005b3fc0, kinds FUN_005bc560's reply, join FUN_005b5700, my results FUN_005bfed0.
/// Times are Unix seconds.
/// </remarks>
public sealed class CompetitionHandlers(
    GameDbContext db,
    IPlayerRepository players,
    IOptionsMonitor<ServerOptions> options,
    TimeProvider time)
{
    private const ServiceRole Roles = ServiceRole.Menu;

    /// <summary>The list parsers reject compe_num above 20.</summary>
    private const int MaxListed = 20;

    /// <summary>The my-results parsers reject match_result_num above 20.</summary>
    private const int MaxMyResults = 20;

    /// <summary>pes2013.exe takes at most 124 kinds (FUN_005bc560's reply: compe_kind_num &lt; 0x7d).</summary>
    private const int MaxKinds = 124;

    private ServerOptions O => options.CurrentValue;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private bool Is2013(CommandContext ctx) => ctx.Profile == GameProfile.Pes2013Pc;

    /// <summary>
    /// pes2013.exe's compe_mode (enum 0x12): NORMAL asks for the normal competitions, ML for the
    /// Master League Online ones. PES 2011 sends none and sees all.
    /// </summary>
    private IEnumerable<CompetitionOptions> Competitions(CommandContext ctx)
    {
        var all = CompetitionCatalog.All(O, Now);
        return ctx.Request.GetString("compe_mode") switch
        {
            "ML" => all.Where(c => c.MasterLeague),
            "NORMAL" when Is2013(ctx) => all.Where(c => !c.MasterLeague),
            _ => all,
        };
    }

    private CompetitionOptions? Find(int id) => CompetitionCatalog.Find(O, id);

    /// <summary>
    /// Entering and leaving the Online Competition menu (pes2013.exe FUN_0066d9f0 sends
    /// CMD_JOIN_COMPLG_MODE with jdlc_type and only waits for a reply). Nothing is kept.
    /// </summary>
    [Command("CMD_JOIN_COMPLG_MODE", Roles = Roles)]
    [Command("CMD_LEAVE_COMPLG_MODE", Roles = Roles)]
    public ValueTask<KvMessage[]> Mode(CommandContext ctx) => Reply.Of(ctx.Ok());

    [Command("CMD_GET_COMPLG_KIND_LIST", Roles = Roles)]
    public ValueTask<KvMessage[]> GetKindList(CommandContext ctx)
    {
        var kinds = Competitions(ctx).GroupBy(c => CompetitionCatalog.Kind(c, ctx.Profile)).Take(MaxKinds)
            .Select(g =>
            {
                var entry = new KvMessage().Set("compe_kind", g.Key).Set("compe_name", g.First().Name);
                return Is2013(ctx) ? entry.Set("banner_file_name", g.First().BannerFileName) : entry;
            })
            .ToArray();
        return Reply.Of(ctx.Ok().SetList("compe_kind_num", "compe_kind_list", kinds));
    }

    /// <summary>
    /// compe_list_kind (NOW_PLAY, NOW_WATCH, OLD_ALL, OLD_MY, SCHEDULE) must be echoed, and for
    /// OLD_ALL compe_kind too; otherwise the client asks for its default kind (PES 2011
    /// KONAMI_CUP_ONLINE, PES 2013 KONAMI_CHAMPIONSHIP), so the kind only filters the archive.
    /// </summary>
    [Command("CMD_GET_COMPLG_LIST", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetList(CommandContext ctx)
    {
        var listKind = ctx.Request.GetString("compe_list_kind") ?? "NOW_PLAY";
        var kind = ctx.Request.GetString("compe_kind");
        var now = Now;
        var mine = (await players.GetCompetitionEntriesAsync(ctx.Session.Pid, ctx.CancellationToken)).Select(e => e.CompeId).ToHashSet();

        bool Listed(CompetitionOptions c) => listKind switch
        {
            "SCHEDULE" => now < c.EntryStart,
            "NOW_PLAY" or "NOW_WATCH" => now >= c.EntryStart && now < c.End,
            "OLD_ALL" => now >= c.End && CompetitionCatalog.Kind(c, ctx.Profile) == kind,
            "OLD_MY" => now >= c.End && mine.Contains(c.Id),
            _ => false,
        };

        var entries = new List<KvMessage>();
        var listed = Competitions(ctx).Where(Listed);
        listed = listKind.StartsWith("OLD", StringComparison.Ordinal) ? listed.OrderByDescending(c => c.End) : listed.OrderBy(c => c.EntryStart);
        foreach (var c in listed.Take(MaxListed))
            entries.Add(await Summary(ctx, c, ctx.CancellationToken));

        var reply = ctx.Ok().Set("compe_list_kind", listKind);
        if (listKind == "OLD_ALL" || (Is2013(ctx) && kind is not null))
            reply.Set("compe_kind", kind ?? "");
        return [reply.SetList("compe_num", "compe_list", entries)];
    }

    /// <summary>Everything sits in compe_data, whose compe_id must be the one asked for.</summary>
    [Command("CMD_GET_COMPLG_INFO", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetInfo(CommandContext ctx)
    {
        if (Find(ctx.Request.GetInt32("compe_id", 0)) is not { } c)
            return [ctx.Fail("ERR_COMPE_NOTFOUND")];

        var teams = TitleProfiles.UseableTeams(O.GameProfile, O.Teams);
        string Time(string t) => t.Length > 0 ? t : c.GameTime;
        const string yes = "TRUE", no = "FALSE";
        var data = (await Summary(ctx, c, ctx.CancellationToken))
            // A Master League competition is played with the entrant's ML club only.
            .Set("use_national", c.MasterLeague ? no : yes)
            .Set("use_club", c.MasterLeague ? no : yes)
            .Set("use_ml", c.MasterLeague ? yes : no)
            .Set("useable_team_size", teams.Length)
            .Set("useable_team_id", new KvArray(teams.Cast<object?>().ToArray()))
            .Set("usable_ball_pre_num", 0)
            .Set("usable_ball_playoff_num", 0)
            .Set("usable_ball_final_num", 0)
            // Qualifying round, playoff rounds, final.
            .Set("gametime", new KvArray(c.GameTime, Time(c.PlayoffGameTime), Time(c.FinalGameTime)))
            .Set("fee", c.Fee)
            .Set("is_digest", "NO");

        if (Is2013(ctx))
        {
            // FUN_005b3fc0 reads five ball and spike prizes, eight second-round prizes and five tournament
            // prizes (VICTORY, SECOND_PLACE, BEST_4, BEST_8, FINAL_ADVANCEMENT) as plain numbers.
            long Prize(int i) => c.MasterLeague && i < c.Prizes.Length ? c.Prizes[i] : 0;
            data.Set("compe_fee", c.Fee)
                .Set("comment", c.Comment)
                .Set("get_ball_id", KvArray.Repeat(0, 5))
                .Set("get_spike_id", KvArray.Repeat(0, 5))
                .Set("prize_second_round_list", KvArray.Repeat(0, 8))
                .Set("prize_tournament_list", new KvArray(Enumerable.Range(0, 5).Select(i => (object?)Prize(i)).ToArray()))
                .Set("ad_file_name", "");
        }

        return [ctx.Ok().Set("compe_data", data)];
    }

    /// <summary>The entered compe_id, 0 when not entered or the competition is over.</summary>
    [Command("CMD_GET_COMPLG_MYENTRY_COMPEID", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetMyEntry(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        var entered = player is not null && Find(player.CompetitionId) is { } c && Now < c.End ? c.Id : 0;
        return [ctx.Ok().Set("compe_id", entered)];
    }

    /// <summary>
    /// Only the error names in the client's table for CMD_JOIN_COMPLG are used. The NOERR reply
    /// has to carry <c>is_select_team</c> (YES/NO): without it the client drops the reply as a
    /// failed join and stays on the detail page. It says whether the player has picked a team yet
    /// (CMD_SET_COMPLG_TEAM). PES 2013 sends <c>is_entry</c>: YES to enter, NO to open the
    /// competition already entered (FUN_005b5650's two callers).
    /// </summary>
    [Command("CMD_JOIN_COMPLG", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Join(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (player is null || Find(ctx.Request.GetInt32("compe_id", 0)) is not { } c)
            return [ctx.Fail("ERR_DATABASE")];
        if (player.CompetitionId == c.Id)
        {
            await players.RecordCompetitionEntryAsync(player.Pid, c.Id, CompetitionCatalog.Kind(c, ctx.Profile), c.Name, ctx.CancellationToken);
            return [ctx.Ok().Set("is_select_team", player.CompetitionTeamId > 0 || c.MasterLeague ? "YES" : "NO")];
        }

        if (ctx.Request.GetString("is_entry") == "NO")
            return [ctx.Fail("ERR_COMPE_NOTENTRY")];

        var now = Now;
        if (now < c.EntryStart)
            return [ctx.Fail("ERR_COMPE_BEFORE_ENTRYTIME")];
        if (now >= c.EntryEnd)
            return [ctx.Fail("ERR_COMPE_AFTER_ENTRYTIME")];
        if (Find(player.CompetitionId) is { } other && now < other.End)
            return [ctx.Fail("ERR_COMPE_JOIN_OTHERCOMPE")];
        // ponytail: count-then-save race can overfill by a player or two; a DB constraint if it matters.
        if (await players.CountInCompetitionAsync(c.Id, ctx.CancellationToken) >= c.Capacity)
            return [ctx.Fail("ERR_COMPE_FULL")];
        if (Is2013(ctx) && !CompetitionCatalog.SkillAllowed(c, ListWatchEntries.SkillLevel2013(player.SelfReportLevel)))
            return [ctx.Fail("ERR_COMPE_JOIN_SKILL_LEVEL")];
        if (c.MasterLeague)
        {
            // An ML competition needs an ML club, within the team power limits.
            var league = ctx.Resolve<MasterLeague.MlLeague>();
            if (await league.TeamAsync(player.Pid, ctx.CancellationToken) is null)
                return [ctx.Fail("ERR_NOTREGISTERED")];
            var power = league.TeamPower(await league.SquadAsync(player.Pid, ctx.CancellationToken));
            if ((c.MinTeamPower > 0 && power < c.MinTeamPower) || (c.MaxTeamPower > 0 && power > c.MaxTeamPower))
                return [ctx.Fail("ERR_COMPE_JOIN_TEAMPOWER")];
        }

        player.CompetitionId = c.Id;
        (player.CompetitionTeamId, player.CompetitionTeamName, player.CompetitionTeam) = (0, "", "");
        await players.SaveAsync(ctx.CancellationToken);
        await players.RecordCompetitionEntryAsync(player.Pid, c.Id, CompetitionCatalog.Kind(c, ctx.Profile), c.Name, ctx.CancellationToken);
        await TellPlayerNumAsync(ctx, c.Id);
        // An ML competition is played with the ML club: there is no team to pick.
        return [ctx.Ok().Set("is_select_team", c.MasterLeague ? "YES" : "NO")];
    }

    /// <summary>Who of the competition's entrants has the entrant count screen open hears the new count.</summary>
    private async Task TellPlayerNumAsync(CommandContext ctx, int compeId)
    {
        var count = await players.CountInCompetitionAsync(compeId, ctx.CancellationToken);
        var entered = await db.Players.AsNoTracking().Where(p => p.CompetitionId == compeId).Select(p => p.Pid).ToListAsync(ctx.CancellationToken);
        foreach (var pid in entered.Where(p => p != ctx.Session.Pid))
            CompetitionWatches.PlayerNum(ctx.Hub, pid, count);
    }

    /// <summary>
    /// The Popularity Ranking button: teams by how many entrants picked them, as <c>rank_list</c>
    /// entries of team_id and user_num.
    /// </summary>
    [Command("CMD_GET_COMPLG_TEAMRANKING", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetTeamRanking(CommandContext ctx)
    {
        if (Find(ctx.Request.GetInt32("compe_id", 0)) is not { } c)
            return [ctx.Fail("ERR_COMPE_NOTFOUND")];

        var teams = (await players.GetCompetitionTeamCountsAsync(c.Id, ctx.CancellationToken))
            .Select(t => new KvMessage().Set("team_id", t.TeamId).Set("user_num", t.Players)).ToList();
        return [ctx.Ok().SetList("rank_list_num", "rank_list", teams)];
    }

    /// <summary>
    /// The exit icon of the competition screens sends this each time, and the team picked stays: it
    /// only ends the visit, so the next join finds the entry (and, with is_select_team YES, the
    /// team) as it was. Withdrawing is <see cref="Quit"/>.
    /// </summary>
    [Command("CMD_LEAVE_COMPLG", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Leave(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        return player is null || player.CompetitionId == 0 ? [ctx.Fail("ERR_COMPE_NOTENTRY")] : [ctx.Ok()];
    }

    /// <summary>
    /// Withdraws from the competition: the entry and the team go (the entry stays counted). Once
    /// the qualifying round has started the results stay in the table.
    /// </summary>
    [Command("CMD_QUIT_COMPLG", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Quit(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (player is null || player.CompetitionId == 0)
            return [ctx.Fail("ERR_COMPE_NOTENTRY")];

        var compeId = player.CompetitionId;
        (player.CompetitionId, player.CompetitionTeamId, player.CompetitionTeamName, player.CompetitionTeam) = (0, 0, "", "");
        await players.SaveAsync(ctx.CancellationToken);
        await TellPlayerNumAsync(ctx, compeId);
        return [ctx.Ok()];
    }

    /// <summary>The competition the player has entered, which the qualifying round screens ask about without naming it.</summary>
    private async Task<(CompetitionOptions? Cup, Player? Player, KvMessage? Error)> EnteredAsync(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        var id = ctx.Request.GetInt32("compe_id", 0) is > 0 and var asked ? asked : player?.CompetitionId ?? 0;
        return player is not null && id == player.CompetitionId && Find(id) is { } c ? (c, player, null) : (null, player, ctx.Fail("ERR_COMPE_NOTENTRY"));
    }

    /// <summary>"Number of Users / Max Capacity" on the qualifying round screen: just player_num, again whenever it changes.</summary>
    [Command("CMD_REQWATCH_COMPLG_PLAYERNUM", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> WatchPlayerNum(CommandContext ctx)
    {
        var (cup, _, error) = await EnteredAsync(ctx);
        if (cup is null)
            return [error!];
        ctx.Session.CompePlayerNumWatchRqid = ctx.Rqid;
        return [ctx.Ok().Set("player_num", await players.CountInCompetitionAsync(cup.Id, ctx.CancellationToken))];
    }

    /// <summary>
    /// <c>is_waiting_time</c> TRUE with the <c>match_start_time</c> to wait for, FALSE once the
    /// matches may be played: the qualifying round's start, then a qualifier's next playoff round.
    /// </summary>
    [Command("CMD_GET_COMPLG_MATCH_START_TIME", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetMatchStartTime(CommandContext ctx)
    {
        var (cup, player, error) = await EnteredAsync(ctx);
        if (cup is null)
            return [error!];
        return [CompetitionWatches.StartTime(ctx.Ok(), await WaitUntilAsync(cup, player!.Pid, ctx.CancellationToken))];
    }

    /// <summary>When the player's next match may start, null when it may now (or there is none to wait for).</summary>
    private async Task<DateTime?> WaitUntilAsync(CompetitionOptions c, int pid, CancellationToken ct)
    {
        var now = Now;
        if (now < c.MatchStart)
            return c.MatchStart;
        if (now < c.MatchEnd)
            return null;

        var bracket = await CompetitionBracket.LoadAsync(db, c.Id, ct);
        var round = CompetitionBracket.CurrentRound(bracket);
        if (bracket.Count == 0 || round == 0 || CompetitionBracket.MatchOf(bracket, pid) is null)
        {
            // Not drawn yet: a qualifier waits for the first round.
            var first = CompetitionCatalog.RoundStart(c, CompetitionCatalog.FirstPlayoffRound(c));
            return bracket.Count == 0 && CompetitionCatalog.PlayoffRounds(c) > 0 && now < first ? first : null;
        }

        var start = CompetitionCatalog.RoundStart(c, round);
        return now < start ? start : null;
    }

    /// <summary>The playoff's qualifiers, once drawn; the current top of the table before.</summary>
    private async Task<HashSet<int>> QualifiersAsync(CompetitionOptions c, IReadOnlyList<CompetitionLine> table, CancellationToken ct)
    {
        var progress = await db.CompetitionProgress.AsNoTracking().FirstOrDefaultAsync(p => p.CompeId == c.Id, ct);
        if (progress is { QualifierClosed: true })
            return progress.Qualifiers.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        var size = c.PlayoffPlaces;
        return size < 2 ? [] : table.Where(l => l.Tally.Matches >= Math.Max(1, c.MinQualifierMatches)).Take(size).Select(l => l.Player.Pid).ToHashSet();
    }

    /// <summary>
    /// Standings of the qualifying round: every entrant, three points for a win and one for a draw.
    /// An entry is rank, rank_by_team, team_id, pid, win/lose/draw_count, point, score (goals for),
    /// loss (goals against), scorediff, update_date, rating, ml_point, continuous_win, npid,
    /// is_qualifier (YES/NO: going through to the playoff), name and team_name.
    /// </summary>
    [Command("CMD_GET_COMPLG_PRE_RESULTS", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetPreResults(CommandContext ctx)
    {
        if (Find(ctx.Request.GetInt32("compe_id", 0)) is not { } c)
            return [ctx.Fail("ERR_COMPE_NOTFOUND")];

        var table = await CompetitionTable.StandingsAsync(db, c.Id, ctx.CancellationToken);
        var through = await QualifiersAsync(c, table, ctx.CancellationToken);
        var rows = table.Select(l =>
        {
            var rank = CompetitionTable.RankOf(table, l);
            return new KvMessage()
                .Set("rank", rank).Set("rank_by_team", rank)
                .Set("team_id", l.TeamId).Set("pid", l.Player.Pid)
                .Set("win_count", l.Tally.Wins).Set("lose_count", l.Tally.Losses).Set("draw_count", l.Tally.Draws)
                .Set("point", l.Points).Set("score", l.Tally.GoalsFor).Set("loss", l.Tally.GoalsAgainst).Set("scorediff", l.Difference)
                .Set("update_date", l.UpdatedAt).Set("rating", l.Player.Rating).Set("ml_point", 0).Set("continuous_win", l.Tally.WinRun)
                .Set("npid", "").Set("is_qualifier", through.Contains(l.Player.Pid) ? "YES" : "NO").Set("name", l.Player.Name).Set("team_name", l.TeamName);
        }).ToList();

        return [Page(ctx, rows).Set("compe_name", c.Name).Set("date", CompetitionCatalog.Unix(Now))];
    }

    /// <summary>
    /// Goal and assist rankings: the entrants by goals scored or assists made in the whole
    /// competition (qualifying round and playoff). An entry is rank, team_id, pid, score|assist,
    /// npid, name, team_name.
    /// </summary>
    [Command("CMD_GET_COMPLG_GOALRANKING", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    [Command("CMD_GET_COMPLG_ASSISTRANKING", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetScorerRanking(CommandContext ctx)
    {
        if (Find(ctx.Request.GetInt32("compe_id", 0)) is not { } c)
            return [ctx.Fail("ERR_COMPE_NOTFOUND")];

        var assists = ctx.Msg == "CMD_GET_COMPLG_ASSISTRANKING";
        int Value(CompetitionLine l) => assists ? l.Tally.Assists : l.Tally.GoalsFor;
        var matches = await CompetitionTable.MatchesAsync(db, c.Id, qualifierOnly: false, ctx.CancellationToken);
        var table = (await CompetitionTable.LinesAsync(db, c.Id, matches, ctx.CancellationToken))
            .Where(l => l.Tally.Matches > 0).OrderByDescending(Value).ThenBy(l => l.Player.Pid).ToList();

        var rows = table.Select(l => new KvMessage()
            .Set("rank", 1 + table.Count(o => Value(o) > Value(l))).Set("team_id", l.TeamId).Set("pid", l.Player.Pid)
            .Set(assists ? "assist" : "score", Value(l))
            .Set("npid", "").Set("name", l.Player.Name).Set("team_name", l.TeamName)).ToList();
        return [Page(ctx, rows)];
    }

    /// <summary>
    /// A ranking reply: the part of <paramref name="rows"/> from start_rank on (get_count of them, the
    /// client takes at most 1000), with where it starts and how many there are in all.
    /// </summary>
    private static KvMessage Page(CommandContext ctx, IReadOnlyList<KvMessage> rows)
    {
        var start = Math.Max(1, ctx.Request.GetInt32("start_rank", 1));
        var shown = rows.Skip(start - 1).Take(Math.Clamp(ctx.Request.GetInt32("get_count", 100), 0, 1000)).ToList();
        return ctx.Ok().Set("start_rank", start).Set("rank_count", rows.Count).SetList("rank_num", "rank_list", shown);
    }

    /// <summary>
    /// "My results": the player's own place in the qualifying table and their latest matches of the
    /// competition (the client takes 20), each with both sides. PES 2013 (FUN_005bfed0) also reads
    /// gameend_reason (enum 0x21) per match and is_restricted and skill_level per side
    /// (FUN_005bb1a0).
    /// </summary>
    [Command("CMD_GET_COMPLG_MYRESULTS", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetMyResults(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (player is null || Find(player.CompetitionId) is not { } c)
            return [ctx.Fail("ERR_COMPE_NOTENTRY")];

        var matches = await CompetitionTable.MatchesAsync(db, c.Id, qualifierOnly: false, ctx.CancellationToken);
        var table = await CompetitionTable.LinesAsync(db, c.Id, matches.Where(m => m.PlayoffMatchId == 0).ToList(), ctx.CancellationToken);
        var me = table.FirstOrDefault(l => l.Player.Pid == player.Pid);
        var rank = me is null ? 0 : CompetitionTable.RankOf(table, me);

        var mine = matches.Where(m => (m.HomePid == player.Pid || m.AwayPid == player.Pid) && !(m.Disconnected && m.DisconnectedPid is null))
            .OrderByDescending(m => m.PlayedAt).Take(MaxMyResults).ToList();
        var byPid = table.ToDictionary(l => l.Player.Pid);
        KvMessage Side(int pid, int teamId, string periods, int goals)
        {
            var p = byPid.GetValueOrDefault(pid)?.Player;
            var team = p is not null && (teamId == 0 || teamId == p.CompetitionTeamId) ? p : null;
            var scores = periods.Split(',').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
            if (scores.Length != 5)
                scores = [goals, 0, 0, 0, 0];

            var side = new KvMessage()
                .Set("pid", pid).Set("manner", p?.Manner ?? 3).Set("rating", p?.Rating ?? 0)
                .Set("team_id", teamId != 0 ? teamId : p?.CompetitionTeamId ?? 0)
                .Set("name", p?.Name ?? "").Set("division", p is null ? "ML_DIVISION_5" : RankSystem.Label(p.Division, ctx.Profile))
                .Set("is_guest_player", "FALSE").Set("npid", "")
                .Set("team_name", team is { CompetitionTeamName.Length: > 0 } ? team.CompetitionTeamName : "-")
                .Set("score", new KvMessage().Set("score_1st", scores[0]).Set("score_2nd", scores[1])
                    .Set("score_ex1st", scores[2]).Set("score_ex2nd", scores[3]).Set("score_pk", scores[4]));
            return Is2013(ctx)
                ? side.Set("is_restricted", "FALSE").Set("skill_level", ListWatchEntries.SkillLevel2013(p?.SelfReportLevel))
                : side;
        }

        var results = mine.Select(m =>
        {
            var entry = new KvMessage()
                .Set("date", RankSystem.Unix(m.PlayedAt))
                .Set("home", Side(m.HomePid, m.HomeTeamId, m.HomePeriods, m.HomeScore))
                .Set("away", Side(m.AwayPid, m.AwayTeamId, m.AwayPeriods, m.AwayScore));
            // NORMAL_END, or DISCONN_FORFEITED when someone dropped.
            return Is2013(ctx) ? entry.Set("gameend_reason", m.Disconnected ? "DISCONN_FORFEITED" : "NORMAL_END") : entry;
        }).ToList();

        return [ctx.Ok().Set("team_id", player.CompetitionTeamId).Set("team_name", player.CompetitionTeamName.Length > 0 ? player.CompetitionTeamName : "-")
            .Set("my_rank", rank).SetList("match_result_num", "match_result_list", results)];
    }

    /// <summary>
    /// The team the player picks for the competition: team_id, team_name, member_num and the
    /// footballers' details, kept as sent and played back by <see cref="GetTeamData"/>. The client's
    /// member list ends with a comma, which <see cref="KvReader"/> accepts. The team is fixed once
    /// the qualifying round is over.
    /// </summary>
    [Command("CMD_SET_COMPLG_TEAM", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> SetTeam(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (player is null || Find(player.CompetitionId) is not { } c)
            return [ctx.Fail("ERR_COMPE_NOTENTRY")];
        if (Now >= c.MatchEnd && player.CompetitionTeamId != 0)
            return [ctx.Fail("ERR_COMPE_STATUS")];

        player.CompetitionTeamId = ctx.Request.GetInt32("team_id", 0);
        player.CompetitionTeamName = ctx.Request.GetString("team_name") ?? "";
        player.CompetitionTeam = new KvWriter().Write(new KvMessage().Set("member", ctx.Request.GetValue("member") ?? new List<KvMessage>())).TrimEnd('\0');
        await players.SaveAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>
    /// The squad an entrant picked (request: pid; the parser reads member_num, team_id, team_name and
    /// the member list, and drops the reply without the owner's pid and name). Someone who has
    /// picked none gets the client's own error for it.
    /// </summary>
    [Command("CMD_GET_COMPLG_TEAM_DATA", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetTeamData(CommandContext ctx)
    {
        var me = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        var other = await players.GetAsync(ctx.Request.GetInt32("pid", ctx.Session.Pid), ctx.CancellationToken);
        if (me is null || other is null || me.CompetitionId == 0 || other.CompetitionId != me.CompetitionId)
            return [ctx.Fail("ERR_PLAYERNOTINCOMPE")];
        if (other.CompetitionTeamId == 0 || other.CompetitionTeamName.Length == 0)
            return [ctx.Fail("ERR_COMPE_NOT_SELECT_TEAM")];

        object members = new List<KvMessage>();
        if (other.CompetitionTeam.Length > 0 && new KvReader().TryParse(other.CompetitionTeam)?.GetValue("member") is { } stored)
            members = stored;

        // The player results screen shows each footballer's goals, assists and cards in this competition.
        if (members is IReadOnlyList<KvMessage> squad && other.CompetitionId != 0)
        {
            var played = await db.Matches.AsNoTracking()
                .Where(m => m.CompeId == other.CompetitionId && m.Completed && (m.HomePid == other.Pid || m.AwayPid == other.Pid))
                .Select(m => new { m.HomePid, m.Events }).ToListAsync(ctx.CancellationToken);
            members = CommunityStats.WithTotals(squad, played.Select(m => (m.HomePid == other.Pid ? "HOME" : "AWAY", m.Events)));
        }

        var count = members is IReadOnlyList<KvMessage> list ? list.Count : 0;
        return [ctx.Ok().Set("pid", other.Pid).Set("name", other.Name).Set("team_id", other.CompetitionTeamId).Set("team_name", other.CompetitionTeamName).Set("member_num", count).Set("member", members)];
    }

    /// <summary>The keys the list and the detail page share.</summary>
    private async Task<KvMessage> Summary(CommandContext ctx, CompetitionOptions c, CancellationToken ct)
    {
        var entry = new KvMessage()
            .Set("compe_kind", CompetitionCatalog.Kind(c, ctx.Profile))
            .Set("compe_id", c.Id)
            .Set("entry_start_time", CompetitionCatalog.Unix(c.EntryStart))
            .Set("entry_end_time", CompetitionCatalog.Unix(c.EntryEnd))
            .Set("match_start_time", CompetitionCatalog.Unix(c.MatchStart))
            .Set("match_end_time", CompetitionCatalog.Unix(c.MatchEnd))
            .Set("compe_end_time", CompetitionCatalog.Unix(c.End))
            .Set("compe_name", c.Name)
            .Set("status", CompetitionCatalog.Status(c, Now))
            .Set("category", c.Category)
            // Non-empty, at most 15 characters.
            .Set("dlcver", c.DlcVersion.Length > 0 ? c.DlcVersion : O.Protocol.DlcVersion)
            .Set("player_capacity", c.Capacity)
            .Set("player_num", await players.CountInCompetitionAsync(c.Id, ct))
            .Set("champion_notice", await ChampionNoticeAsync(ctx, c, ct) ? "TRUE" : "FALSE")
            .Set("is_coach_mode", "NO")
            .Set("need_konamiid", "NO");

        if (!Is2013(ctx))
            // PES 2011: entry_limit enum 0x09 DIVISION with the six D5..D1, NONE flags; every division may enter.
            return entry.Set("entry_limit", "DIVISION").Set("entry_limit_rank", 0).Set("guest_count", 0)
                .Set("entry_limit_division", KvArray.Repeat("TRUE", 6));

        // PES 2013 (FUN_005b31e0): entry_limit enum 0x0e, six skill level flags (BEGINNER..SUPERSTAR),
        // the Facebook flag, the team power limits and an optional banner.
        return entry
            .Set("entry_limit", CompetitionCatalog.EntryLimit2013(c))
            .Set("entry_limit_facebook", "FALSE")
            .Set("entry_limit_skill_level", new KvArray(CompetitionCatalog.SkillLevels.Select(l => (object?)(CompetitionCatalog.SkillAllowed(c, l) ? "TRUE" : "FALSE")).ToArray()))
            .Set("entry_limit_team_power_min", c.MinTeamPower)
            .Set("entry_limit_team_power_max", c.MaxTeamPower)
            .Set("banner_file_name", c.BannerFileName)
            .Set("is_ml_compe", c.MasterLeague ? "YES" : "NO");
    }

    /// <summary>The champion is known: the list shows the champion banner.</summary>
    private async Task<bool> ChampionNoticeAsync(CommandContext ctx, CompetitionOptions c, CancellationToken ct)
        => Now >= CompetitionCatalog.PlayoffEnd(c)
           && await db.CompetitionProgress.AnyAsync(p => p.CompeId == c.Id && p.Finished && p.ChampionPid != 0, ct);
}
