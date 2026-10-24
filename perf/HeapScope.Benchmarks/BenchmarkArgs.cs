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

using System.Globalization;

namespace HeapScope.Benchmarks;

/// <summary>Minimal "--name value" argument reader for benchmarks.</summary>
internal sealed class BenchmarkArgs(string[] args)
{
    public string? Get(string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], "--" + name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    public int GetInt(string name, int fallback) =>
        Get(name) is { } value ? int.Parse(value.Replace("_", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture) : fallback;

    public double GetDouble(string name, double fallback) =>
        Get(name) is { } value ? double.Parse(value, CultureInfo.InvariantCulture) : fallback;
}
