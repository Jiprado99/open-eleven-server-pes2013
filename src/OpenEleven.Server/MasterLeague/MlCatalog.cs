// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.MasterLeague;

/// <summary>
/// The footballers and squads Master League Online works with, loaded on first use: from the cache
/// file when there is one, else from the game installation (and then cached). The data is Konami's,
/// so it is never part of the repository. Without either, a placeholder starting squad keeps club
/// creation working and every unknown footballer counts as overall 70.
/// </summary>
public sealed class MlCatalog
{
    /// <summary>The ids of the game's MASTERLEAGUE DEFAULT squad (team 140), used when the game cannot be read.</summary>
    private static readonly int[] FallbackSquad = [.. Enumerable.Range(55244, 28)];

    private readonly Lazy<MlCatalogData> data;

    public MlCatalog(IOptions<ServerOptions> options, ILogger<MlCatalog> log)
        : this(() => Load(options.Value.MasterLeague, log))
    {
    }

    public MlCatalog(MlCatalogData fixedData) : this(() => fixedData)
    {
    }

    private MlCatalog(Func<MlCatalogData> load)
    {
        data = new Lazy<MlCatalogData>(() =>
        {
            var d = load();
            players = d.Players.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
            teams = d.Teams.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
            return d;
        });
    }

    private Dictionary<int, MlFootballer> players = [];
    private Dictionary<int, MlSquad> teams = [];

    public bool IsLoaded => Data.Players.Count > 0;

    private MlCatalogData Data => data.Value;

    public IReadOnlyCollection<MlFootballer> Players { get { _ = Data; return players.Values; } }

    public bool Exists(int id) { _ = Data; return players.ContainsKey(id) || !IsLoaded; }

    public MlFootballer Player(int id)
    {
        _ = Data;
        return players.TryGetValue(id, out var p) ? p : new MlFootballer(id, $"#{id}", 70, false);
    }

    public bool HasTeam(int id) { _ = Data; return teams.ContainsKey(id) || !IsLoaded; }

    public MlSquad? Team(int id) { _ = Data; return teams.GetValueOrDefault(id); }

    /// <summary>The squad a new club starts with.</summary>
    public IReadOnlyList<MlSquadMember> StartingSquad(int teamId)
    {
        if (Team(teamId) is { Members.Count: > 0 } squad)
            return squad.Members;
        return FallbackSquad.Select((id, i) => new MlSquadMember(id, i + 1)).ToList();
    }

    private static MlCatalogData Load(MasterLeagueOptions options, ILogger log)
    {
        var cache = Path.IsPathRooted(options.CatalogCache)
            ? options.CatalogCache
            : Path.Combine(AppContext.BaseDirectory, options.CatalogCache);
        try
        {
            if (File.Exists(cache))
            {
                var cached = JsonSerializer.Deserialize<MlCatalogData>(File.ReadAllText(cache)) ?? new MlCatalogData();
                log.LogInformation("Master League catalogue: {Players} footballers, {Teams} squads from {File}",
                    cached.Players.Count, cached.Teams.Count, cache);
                return cached;
            }

            if (!Directory.Exists(Path.Combine(options.GameDirectory, "img")))
            {
                log.LogWarning("Master League catalogue: no cache at {Cache} and no game at {Game}; using the placeholder squad",
                    cache, options.GameDirectory);
                return new MlCatalogData();
            }

            var read = GameDataReader.Read(options.GameDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            File.WriteAllText(cache, JsonSerializer.Serialize(read));
            log.LogInformation("Master League catalogue: read {Players} footballers and {Teams} squads from {Game}, cached in {File}",
                read.Players.Count, read.Teams.Count, options.GameDirectory, cache);
            return read;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            log.LogError(e, "Master League catalogue could not be loaded; using the placeholder squad");
            return new MlCatalogData();
        }
    }
}
