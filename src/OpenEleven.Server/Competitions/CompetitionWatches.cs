// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol.Kv;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Competitions;

/// <summary>
/// Answers to the competition watches a client parked (CMD_WATCH_COMPLG_*). Each goes to every
/// connection of the player that armed it, under its rqid; a player who has not opened the
/// screen gets nothing and reads the state when it opens.
/// </summary>
public static class CompetitionWatches
{
    private static int Push(Hub hub, int pid, Func<Session, int?> rqid, string msg, Action<KvMessage> fields)
    {
        var sent = 0;
        foreach (var s in hub.SessionsForPid(pid))
        {
            if (rqid(s) is not { } id)
                continue;
            var message = KvMessage.Ok(msg, id);
            fields(message);
            if (s.Push(message))
                sent++;
        }

        return sent;
    }

    /// <summary>
    /// CMD_WATCH_COMPLG_END (FUN_005b6df0): end_kind (enum 0x14) FINAL_ADVANCEMENT to the qualifiers
    /// when the qualifying round closes, SECOND_ROUND_EXIT to the rest, END_PLAYOFF when the player's
    /// playoff is over; is_disqualified (NO|YES) for a no-show.
    /// ponytail: which end_kind the client expects at each moment is read from the enum's names, not a capture.
    /// </summary>
    public static int End(Hub hub, int pid, string endKind, bool disqualified = false)
        => Push(hub, pid, s => s.CompeEndWatchRqid, "CMD_WATCH_COMPLG_END",
            m => m.Set("end_kind", endKind).Set("is_disqualified", disqualified ? "YES" : "NO"));

    /// <summary>CMD_WATCH_COMPLG_ABORT: the competition was called off (any reply aborts, FUN_005b7240).</summary>
    public static int Abort(Hub hub, int pid)
        => Push(hub, pid, s => s.CompeAbortWatchRqid, "CMD_WATCH_COMPLG_ABORT", _ => { });

    /// <summary>CMD_WATCH_COMPLG_MATCH_START (FUN_005b73b0): is_match_start TRUE when the player's playoff round begins.</summary>
    public static int MatchStart(Hub hub, int pid)
        => Push(hub, pid, s => s.CompeMatchStartWatchRqid, "CMD_WATCH_COMPLG_MATCH_START", m => m.Set("is_match_start", "TRUE"));

    /// <summary>CMD_WATCH_COMPLG_MATCH_START_TIME (FUN_005b7990): the same reply as CMD_GET_COMPLG_MATCH_START_TIME.</summary>
    public static int MatchStartTime(Hub hub, int pid, DateTime? waitUntil)
        => Push(hub, pid, s => s.CompeMatchStartTimeWatchRqid, "CMD_WATCH_COMPLG_MATCH_START_TIME", m => StartTime(m, waitUntil));

    /// <summary>is_waiting_time TRUE with the match_start_time to wait for, FALSE once matches may be played.</summary>
    public static KvMessage StartTime(KvMessage m, DateTime? waitUntil)
        => waitUntil is { } at
            ? m.Set("is_waiting_time", "TRUE").Set("match_start_time", CompetitionCatalog.Unix(at))
            : m.Set("is_waiting_time", "FALSE");

    /// <summary>
    /// CMD_WATCH_SERVERMATCHEND (FUN_005b7680): limit_time, when the server settles the round.
    /// ponytail: sent as unix seconds like the other times; a count of seconds would read the same way only if the client compares it to now.
    /// </summary>
    public static int ServerMatchEnd(Hub hub, int pid, DateTime limit)
        => Push(hub, pid, s => s.ServerMatchEndWatchRqid, "CMD_WATCH_SERVERMATCHEND", m => m.Set("limit_time", CompetitionCatalog.Unix(limit)));

    /// <summary>
    /// CMD_WATCH_COMPLG_END_MATCH (FUN_005b7d30): end_kind (enum 0x18 NORMAL_END, TIMEOUT...),
    /// is_pk and is_draw (FALSE|TRUE) and win_pid.
    /// </summary>
    public static int EndMatch(Hub hub, int pid, Data.Entities.CompetitionPlayoffMatch match)
        => Push(hub, pid, s => s.CompeEndMatchWatchRqid, "CMD_WATCH_COMPLG_END_MATCH", m => m
            .Set("end_kind", match.EndKind)
            .Set("is_pk", match.Penalties ? "TRUE" : "FALSE")
            .Set("is_draw", match.Draw ? "TRUE" : "FALSE")
            .Set("win_pid", match.WinPid));

    /// <summary>The answer to CMD_REQWATCH_COMPLG_PLAYERNUM (FUN_005b6bc0): just player_num.</summary>
    public static int PlayerNum(Hub hub, int pid, int playerNum)
        => Push(hub, pid, s => s.CompePlayerNumWatchRqid, "CMD_REQWATCH_COMPLG_PLAYERNUM", m => m.Set("player_num", playerNum));
}
