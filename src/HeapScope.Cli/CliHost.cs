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
    public static IHost Build(string? attachPipe)
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
        builder.Logging.AddProvider(new FileLoggerProvider(builder.Configuration["Logging:File:Directory"]));

        builder.Services.AddHeapScopeApplication();
        builder.Services.Configure<WorkerHostOptions>(builder.Configuration.GetSection(WorkerHostOptions.SectionName));
        if (!string.IsNullOrWhiteSpace(attachPipe))
        {
            builder.Services.PostConfigure<WorkerHostOptions>(o => o.AttachPipeName = attachPipe);
        }

        return builder.Build();
    }
}
