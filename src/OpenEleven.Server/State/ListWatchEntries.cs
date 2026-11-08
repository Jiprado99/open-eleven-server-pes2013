// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.State;

/// <summary>A CMD_REQWATCHL_* registration: the reply has to keep using the names the client chose.</summary>
public sealed record WatchListSubscription(string Msg, int Rqid, string NumName, string ListName);

/// <summary>
/// Entry builders for the PES 2011 and later list watches (room list, room players, lobby players). Field names are the ones the client's
/// parsers read: room entries from <c>FUN_00515770</c>, block players from <c>FUN_00514a80</c>.
/// </summary>
public static class ListWatchEntries
{
    public static KvMessage RoomEntry(Room room, GameProfile profile)
    {
        var env = room.GameEnvironment;
        string Env(string key, string fallback) => env?.GetString(key) ?? fallback;
        int EnvInt(string key, int fallback) => env?.GetInt32(key, fallback) ?? fallback;

        var members = room.Snapshot();
        // enter_no / game_no are the real slot numbers; game_no is -1 until the player enters
        // the match (signed in the client's reader, like game_entry_no below).
        var gamers = members.Select(m =>
        {
            var gamer = new KvMessage()
                .Set("pid", m.Pid)
                .Set("is_room_owner", m.Pid == room.OwnerPid ? "YES" : "NO")
                .Set("side", m.GameSide == 1 ? "AWAY" : "HOME")
                .Set("is_watcher", "NO")
                .Set("mpid", m.Pid)
                .Set("guest_mpid", 0)
                .Set("enter_no", m.RoomEntryNo)
                .Set("game_no", m.GameEntryNo);

            // pes2012.exe parses the room list in "mode 1" (FUN_006023e0 -> FUN_00540fe0), where
            // FUN_0053d650 reads each gamer's profile inline instead of looking the pid up in the
            // block roster (FUN_0053bbf0 does that lookup only in mode 0). A missing key drops the
            // whole room row.
            if (profile.IsSince2012())
                gamer.Set("name", m.PlayerName)
                    .Set("xuid", 0)
                    .Set("division", m.Card.Division)
                    .Set("is_restricted", "NO")
                    .Set("lang", m.Card.Lang)
                    .Set("rating", m.Card.Rating)
                    .Set("selfreport_level", m.Card.SelfReportLevel)
                    .Set("position_want", m.Card.PositionWant);

            return gamer;
        }).ToArray();

        var entry = new KvMessage()
            .Set("match_type", room.MatchType.Length > 0 ? room.MatchType : "OC_FREE")
            .Set("room_id", room.Id)
            .Set("status", room.Status)
            .Set("game_phase", room.Phase)
            .Set("name", room.Name)
            // FUN_00515770 drops the whole row unless room_list[i].gameenv is present.
            .Set("gameenv", new KvMessage()
                .Set("cpuLevel", Env("cpuLevel", "NORMAL"))
                .Set("gametime", Env("gametime", "10MINUTES"))
                .Set("injury", Env("injury", "NO"))
                .Set("condition", Env("condition", "NORMAL"))
                .Set("ball_type", EnvInt("ball_type", 0))
                .Set("exGame", Env("exGame", "NO"))
                .Set("pkOnOff", Env("pkOnOff", "NO"))
                .Set("substitution", EnvInt("substitution", 3))
                .Set("limitTime", Env("limitTime", "NOSET")))
            .Set("is_nogame", "NO")
            .Set("is_passwd", room.Password.Length > 0 ? "YES" : "NO")
            .Set("max_players", room.MaxMembers)
            .Set("is_invite_limit", room.InviteLimited ? "YES" : "NO")
            .Set("team_name", room.TeamName)
            .Set("short_teamname", room.ShortTeamName)
            .Set("is_legends_vs_player", room.LegendsVsPlayer)
            .Set("total_combination", room.TotalCombination)
            .Set("max_combination", room.MaxCombination)
            .Set("dlcontent", room.DlcVersion.Length > 0 ? room.DlcVersion : "1.00")
            .SetList("gamer_num", "gamer", gamers)
            .Set("enable_guest", room.AllowGuest ? "YES" : "NO");

        // pes2012.exe's room key table ends with FD_rank right after is_legends_vs_player; PES 2011 has no such key.
        // ponytail: always 0, Legends ranking is not served. Real value once it is.
        // FUN_0053BB50 / FUN_0053E650 / FUN_0053E740 read team[0..1], team_flag[0..2] and score[0..1]
        // as paths into arrays (FUN_00d39600). They must go out as arrays: the client's tokenizer
        // (FUN_00d39470) splits on '[', so a literal team[0]= key fails the parse of the whole message.
        if (profile.IsSince2012())
        {
            var score = new KvMessage()
                .Set("score_1st", 0).Set("score_2nd", 0).Set("score_ex1st", 0)
                .Set("score_ex2nd", 0).Set("score_pk", 0);
            return entry
                .Set("team_flag", room.TeamFlag)
                .Set("score", new[] { score, score })
                .Set("FD_rank", 0)
                .Set("team", new KvArray(new object?[] { room.HomeTeamId, room.AwayTeamId }));
        }

        return entry.Set("team_flag", room.TeamFlag);
    }

    /// <summary>
    /// One member of a room as the watcher sees it. FUN_00640e10 reads pid + ex_ip/ex_port/in_ip/in_port
    /// out of every entry to build the P2P peer table (it skips the viewer's own pid); without
    /// them the client never opens a connection to the host and gives up with CMD_LEAVE_ROOM after 40 s.
    /// </summary>
    public static KvMessage RoomPlayerEntry(Session member, PeerEndpoints at, GameProfile profile)
    {
        // pes2013.exe's roomplayer_list key table: name, room_player_id, host_addr, reflexive_addr,
        // relayed_addr, ntl_nat_type, nickname_id, contribution_prize, team_match_nice_num,
        // evaluation_average, stats, game_entry_no, is_team_data_load, side. The 2012 ex_ip/in_ip,
        // desiredPosition, room_entry_no and gameplayer_name keys are not in that exe at all.
        // ponytail: from the exe's strings only. The value of stats (a win/draw/lose record, going by the
        // community screens) and of is_team_data_load (TRUE|FALSE assumed) are guesses.
        if (profile == GameProfile.Pes2013Pc)
        {
            static KvMessage Zero() => new KvMessage().Set("address_family", 1).Set("sa_data", "0000000000000000000000000000");
            return new KvMessage()
                .Set("pid", member.Pid)
                .Set("name", member.PlayerName)
                .Set("room_player_id", member.Pid)
                .Set("host_addr", member.NtlHostAddr ?? Zero())
                .Set("reflexive_addr", member.NtlReflexiveAddr ?? Zero())
                .Set("relayed_addr", member.NtlRelayedAddr ?? Zero())
                .Set("ntl_nat_type", member.NtlNatType)
                .Set("nickname_id", member.Card.NicknameId)
                .Set("contribution_prize", 0)
                .Set("team_match_nice_num", 0)
                .Set("evaluation_average", 0)
                .Set("stats", new KvMessage().Set("win_count", 0).Set("draw_count", 0).Set("lose_count", 0))
                .Set("game_entry_no", member.GameEntryNo)
                .Set("is_team_data_load", "FALSE")
                .Set("side", member.GameSide == 1 ? "AWAY" : "HOME");
        }

        var entry = new KvMessage()
            .Set("pid", member.Pid)
            .Set("room_player_id", member.Pid)
            .Set("room_entry_no", member.RoomEntryNo)
            .Set("game_entry_no", member.GameEntryNo)
            .Set("gameplayer_name", member.PlayerName)
            .Set("ex_ip", at.ExternalIp)
            .Set("ex_port", at.ExternalPort)
            .Set("in_ip", at.InternalIp)
            .Set("in_port", at.InternalPort);

        if (profile.IsSince2012())
        {
            // FUN_0069C0B0 reads desiredPosition[0..13] (14 slots, a path into the array) and the
            // user-list screen also reads the profile fields below.
            return entry
                .Set("desiredPosition", KvArray.Repeat("NO", 14))
                .Set("gameplayer_id", 0)
                .Set("mpid", member.Pid)
                .Set("guest_mpid", 0)
                .Set("xuid", 0)
                .Set("division", member.Card.Division)
                .Set("rating", member.Card.Rating)
                .Set("is_restricted", "NO")
                .Set("fd_selfreport_level", member.Card.SelfReportLevel)
                .Set("fd_position_want", member.Card.PositionWant)
                .Set("player_name", member.PlayerName)
                .Set("lang", member.Card.Lang);
        }

        return entry.Set("desiredPosition", KvArray.Repeat("NO", 13));
    }

    /// <summary>
    /// A peer's NTL report (nat type and host / reflexive / relayed endpoints) as PES 2013 exchanges it:
    /// CMD_GET_IPANDPORT_NTL and CMD_WATCH_IPANDPORT_NTL carry the same keys as CMD_SEND_IPANDPORT_NTL.
    /// ponytail: key set taken from CMD_SEND_IPANDPORT_NTL and the exe's strings, not a live capture.
    /// </summary>
    public static KvMessage NtlPeerInfo(KvMessage target, Session peer, Session? viewer = null)
    {
        return target
            .Set("pid", peer.Pid)
            .Set("ntl_nat_type", peer.NtlNatType)
            .Set("host_addr", NtlAddress.HostFor(viewer, peer) ?? NtlAddress.Zero())
            .Set("reflexive_addr", peer.NtlReflexiveAddr ?? NtlAddress.Zero())
            .Set("relayed_addr", peer.NtlRelayedAddr ?? NtlAddress.Zero());
    }

    /// <summary>Makes a PES 2010 style block entry acceptable to the 2011 parser.</summary>
    public static KvMessage BlockPlayerEntry(KvMessage e, GameProfile profile)
    {
        foreach (var (key, value) in new (string, object)[]
                 {
                     ("npid", ""), ("xuid", 0), ("manner", 3), ("selfreport_level", "PRO"),
                     ("beginnermark", "NO"), ("position_want", "CF"),
                 })
        {
            if (!e.Has(key))
                e.Set(key, value);
        }

        if (!e.Has("desired_position"))
            e.Set("desired_position", KvArray.Repeat("NO", 13));

        // pes2012.exe FUN_00544b40 reads blockplayer_list[i] through a key table that needs three
        // more keys than PES 2011; an entry missing any of them is skipped (the count still shows).
        if (profile.IsSince2012())
        {
            e.Set("is_restricted", "NO");
            e.Set("player_name", e.GetString("name") ?? "");
            e.Set("gameplayer_id", 0);
        }

        // pes2013.exe's blockplayer_list key table is name, skill_level, nickname_id, room_id, rating,
        // kind, lang, npid, xuid, is_restricted: the 2012 selfreport_level is gone (the string is not in
        // the exe) and skill_level, with the enum below, takes its place. Without these two keys the
        // entry is most likely skipped, as in 2012.
        // ponytail: from the exe's strings only; not yet checked against a live 2013 client.
        if (profile == GameProfile.Pes2013Pc)
        {
            e.Set("skill_level", SkillLevel2013(e.GetString("selfreport_level")));
            if (!e.Has("nickname_id"))
                e.Set("nickname_id", 0);
        }

        return e;
    }

    /// <summary>pes2013.exe's skill enum (its string table lists exactly these six values).</summary>
    private static readonly string[] SkillLevels2013 =
        ["BEGINNER", "AMATEUR", "STANDARD", "PROFESSIONAL", "TOPPLAYER", "SUPERSTAR"];

    /// <summary>The stored 2012-era value (default "PRO") is not in the 2013 enum; anything unknown becomes STANDARD.</summary>
    internal static string SkillLevel2013(string? stored)
        => stored is not null && SkillLevels2013.Contains(stored, StringComparer.Ordinal) ? stored : "STANDARD";
}
