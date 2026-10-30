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

namespace HeapScope.Loaders.ClrMd;

/// <summary>
/// The data access component (DAC) matching the dumped runtime was not found locally and downloads are off
/// (ADR-017). The message names the runtime version and how to provide the file.
/// </summary>
public sealed class RuntimeComponentMissingException : Exception
{
    public RuntimeComponentMissingException()
    {
    }

    public RuntimeComponentMissingException(string message)
        : base(message)
    {
    }

    public RuntimeComponentMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="hint">What to do first when installing the runtime would not help (sprint 2.3.5), or null.</param>
    public RuntimeComponentMissingException(string runtimeVersion, string fileName, IReadOnlyList<string> searched, string? hint = null)
        : base(BuildMessage(runtimeVersion, fileName, searched, hint))
    {
        RuntimeVersion = runtimeVersion;
        FileName = fileName;
        Searched = searched;
    }

    public string? RuntimeVersion { get; }

    public string? FileName { get; }

    public IReadOnlyList<string> Searched { get; } = [];

    private static string BuildMessage(string runtimeVersion, string fileName, IReadOnlyList<string> searched, string? hint)
    {
        // ClrMD reads some core files' runtime version as 0.0 (sprint 2.3.5); such a version is not named.
        var known = !runtimeVersion.StartsWith("0.0", StringComparison.Ordinal);
        var runtime = known ? $".NET {runtimeVersion}" : "a .NET runtime whose version it does not record";
        return $"This dump was taken from {runtime}, and the matching {fileName} was not found on this machine. " +
            (hint is null ? "" : hint + " ") +
            $"Install {(known ? $"the .NET {runtimeVersion} runtime" : "that runtime")}, or copy {fileName} from that runtime's folder into a folder given with --dac-folder (CLI) or the Symbols:DacFolder setting (a copy next to the dump is used once you trust it). " +
            "Downloading it from Microsoft's symbol server is off by default; it can be turned on with --allow-symbol-server (CLI) or the Symbols:AllowMicrosoftSymbolServer setting. " +
            $"Searched: {(searched.Count == 0 ? "nothing" : string.Join("; ", searched))}.";
    }
}
