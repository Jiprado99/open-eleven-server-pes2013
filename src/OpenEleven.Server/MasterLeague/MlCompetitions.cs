// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Server.MasterLeague;

/// <summary>Master League competitions (COMPLG with is_ml_compe): entry state the market checks.</summary>
public static class MlCompetitions
{
    /// <summary>Whether the club is entered in a running ML competition (transfers are frozen: ERR_COMPE_ENTRY).</summary>
    public static Task<bool> IsEnteredAsync(MlLeague league, int pid, CancellationToken ct) => Task.FromResult(false);
}
