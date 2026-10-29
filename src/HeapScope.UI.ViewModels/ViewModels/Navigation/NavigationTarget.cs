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
    string? NodeKey = null,
    ObjectReferenceTabs ObjectTab = ObjectReferenceTabs.Outgoing,
    ObjectSetQuery? Set = null,
    string? SetTitle = null,
    SetViews SetView = SetViews.Types)
{
    /// <summary>One object, with <paramref name="tab"/> shown in its references panel.</summary>
    public static NavigationTarget Object(SnapshotHandle snapshot, ulong address, ObjectReferenceTabs tab = ObjectReferenceTabs.Outgoing) =>
        new(NavigationKind.Object, snapshot, Address: address, ObjectTab: tab);

    public static NavigationTarget Instances(SnapshotHandle snapshot, string typeName, string? module, InstanceScope scope = InstanceScope.All, ulong sameValueAs = 0) =>
        new(NavigationKind.Instances, snapshot, TypeName: typeName, Module: module, Scope: scope, SameValueAs: sameValueAs);

    public static NavigationTarget TypeReferrers(SnapshotHandle snapshot, string typeName, string? module) =>
        new(NavigationKind.TypeReferrers, snapshot, TypeName: typeName, Module: module);

    public static NavigationTarget TypePaths(SnapshotHandle snapshot, string typeName, string? module) =>
        new(NavigationKind.TypePaths, snapshot, TypeName: typeName, Module: module);

    /// <summary>
    /// The dominator tree: below the object at <paramref name="address"/> (with its dominators as a trail), or the biggest
    /// retainers grouped by type with <paramref name="typeName"/>'s group open, or (with neither) the biggest retainers.
    /// </summary>
    public static NavigationTarget Retainers(SnapshotHandle snapshot, ulong address = 0, string? typeName = null, string? module = null) =>
        new(NavigationKind.Retainers, snapshot, Address: address, TypeName: typeName, Module: module,
            NodeKey: address != 0 ? DominatorTreeKeys.Object(address) : typeName is null ? null : DominatorTreeKeys.TypeGroup(DominatorTreeKeys.Root, typeName, module));

    public static NavigationTarget RetainedBySet(SnapshotHandle snapshot, string typeName, string? module) =>
        new(NavigationKind.RetainedBySet, snapshot, TypeName: typeName, Module: module);

    /// <summary>What the members of any object set retain together (sprint 3.1), on the Retainers tab.</summary>
    public static NavigationTarget RetainedBySet(SnapshotHandle snapshot, ObjectSetQuery set, string title) =>
        new(NavigationKind.RetainedBySet, snapshot, Set: set, SetTitle: title);

    /// <summary>
    /// An object set with its views (sprint 3.1), titled <paramref name="title"/> in the analysis path, showing
    /// <paramref name="view"/> first (sprint 3.2).
    /// </summary>
    public static NavigationTarget ObjectSet(SnapshotHandle snapshot, ObjectSetQuery set, string title, SetViews view = SetViews.Types) =>
        new(NavigationKind.Set, snapshot, Set: set, SetTitle: title, SetView: view);

    /// <summary>The members of a set (of one type when <paramref name="typeName"/> is given), as instances (sprint 3.1).</summary>
    public static NavigationTarget SetInstances(SnapshotHandle snapshot, ObjectSetQuery set, string title, string? typeName = null, string? module = null) =>
        new(NavigationKind.Instances, snapshot, TypeName: typeName ?? string.Empty, Module: module, Set: set, SetTitle: title);
}
