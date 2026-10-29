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

using HeapScope.Core.Loading;

namespace HeapScope.Loaders.ClrMd;

/// <summary>Options of the full dump loader (ARCHITECTURE.md 6.5, ADR-017).</summary>
public sealed record ClrMdLoaderOptions
{
    /// <summary>Default cap of ClrMD's dump data cache, measured in sprint 0.4 (same speed, half the memory).</summary>
    public const long DefaultMaxDumpCacheBytes = 512L * 1024 * 1024;

    /// <summary>Upper bound of ClrMD's dump data cache in bytes.</summary>
    public long MaxDumpCacheBytes { get; init; } = DefaultMaxDumpCacheBytes;

    /// <summary>A user folder searched for the data access component (DAC), configuration <c>Symbols:DacFolder</c>.</summary>
    public string? DacFolder { get; init; }

    /// <summary>
    /// Opt-in (configuration <c>Symbols:AllowMicrosoftSymbolServer</c>, default off): when no local DAC matches, let
    /// ClrMD download it from Microsoft's symbol server. Sends module identifiers, never dump contents.
    /// </summary>
    public bool AllowMicrosoftSymbolServer { get; init; }

    /// <summary>
    /// Folders holding installed shared runtimes (<c>...\shared\Microsoft.NETCore.App</c>). Null means the machine
    /// defaults (<c>DOTNET_ROOT</c> and Program Files); tests pass their own.
    /// </summary>
    public IReadOnlyList<string>? InstalledRuntimeRoots { get; init; }

    /// <summary>
    /// Whether the dumped runtime's own folder (a path recorded in the dump) is searched first. Always true in the
    /// product; tests turn it off to simulate a dump from another machine.
    /// </summary>
    public bool SearchDumpedRuntimeDirectory { get; init; } = true;

    /// <summary>
    /// The folder of the runtime HeapScope carries when it runs self-contained (sprint 2.3.6), searched last; null when it
    /// runs on a shared runtime, which is an installed runtime already. Tests pass their own.
    /// </summary>
    public string? OwnRuntimeDirectory { get; init; } = DacLocator.RunningSharedFolder() is null ? AppContext.BaseDirectory : null;

    /// <summary>
    /// Told which DAC each runtime was created with, by the loader and by the field reader, which opens the dump again
    /// later (sprint 2.3.7 review M3). The worker logs it; the loaders stay free of logging.
    /// </summary>
    public Action<DacChoice>? DacChosen { get; init; }
}
