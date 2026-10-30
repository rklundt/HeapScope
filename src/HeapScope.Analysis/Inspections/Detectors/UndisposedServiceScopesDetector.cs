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
/// Undisposed service scopes (sprint 2.6, ASP.NET Core): at least <see cref="MinScopes"/> reachable dependency injection
/// scopes not disposed, the root scope left out. A scope created with CreateScope and never disposed keeps its scoped and
/// disposable services.
/// </summary>
internal sealed class UndisposedServiceScopesDetector : IInspection
{
    public const long MinScopes = 500;

    public const string ScopeType = "Microsoft.Extensions.DependencyInjection.ServiceLookup.ServiceProviderEngineScope";

    public string Name => "undisposed-service-scopes";

    public string Title => "Undisposed service scopes";

    public string Cause => "Dependency injection scopes are created and never disposed: each keeps its scoped services, and every disposable it resolved, alive.";

    public string Fix => "Dispose every scope you create (using var scope = provider.CreateScope()), or use an async scope with await using.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.FieldValues;

    public InspectionKind Kind => InspectionKind.Leak;

    public bool UsesReachable => true;

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinScopes={MinScopes}");

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var reader = Readers.Of(context);
        var reachable = DetectorGraph.Reachable(context);
        long open = 0, closed = 0, roots = 0, unreadable = 0, bytes = 0;
        var openScopes = new List<int>();
        var type = -1;
        foreach (var obj in DetectorGraph.ReachableOf(graph, reachable, DetectorGraph.TypesNamed(graph, ScopeType), context, "Reading service scopes"))
        {
            type = graph.TypeIndices[obj];
            var address = graph.Addresses[obj];
            if (Fields.Boolean(reader, address, RuntimeLayouts.ServiceScopes.IsRootScope) == true)
            {
                roots++;
                continue;
            }

            switch (Fields.Flag(reader, address, RuntimeLayouts.ServiceScopes.Disposed))
            {
                case true:
                    closed++;
                    break;
                case false:
                    open++;
                    openScopes.Add(obj);
                    bytes += (long)graph.GetSize(obj);
                    break;
                default:
                    unreadable++;
                    break;
            }
        }

        DetectorTypeRow[] rows = open >= MinScopes ? [new DetectorTypeRow(ScopeType, string.Empty, open, bytes, ScopeType, DetectorGraph.Module(graph, type), [.. openScopes])] : [];
        var note = rows.Length > 0
            ? "Scopes of requests in flight, and of Blazor Server circuits, are open by design: compare this count with the requests in flight when the dump was taken."
            : null;
        var basis = string.Create(CultureInfo.InvariantCulture, $"{open} reachable scopes not disposed, {closed} disposed, {roots} root, {unreadable} unreadable; {Reachability.Describe(reachable)}");
        return new InspectionResult(rows.Length, rows.Sum(r => r.Bytes), null, note, DetectorTables.Types(rows, null), basis);
    }
}
