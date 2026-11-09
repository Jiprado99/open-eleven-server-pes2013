// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.ComponentModel.DataAnnotations;

namespace OpenEleven.Data.Entities;

/// <summary>
/// How far a competition has got, written by the competition scheduler: the qualifying round
/// closed (and who went through), the playoff round being played, and the champion.
/// </summary>
public sealed class CompetitionProgress
{
    /// <summary>compe_id.</summary>
    public int CompeId { get; set; }

    /// <summary>The qualifying round is over and the playoff bracket was drawn.</summary>
    public bool QualifierClosed { get; set; }

    /// <summary>
    /// The qualifiers by seed (comma-separated pids; seed 1 first). Their place in the qualifying
    /// table is the seed.
    /// </summary>
    [MaxLength(512)]
    public string Qualifiers { get; set; } = "";

    /// <summary>The playoff round started last (1 = round of 16 .. 4 = final), 0 before the playoff.</summary>
    public int Round { get; set; }

    /// <summary>Everything is decided.</summary>
    public bool Finished { get; set; }

    /// <summary>Unix seconds of the end, for the champions' list.</summary>
    public long FinishedAt { get; set; }

    public int ChampionPid { get; set; }
    public int ChampionTeamId { get; set; }

    [MaxLength(150)]
    public string ChampionTeamName { get; set; } = "";

    [MaxLength(96)]
    public string Name { get; set; } = "";

    /// <summary>The cup the trophy is drawn as (compe_kind name).</summary>
    [MaxLength(48)]
    public string Trophy { get; set; } = "";

    public bool MasterLeague { get; set; }

    /// <summary>The end-of-competition mails went out.</summary>
    public bool Mailed { get; set; }
}

/// <summary>
/// One match of a playoff bracket. The client draws 16 places in four rounds: round 1 has slots
/// 0-7, round 2 slots 0-3, round 3 slots 0-1 and the final slot 0; the winners of slots 2k and
/// 2k+1 meet in slot k of the next round. A missing side (pid 0) is a bye.
/// </summary>
public sealed class CompetitionPlayoffMatch
{
    public int Id { get; set; }

    public int CompeId { get; set; }

    /// <summary>1..4.</summary>
    public int Round { get; set; }

    public int Slot { get; set; }

    public int HomePid { get; set; }
    public int AwayPid { get; set; }

    public int HomeTeamId { get; set; }
    public int AwayTeamId { get; set; }

    /// <summary>Goals per period (MatchSheet format) and totals.</summary>
    [MaxLength(32)]
    public string HomePeriods { get; set; } = "";

    [MaxLength(32)]
    public string AwayPeriods { get; set; } = "";

    public int HomeScore { get; set; }
    public int AwayScore { get; set; }

    /// <summary>Who goes through; 0 when nobody does (neither turned up).</summary>
    public int WinPid { get; set; }

    public bool Finished { get; set; }

    /// <summary>Settled on penalties, or level with no shoot-out (the higher seed goes through).</summary>
    public bool Penalties { get; set; }
    public bool Draw { get; set; }

    /// <summary>Enum 0x18 of the end: NORMAL_END, TIMEOUT (a no-show), GAMEING_ERR.</summary>
    [MaxLength(16)]
    public string EndKind { get; set; } = "NORMAL_END";

    /// <summary>The loser did not turn up (CMD_WATCH_COMPLG_END is_disqualified).</summary>
    public int DisqualifiedPid { get; set; }

    /// <summary>Unix seconds the players asked for the match (PLAYOFF_MATCH_NTL); 0 = not yet.</summary>
    public long HomeShowedAt { get; set; }
    public long AwayShowedAt { get; set; }

    /// <summary>The match history record of the game that decided it, 0 for none.</summary>
    public long MatchRecordId { get; set; }

    /// <summary>The players were told the result (CMD_WATCH_COMPLG_END_MATCH).</summary>
    public bool Notified { get; set; }
}
