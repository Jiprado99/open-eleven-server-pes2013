// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Server.Web;

/// <summary>
/// The client-side helper scripts the /tools page offers. They are the repository's
/// <c>tools/</c> files, copied next to the binary at build and publish time (see the csproj).
/// </summary>
public static class ToolDownloads
{
    /// <summary>Published file name to what the /tools page says about it.</summary>
    public static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["run-pes2013-radmin.bat"] = "Lanzador de doble clic: inicia el juego con ForceBindIP usando tu dirección de Radmin.",
        ["run-pes2013-radmin.ps1"] = "El script que ejecuta el lanzador. Debe estar en la misma carpeta que el .bat.",
    };

    /// <summary>The file on disk for a published name, or null when the name is not one of <see cref="Files"/> or is missing.</summary>
    public static string? Find(string name)
    {
        if (!Files.ContainsKey(name))
            return null;
        var path = Path.Combine(AppContext.BaseDirectory, "tools", name);
        return File.Exists(path) ? path : null;
    }
}
