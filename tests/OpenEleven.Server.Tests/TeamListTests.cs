// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.Tests;

/// <summary>PES 2013's team list (from the game's own teamlist.txt) and the operator's Include / Exclude.</summary>
public class TeamListTests
{
    private static int[] Teams2013(TeamOptions? options = null)
        => TitleProfiles.UseableTeams(GameProfile.Pes2013Pc, options);

    [Fact]
    public void The_2013_list_has_the_national_and_classic_teams_the_2011_2012_guess_missed()
    {
        var teams = Teams2013();

        Assert.Contains(1083, teams);   // Zambia
        Assert.Contains(1113, teams);   // Panama
        Assert.Contains(1009, teams);   // Indonesia
        Assert.Contains(1017, teams);   // Malaysia
        Assert.Equal([902, 903, 904, 905, 906, 907, 908], teams.Where(t => t is >= 902 and <= 908).Order());
        Assert.Contains(105, teams);    // West Ham United
    }

    [Fact]
    public void Placeholders_and_internal_master_league_teams_are_not_offered()
    {
        var teams = Teams2013();

        Assert.DoesNotContain(58, teams);                        // (null)
        Assert.DoesNotContain(140, teams);                       // MASTERLEAGUE DEFAULT
        Assert.DoesNotContain(141, teams);                       // MASTERLEAGUE HIDE1
        Assert.DoesNotContain(2511, teams);                      // ML NEWFACE EURO
        Assert.DoesNotContain(2470, teams);                      // TERZAMOC
        Assert.DoesNotContain(1261, teams);                      // (null)
        Assert.DoesNotContain(2504, teams);                      // (null)
    }

    [Fact]
    public void National_teams_come_first_so_the_default_pick_is_a_real_national_team()
        => Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Teams2013().Take(5));

    [Fact]
    public void Include_goes_first_and_survives_the_400_limit_and_exclude_removes()
    {
        var built = Teams2013();
        Assert.Equal(TitleProfiles.MaxUseableTeams, built.Length);

        var changed = Teams2013(new TeamOptions { Include = [9000, 9001, 5], Exclude = [1, 2] });

        Assert.Equal(new[] { 9000, 9001, 5 }, changed.Take(3));     // 5 is already built in: no duplicate later
        Assert.DoesNotContain(1, changed);
        Assert.DoesNotContain(2, changed);
        Assert.Equal(changed.Length, changed.Distinct().Count());
        Assert.InRange(changed.Length, 1, TitleProfiles.MaxUseableTeams);
    }

    [Fact]
    public void Without_changes_the_built_in_list_is_returned_as_is()
        => Assert.Same(TitleProfiles.UseableTeams(GameProfile.Pes2013Pc), Teams2013(new TeamOptions()));
}
