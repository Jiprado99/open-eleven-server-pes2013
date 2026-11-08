// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.ComponentModel.DataAnnotations;

namespace OpenEleven.Data.Entities;

/// <summary>
/// A player's Master League Online club (PES 2013, CMD_CREATE_ML_MYTEAM). One per profile: the
/// pid is the key. The squad is in <see cref="MlTeamPlayer"/>.
/// </summary>
public sealed class MlTeam
{
    public int Pid { get; set; }

    [MaxLength(64)]
    public string Name { get; set; } = "";

    [MaxLength(8)]
    public string ShortName { get; set; } = "";

    /// <summary>The club whose badge, kit and stadium the team uses (game team id).</summary>
    public int BaseTeamId { get; set; }

    /// <summary>CMD_SET_ML_MYTEAM_TYPE; the client's own team type value.</summary>
    [MaxLength(32)]
    public string TeamType { get; set; } = "";

    public long Funds { get; set; }

    /// <summary>Division points: 3 a win, 1 a draw (ML rankings, DIVISION_POINT).</summary>
    public int Points { get; set; }

    /// <summary>0..4 = ML_DIVISION_5..ML_DIVISION_1.</summary>
    public int Division { get; set; }

    /// <summary>The division the player was last told about (CMD_GET_ML_DIVISIONUPDATE).</summary>
    public int AnnouncedDivision { get; set; }

    /// <summary>Elo-like rating of ML matches (ml_rating).</summary>
    public int Rating { get; set; } = 1000;

    public int Wins { get; set; }
    public int Draws { get; set; }
    public int Losses { get; set; }

    /// <summary>Current run of wins (con_win).</summary>
    public int WinRun { get; set; }

    /// <summary>Market purchases left in the current period, and when that period started (Unix seconds).</summary>
    public int BuysLeft { get; set; }
    public long BuyPeriodStart { get; set; }

    /// <summary>The squad's market value at the last weekly roll (total_price_old).</summary>
    public long PreviousSquadValue { get; set; }

    /// <summary>Ledger of the last ML match (CMD_GET_ML_MATCHINCOME_INFO / CMD_GET_ML_MATCHEXPENSE).</summary>
    public long LastIncome { get; set; }
    public long LastPrizeIncome { get; set; }
    public long LastDerbyIncome { get; set; }
    public long LastExpense { get; set; }
    public int LastYellowCards { get; set; }
    public int LastRedCards { get; set; }
    public int LastPurchases { get; set; }

    /// <summary>Rival Rank week the weekly bonus was last paid for (Unix seconds of the week start).</summary>
    public long RivalBonusWeek { get; set; }

    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

/// <summary>A footballer in a Master League Online squad (the game's gameplayer_id).</summary>
public sealed class MlTeamPlayer
{
    public int Id { get; set; }

    public int Pid { get; set; }

    public int GamePlayerId { get; set; }

    public int UniformNumber { get; set; }

    /// <summary>Matches left on the contract.</summary>
    public int ContractTerm { get; set; }

    /// <summary>What the club paid for him (buy_price); 0 for the starting squad.</summary>
    public long BuyPrice { get; set; }

    /// <summary>0..100, rises when he plays and recovers between matches.</summary>
    public int Tiredness { get; set; }

    public int Games { get; set; }
    public int Goals { get; set; }
    public int Assists { get; set; }

    /// <summary>Sum of the match ratings the manager gave him, and how many (average = sum / count).</summary>
    public int RatingSum { get; set; }
    public int RatingCount { get; set; }

    /// <summary>Last rating given (CMD_SET_ML_MYTEAM_PLAYER_RATING), 0 = none.</summary>
    public int LastRating { get; set; }

    public int YellowCards { get; set; }
    public int RedCards { get; set; }

    /// <summary>Matches he still misses through injury (injury.mcount_left); 0 = fit.</summary>
    public int InjuryMatches { get; set; }

    public long JoinedAt { get; set; }
}

/// <summary>A footballer on a manager's watch list (CMD_ADD_ML_MARKET_CHECKLIST).</summary>
public sealed class MlChecklistItem
{
    public int Id { get; set; }
    public int Pid { get; set; }
    public int GamePlayerId { get; set; }
    public long AddedAt { get; set; }
}

/// <summary>A footballer's market price on a day (ML_GAMEPLAYERHISTORY, market rankings).</summary>
public sealed class MlPricePoint
{
    public int Id { get; set; }
    public int GamePlayerId { get; set; }

    /// <summary>Unix seconds of the day (UTC midnight).</summary>
    public long Day { get; set; }

    public long Price { get; set; }
    public int Owners { get; set; }
}

/// <summary>Something that happened to a club (CMD_GET_ML_EVENTLIST) and the news line it makes.</summary>
public sealed class MlEvent
{
    public int Id { get; set; }
    public int Pid { get; set; }

    /// <summary>TEAM_CREATE, TEAM_NAME, TEAM_RESET, BUY, SELL, COMPE, RIVAL_RANK_CHAMP.</summary>
    [MaxLength(24)]
    public string Type { get; set; } = "";

    public int GamePlayerId { get; set; }
    public int CompeId { get; set; }

    /// <summary>The manager's comment (CMD_SET_ML_NEWS_COMMENT_*), shown in the COMMENT news.</summary>
    [MaxLength(256)]
    public string Comment { get; set; } = "";

    [MaxLength(128)]
    public string Text { get; set; } = "";

    public long At { get; set; }
}
