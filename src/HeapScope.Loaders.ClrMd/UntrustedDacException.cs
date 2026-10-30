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
/// The only DAC matching the dumped runtime lies next to the dump, and nobody has trusted it (peer review P2). A DAC is
/// native code that runs in the worker; one that came with a dump is the dump maker's code. The message names the file
/// and its SHA-256, which the user trusts (the app asks; the CLI takes <c>--trust-dac</c>) before it is loaded.
/// </summary>
public sealed class UntrustedDacException : Exception
{
    public UntrustedDacException()
    {
    }

    public UntrustedDacException(string message)
        : base(message)
    {
    }

    public UntrustedDacException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UntrustedDacException(string path, string sha256, string runtimeVersion)
        : base($"The .NET {runtimeVersion} runtime's data access component (DAC) was found only next to the dump, at {path} (SHA-256 {sha256}). " +
            "A DAC is native code HeapScope runs to read the dump, and one that came with a dump runs code from whoever made it. " +
            $"Trust it only if you know where it came from: answer the question when opening the dump, or add --trust-dac {sha256} on the command line. " +
            "Otherwise install that runtime, or put its DAC in the Symbols:DacFolder folder.")
    {
        Path = path;
        Sha256 = sha256;
        RuntimeVersion = runtimeVersion;
    }

    public string? Path { get; }

    public string? Sha256 { get; }

    public string? RuntimeVersion { get; }
}
