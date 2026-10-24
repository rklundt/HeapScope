using HeapScope.Analysis.Inspections;
using HeapScope.Contracts;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// An inspection's rows as a result set (sprint 1.6). The rows read the graph while formatting, so the set holds a
/// snapshot lease as its owner, like the instances source.
/// </summary>
internal sealed class InspectionSource(InspectionTable table) : IResultSource
{
    public IReadOnlyList<ResultColumn> Columns { get; } = [.. table.Columns.Select(c => new ResultColumn(c.Name, KindOf(c.Kind), c.IsKey))];

    public int Count => table.Count;

    public long EstimatedBytes => table.EstimatedBytes;

    public void FormatRow(int index, string?[] cells) => table.FormatRow(index, cells);

    private static ResultColumnKind KindOf(InspectionColumnKind kind) => kind switch
    {
        InspectionColumnKind.Integer => ResultColumnKind.Integer,
        InspectionColumnKind.Bytes => ResultColumnKind.Bytes,
        InspectionColumnKind.Address => ResultColumnKind.Address,
        InspectionColumnKind.Percent => ResultColumnKind.Percent,
        _ => ResultColumnKind.Text,
    };
}
