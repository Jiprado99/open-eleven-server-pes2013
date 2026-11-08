// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.MasterLeague;

/// <summary>
/// The Master League Online economy: prices, salaries, contracts, team power and divisions.
/// Pure functions of <see cref="MasterLeagueOptions"/>, so they are easy to test and tune.
/// ponytail: the original service's formulas are unknown; these are invented to be sensible.
/// </summary>
public sealed class MlRules(MasterLeagueOptions o)
{
    public MasterLeagueOptions Options => o;

    /// <summary>Price of a footballer with no owners: PriceAt75 × 1.25^(overall − 75), rounded to thousands.</summary>
    public long BasePrice(int overall)
        => RoundThousands(o.PriceAt75 * Math.Pow(1.25, overall - 75));

    /// <summary>Market price: base price raised by demand (clubs that own him).</summary>
    public long MarketPrice(int overall, int owners)
    {
        var demand = Math.Min(o.MaxDemandPercent, Math.Max(0, owners) * o.PricePercentPerOwner);
        return RoundThousands(BasePrice(overall) * (100 + demand) / 100.0);
    }

    public long SellPrice(long marketPrice) => RoundThousands(marketPrice * o.SellPercent / 100.0);

    /// <summary>Salary per match.</summary>
    public long Salary(long marketPrice) => Math.Max(1_000, RoundThousands(marketPrice * o.SalaryPerMille / 1000.0));

    /// <summary>Base salary per match of a footballer with no owners (contract_price_base).</summary>
    public long BaseSalary(int overall) => Salary(BasePrice(overall));

    /// <summary>Cost of each extension type (CONTRACT_TYPE1..3): salary × matches, 5% and 10% off the longer ones.</summary>
    public long[] ContractPrices(long marketPrice)
    {
        var salary = Salary(marketPrice);
        return o.ExtendMatchesOrDefault().Select((matches, i) =>
            RoundThousands(salary * matches * (100 - 5 * i) / 100.0)).ToArray();
    }

    /// <summary>CONTRACT_TYPE1..3 → index 0..2, or -1.</summary>
    public static int ContractIndex(string? type) => type switch
    {
        "CONTRACT_TYPE1" => 0,
        "CONTRACT_TYPE2" => 1,
        "CONTRACT_TYPE3" => 2,
        _ => -1,
    };

    /// <summary>Team power: mean overall of the best eleven.</summary>
    public static int TeamPower(IEnumerable<int> overalls)
    {
        var best = overalls.OrderByDescending(x => x).Take(11).ToList();
        return best.Count == 0 ? 0 : (int)Math.Round(best.Average());
    }

    /// <summary>0..4 (ML_DIVISION_5..1) from the division points.</summary>
    public int DivisionFor(int points)
    {
        var division = 0;
        for (var i = 0; i < o.DivisionPointsOrDefault().Length && i < 4; i++)
            if (points >= o.DivisionPointsOrDefault()[i])
                division = i + 1;
        return division;
    }

    public static string DivisionLabel(int division) => $"ML_DIVISION_{5 - Math.Clamp(division, 0, 4)}";

    /// <summary>The skill_level enum of the lobbies, from the division.</summary>
    public static string SkillLevel(int division) => Math.Clamp(division, 0, 4) switch
    {
        0 => "BEGINNER",
        1 => "AMATEUR",
        2 => "STANDARD",
        3 => "PROFESSIONAL",
        _ => "TOPPLAYER",
    };

    /// <summary>Condition from tiredness (ml_condition, the client's spelling).</summary>
    public static string Condition(int tiredness) => tiredness switch
    {
        < 10 => "EXCELENT",
        < 30 => "GOOD",
        < 55 => "NORMAL",
        < 80 => "POOR",
        _ => "TERRIBLE",
    };

    public static long RoundThousands(double value) => (long)Math.Round(value / 1000.0) * 1000;
}
