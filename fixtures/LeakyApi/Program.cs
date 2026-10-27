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
using Fixtures.Leaks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

// A small API with switchable, scalable leaks (sprint 0.4). Configuration (command line or appsettings):
//   Leaks:Enabled          all | none | comma separated leak names (default: all)
//   Leaks:ItemsPerRequest  items each enabled leak grows by on POST /work (default: 100)
//   Leaks:PayloadBytes     payload per item (default: 64)
//   Leaks:Publishers       publishers of the EventHandler leak (default: 16384)
//   Leaks:Detected         all | none | comma separated: the 2.6 detector leaks (Timer, DisposedReferenced; default: none)
//   Clean:Enabled          true to also run the clean workload on POST /work (sprint 2.6; default: false)
//   ParentPid              exit when this process exits
// Prints "READY pid=<pid> url=<first bound url>" when listening, so callers can use port 0.
var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
IReadOnlyList<ILeak> enabled =
[
    .. LeakCatalog.Resolve(builder.Configuration["Leaks:Enabled"] ?? "all"),
    .. LeakCatalog.Resolve(builder.Configuration["Leaks:Detected"] ?? "none", LeakCatalog.Detected),
];
var clean = builder.Configuration.GetValue("Clean:Enabled", false);
var itemsPerRequest = builder.Configuration.GetValue("Leaks:ItemsPerRequest", 100);
var payloadBytes = builder.Configuration.GetValue("Leaks:PayloadBytes", 64);
if (builder.Configuration["Leaks:Publishers"] is { } publishers)
{
    EventHandlerLeak.PublisherCount = int.Parse(publishers, CultureInfo.InvariantCulture);
}

if (itemsPerRequest < 0 || payloadBytes < 0)
{
    await Console.Error.WriteLineAsync("Leaks:ItemsPerRequest and Leaks:PayloadBytes must not be negative.").ConfigureAwait(false);
    return 2;
}

ParentWatch.ExitWhenParentExits(builder.Configuration.GetValue<int?>("ParentPid"));
var app = builder.Build();

app.MapGet("/health", () => Results.Ok("ok"));

app.MapPost("/work", () =>
{
    foreach (var leak in enabled)
    {
        leak.Grow(itemsPerRequest, payloadBytes);
    }

    if (clean)
    {
        CleanWorkload.Run(itemsPerRequest, payloadBytes);
    }

    return Results.Ok();
});

// Disabled leaks are never touched, not even to read their count (decision 4).
app.MapGet("/leaks", () => Results.Ok(LeakCatalog.All.Concat(LeakCatalog.Detected).Select(l => new LeakStatus(l.Name, enabled.Contains(l), enabled.Contains(l) ? l.ItemCount : 0))));

app.MapPost("/leaks/{name}/grow", (string name, int count) =>
{
    var leak = LeakCatalog.All.Concat(LeakCatalog.Detected).FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (leak is null)
    {
        return Results.NotFound($"Unknown leak '{name}'.");
    }

    if (!enabled.Contains(leak))
    {
        return Results.Conflict($"Leak '{leak.Name}' is disabled; enable it with Leaks:Enabled.");
    }

    if (count < 0)
    {
        return Results.BadRequest("count must not be negative.");
    }

    leak.Grow(count, payloadBytes);
    return Results.Ok(new LeakStatus(leak.Name, true, leak.ItemCount));
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
    Console.WriteLine($"READY pid={Environment.ProcessId} url={url}");
});

await app.RunAsync().ConfigureAwait(false);
return 0;

internal sealed record LeakStatus(string Name, bool Enabled, int Items);
