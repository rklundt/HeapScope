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

using HeapScope.Application;
using HeapScope.Application.Logging;
using HeapScope.Application.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeapScope.Cli;

/// <summary>Builds the generic host that gives CLI commands their services.</summary>
internal static class CliHost
{
    /// <param name="attachPipe">When set, the CLI attaches to a running worker instead of launching one.</param>
    /// <param name="dacFolder">When set, overrides <c>Symbols:DacFolder</c> (<c>--dac-folder</c>).</param>
    /// <param name="allowSymbolServer">When true, opts in to symbol server downloads (<c>--allow-symbol-server</c>).</param>
    public static IHost Build(string? attachPipe, string? dacFolder = null, bool allowSymbolServer = false)
    {
        // System.CommandLine owns argument parsing, so the host gets no command line arguments.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Console output belongs to command results; diagnostics go to stderr from Warning up and to the file log.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(level => level >= LogLevel.Warning);
        var fileLog = builder.Services.AddHeapScopeFileLog(builder.Configuration, "cli");
        builder.Logging.Configure(o => o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId);
        if (fileLog.MinimumLevel < LogLevel.Information)
        {
            builder.Logging.SetMinimumLevel(fileLog.MinimumLevel);
        }

        builder.Services.PostConfigure<WorkerHostOptions>(o => o.WorkerLogLevel = fileLog.MinimumLevel);

        builder.Services.AddHeapScopeApplication();
        builder.Services.Configure<WorkerHostOptions>(builder.Configuration.GetSection(WorkerHostOptions.SectionName));
        builder.Services.Configure<WorkerHostOptions>(builder.Configuration.GetSection(WorkerHostOptions.SymbolsSectionName));
        builder.Services.Configure<HeapScope.Application.Sessions.SessionOptions>(builder.Configuration.GetSection(HeapScope.Application.Sessions.SessionOptions.SectionName));
        if (!string.IsNullOrWhiteSpace(attachPipe))
        {
            builder.Services.PostConfigure<WorkerHostOptions>(o => o.AttachPipeName = attachPipe);
        }

        if (!string.IsNullOrWhiteSpace(dacFolder))
        {
            builder.Services.PostConfigure<WorkerHostOptions>(o => o.DacFolder = Path.GetFullPath(dacFolder));
        }

        if (allowSymbolServer)
        {
            builder.Services.PostConfigure<WorkerHostOptions>(o => o.AllowMicrosoftSymbolServer = true);
        }

        return builder.Build();
    }
}
