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


using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HeapScope.Application.Logging;

/// <summary>The file log of a front end, set up the same way by the UI and the CLI (ARCHITECTURE.md 11, sprint 2.3.7).</summary>
public static class FileLogging
{
    /// <summary>Configuration key of the log folder.</summary>
    public const string DirectoryKey = "Logging:File:Directory";

    /// <summary>Configuration key of the lowest level written (also passed to the worker).</summary>
    public const string MinimumLevelKey = "Logging:File:MinimumLevel";

    /// <summary>
    /// Adds this process's <see cref="FileLoggerProvider"/>, also registered as a service so messages and the About page
    /// can name its folder. The host also turns on the trace id as a scope (<c>ActivityTrackingOptions.TraceId</c>) and
    /// lowers the logging minimum to <see cref="FileLoggerProvider.MinimumLevel"/> when it is below Information; those
    /// live in Microsoft.Extensions.Logging, which this layer does not reference.
    /// </summary>
    public static FileLoggerProvider AddHeapScopeFileLog(this IServiceCollection services, IConfiguration configuration, string app)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var provider = new FileLoggerProvider(configuration[DirectoryKey], FileLoggerProvider.ParseLevel(configuration[MinimumLevelKey]), app: app);
        services.AddSingleton<ILoggerProvider>(provider);
        services.AddSingleton(provider);
        return provider;
    }
}
