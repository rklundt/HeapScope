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

using CommunityToolkit.Mvvm.ComponentModel;
using HeapScope.Contracts;
using HeapScope.UI.Services;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;

/// <summary>The type grids' layer over the query core (sprint 1.5 split): grouping by type, namespace or assembly.</summary>
public abstract partial class TypeGridViewModel<TRow> : GridQueryViewModel<TRow>
    where TRow : class, IIndexedRow
{
    [ObservableProperty]
    private TypeGrouping _groupBy = TypeGrouping.Type;

    protected TypeGridViewModel(string sortColumn, IUiDispatcher dispatcher, ILogger? logger)
        : base(sortColumn, dispatcher, logger)
    {
    }

    /// <summary>The groupings the selector offers.</summary>
    public IReadOnlyList<TypeGrouping> Groupings { get; } = Enum.GetValues<TypeGrouping>();

    protected override string EmptyText => string.IsNullOrWhiteSpace(Filter) ? "Nothing to show." : $"No {Singular(GroupBy)} matches the filter.";

    protected static string Plural(TypeGrouping grouping) => grouping switch
    {
        TypeGrouping.Namespace => "namespaces",
        TypeGrouping.Assembly => "assemblies",
        _ => "types",
    };

    protected static string Singular(TypeGrouping grouping) => grouping switch
    {
        TypeGrouping.Namespace => "namespace",
        TypeGrouping.Assembly => "assembly",
        _ => "type",
    };

    protected override bool SortsDescendingFirst(string column) => column is not ("Name" or "Change" or "TypeName" or "Module");

    partial void OnGroupByChanged(TypeGrouping value) => Requery();
}
