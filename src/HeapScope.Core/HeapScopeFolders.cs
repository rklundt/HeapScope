// HeapScope
// Copyright (C) 2026 Ray Klundt
// https://github.com/rklundt/HeapScope
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, version 3 only, with additional terms
// under Section 7 of that license.
//
// Additional terms, including attribution requirements, apply.
// See the ADDITIONAL-TERMS.md file distributed with this program.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program (see the LICENSE file). If not, see
// <https://www.gnu.org/licenses/>.
//
// Commercial licenses are available. See COMMERCIAL.md.

namespace HeapScope.Core;

/// <summary>
/// HeapScope's own data folders, named in one place: the session library, logs, the index cache fallback, private DAC
/// copies, trace conversion, and the files at the root (the index cache key, trusted DACs, bookmarks). The root is
/// <c>%LOCALAPPDATA%\HeapScope</c> (the platform's local application data folder elsewhere), or the folder
/// <see cref="EnvironmentVariable"/> names, which every process HeapScope starts inherits; the worker also takes
/// <c>--data-folder</c>. Tests set it so nothing they do reaches the user's own folders.
/// </summary>
public sealed class HeapScopeFolders
{
    /// <summary>Names another root for every HeapScope process started with it in its environment.</summary>
    public const string EnvironmentVariable = "HEAPSCOPE_DATA_FOLDER";

    private readonly bool _isDefault;

    /// <summary>Folders under <paramref name="root"/>, named by <paramref name="source"/> (for example <c>--data-folder</c>).</summary>
    public HeapScopeFolders(string root, string source = "an option")
        : this(root, source, isDefault: false)
    {
    }

    private HeapScopeFolders(string root, string source, bool isDefault)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        Source = source;
        _isDefault = isDefault;
    }

    /// <summary>
    /// The folders this process uses unless told otherwise, read once at its start. A value of only spaces counts as unset
    /// (3.8 review): a malformed variable must not stop every HeapScope process before it can log anything.
    /// </summary>
    public static HeapScopeFolders Default { get; } = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable))
        ? new HeapScopeFolders(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeapScope"), "the default", isDefault: true)
        : new HeapScopeFolders(Environment.GetEnvironmentVariable(EnvironmentVariable)!, EnvironmentVariable);

    /// <summary>Where <see cref="Root"/> came from, for the start lines: the default, the environment variable or an option.</summary>
    public string Source { get; }

    /// <summary>The root: the index cache key, the trusted DACs and the bookmarks live here.</summary>
    public string Root { get; }

    /// <summary>The session library's default root.</summary>
    public string Sessions => Path.Combine(Root, "sessions");

    /// <summary>The front ends' log files.</summary>
    public string Logs => Path.Combine(Root, "logs");

    /// <summary>Index caches of snapshots in folders HeapScope cannot write.</summary>
    public string Cache => Path.Combine(Root, "cache");

    /// <summary>Private copies of trusted DACs, one folder per SHA-256 (ADR-035).</summary>
    public string DacCopies => Path.Combine(Root, "dac");

    /// <summary>
    /// TraceEvent's <c>.etlx</c> files while a trace is read: the user's own temporary folder on Windows, HeapScope's
    /// data folder elsewhere, where the temporary folder is shared by every user (peer review P15); under another root,
    /// that root.
    /// </summary>
    public string Etlx => _isDefault && OperatingSystem.IsWindows()
        ? Path.Combine(Path.GetTempPath(), "HeapScope", "etlx")
        : Path.Combine(Root, "etlx");
}
