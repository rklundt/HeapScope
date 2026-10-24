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

using System.Reflection;

namespace HeapScope.Contracts;

/// <summary>Product name and version shared by every HeapScope executable.</summary>
public static class ProductInfo
{
    public const string Name = "HeapScope";

    /// <summary>The project repository, the target of the attribution link.</summary>
    public const string RepositoryUrl = "https://github.com/rklundt/HeapScope";

    /// <summary>
    /// The Attribution Notice that ADDITIONAL-TERMS.md section 1 requires on the main screen and in CLI help, version
    /// and startup output. Where "HeapScope" cannot be a link, <see cref="AttributionWithUrl"/> shows the URL beside it.
    /// </summary>
    public const string AttributionPrefix = "Powered by ";
    public const string AttributionSuffix = " by Ray Klundt";
    public const string Attribution = AttributionPrefix + Name + AttributionSuffix;
    public const string AttributionWithUrl = Attribution + " (" + RepositoryUrl + ")";

    /// <summary>The informational version of the HeapScope build, without source revision metadata.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return typeof(ProductInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
