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
using HeapScope.Analysis.Retention;
using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Analysis.Inspections;

/// <summary>
/// Many HttpClient instances (sprint 2.6): reachable <c>HttpClient</c>s that own their handler, over at least
/// <see cref="MinInstances"/> distinct handlers, and so as many connection pools. Clients sharing a handler (IHttpClientFactory, or a handler passed in
/// with disposeHandler false) are the recommended pattern and are not counted (review M11).
/// </summary>
internal sealed class HttpClientInstancesDetector : IInspection
{
    public const long MinInstances = 20;

    private const string ClientType = "System.Net.Http.HttpClient";

    public string Name => "httpclient-instances";

    public string Title => "Many HttpClient instances";

    public string Cause => "Many HttpClients each own their handler: every handler has its own connection pool, so creating clients per use wastes memory and sockets.";

    public string Fix => "Create one HttpClient and reuse it, or use IHttpClientFactory (AddHttpClient) to share handlers.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.FieldValues;

    public InspectionKind Kind => InspectionKind.Leak;

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinInstances={MinInstances};Counted=owns its handler");

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var reader = Readers.Of(context);
        var reachable = Reachability.For(graph, context.CancellationToken);
        long owning = 0, sharing = 0, unreadable = 0, bytes = 0;
        var handlers = new HashSet<ulong>();
        var clients = new List<int>();
        var type = -1;
        foreach (var obj in DetectorGraph.ReachableOf(graph, reachable, DetectorGraph.TypesNamed(graph, ClientType), context, "Counting HTTP clients"))
        {
            type = graph.TypeIndices[obj];
            var address = graph.Addresses[obj];
            switch (Fields.Boolean(reader, address, RuntimeLayouts.HttpClients.DisposeHandler))
            {
                case true:
                    // new HttpClient(shared) owns a handler it shares: distinct handlers are the pools (re-review N2).
                    owning++;
                    clients.Add(obj);
                    bytes += (long)graph.GetSize(obj);
                    handlers.Add(Fields.Reference(reader, address, RuntimeLayouts.HttpClients.Handler)?.Address ?? address);
                    break;
                case false:
                    sharing++;
                    break;
                default:
                    unreadable++;
                    break;
            }
        }

        DetectorTypeRow[] rows = handlers.Count >= MinInstances
            ? [new DetectorTypeRow(ClientType, InspectionRows.Number(handlers.Count), owning, bytes, ClientType, DetectorGraph.Module(graph, type), [.. clients])]
            : [];
        var basis = string.Create(CultureInfo.InvariantCulture, $"{owning} reachable HttpClients owning their handler ({handlers.Count} distinct handlers), {sharing} sharing one, {unreadable} unreadable; {Reachability.Describe(reachable)}");
        return new InspectionResult(rows.Length, rows.Sum(r => r.Bytes), null, null, DetectorTables.Types(rows, "Handlers"), basis);
    }
}
