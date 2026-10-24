using CommunityToolkit.Mvvm.ComponentModel;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.Wpf.ViewModels;

/// <summary>The type grids' layer over the query core (sprint 1.5 split): grouping by type, namespace or assembly.</summary>
public abstract partial class TypeGridViewModel<TRow> : GridQueryViewModel<TRow>
    where TRow : class, IIndexedRow
{
    [ObservableProperty]
    private TypeGrouping _groupBy = TypeGrouping.Type;

    protected TypeGridViewModel(string sortColumn, ILogger? logger)
        : base(sortColumn, logger)
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
