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

using HeapScope.Application.Snapshots;
using HeapScope.Contracts;

namespace HeapScope.UI.ViewModels;

/// <summary>
/// Where a link leads (sprint 2.4, 2.3.7 review M8): every cross-link of the app (a type row, a diff row, an inspection
/// row, a reference, a dominator node) is one of these, handed to <see cref="INavigator.Navigate"/>, which picks the tab
/// and the subject. Use the factory methods.
/// </summary>
public sealed record NavigationTarget(
    NavigationKind Kind,
    SnapshotHandle Snapshot,
    ulong Address = 0,
    string? TypeName = null,
    string? Module = null,
    InstanceScope Scope = InstanceScope.All,
    ulong SameValueAs = 0,
    string? NodeKey = null)
{
    public static NavigationTarget Object(SnapshotHandle snapshot, ulong address) => new(NavigationKind.Object, snapshot, Address: address);

    public static NavigationTarget Instances(SnapshotHandle snapshot, string typeName, string? module, InstanceScope scope = InstanceScope.All, ulong sameValueAs = 0) =>
        new(NavigationKind.Instances, snapshot, TypeName: typeName, Module: module, Scope: scope, SameValueAs: sameValueAs);

    public static NavigationTarget TypeReferrers(SnapshotHandle snapshot, string typeName, string? module) =>
        new(NavigationKind.TypeReferrers, snapshot, TypeName: typeName, Module: module);

    public static NavigationTarget TypePaths(SnapshotHandle snapshot, string typeName, string? module) =>
        new(NavigationKind.TypePaths, snapshot, TypeName: typeName, Module: module);

    /// <summary>The dominator tree with <paramref name="nodeKey"/> shown and selected; null for the biggest retainers.</summary>
    public static NavigationTarget Retainers(SnapshotHandle snapshot, string? nodeKey = null, ulong address = 0) =>
        new(NavigationKind.Retainers, snapshot, Address: address, NodeKey: nodeKey);

    public static NavigationTarget RetainedBySet(SnapshotHandle snapshot, string typeName, string? module) =>
        new(NavigationKind.RetainedBySet, snapshot, TypeName: typeName, Module: module);
}
