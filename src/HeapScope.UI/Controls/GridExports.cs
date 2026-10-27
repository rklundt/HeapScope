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

using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HeapScope.Application.Exports;
using HeapScope.Contracts;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.Controls;

/// <summary>
/// CSV export of every grid (sprint 2.7). A grid with an <c>ExportName</c> gets "Export to CSV..." in its context menu;
/// choosing it (or Command+E while the grid has focus) raises <see cref="ExportRequestedEvent"/>, which the window hands
/// to the shell's <see cref="GridExportService"/>. A worker-backed grid exports its row list (all of the result's rows);
/// a grid of rows held in memory exports its visible columns, read through their bindings.
/// </summary>
public static class GridExports
{
    /// <summary>The menu item's text.</summary>
    public const string MenuText = "Export to CSV...";

    /// <summary>The grid's fixed name (the file's suggested name and the log's), such as "Instances".</summary>
    public static readonly AttachedProperty<string?> ExportNameProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, string?>("ExportName", typeof(GridExports));

    /// <summary>
    /// A column of a grid of in-memory rows that shows the target's data (a process's command line): left out of an export
    /// unless the person opts in, as a worker column with <see cref="ResultColumn.HasValues"/> is (review H1).
    /// </summary>
    public static readonly AttachedProperty<bool> HasValuesProperty =
        AvaloniaProperty.RegisterAttached<DataGridColumn, bool>("HasValues", typeof(GridExports));

    public static readonly RoutedEvent<GridExportRequestedEventArgs> ExportRequestedEvent =
        RoutedEvent.Register<GridExportRequestedEventArgs>("ExportRequested", RoutingStrategies.Bubble, typeof(GridExports));

    static GridExports()
    {
        ExportNameProperty.Changed.AddClassHandler<DataGrid>((grid, _) =>
        {
            grid.Loaded -= OnLoaded;
            grid.Loaded += OnLoaded;
            AddMenuItem(grid);
        });
    }

    public static string? GetExportName(DataGrid grid) => grid?.GetValue(ExportNameProperty);

    public static void SetExportName(DataGrid grid, string? value) => grid?.SetValue(ExportNameProperty, value);

    public static bool GetHasValues(DataGridColumn column) => column?.GetValue(HasValuesProperty) ?? false;

    public static void SetHasValues(DataGridColumn column, bool value) => column?.SetValue(HasValuesProperty, value);

    /// <summary>The grid's rows as an export: its worker row list, or its in-memory rows; null when it shows nothing.</summary>
    public static IGridExport? ExportOf(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        return GridBehaviors.GetRows(grid) as IGridExport
            ?? (grid.ItemsSource is { } items ? ItemsGridExport.From(grid, (IEnumerable?)grid.CollectionView ?? items) : null);
    }

    /// <summary>Asks the window to export <paramref name="grid"/>; false when it has no export name.</summary>
    public static bool RequestExport(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (GetExportName(grid) is not { Length: > 0 } name || ExportOf(grid) is not { } export)
        {
            return false;
        }

        grid.RaiseEvent(new GridExportRequestedEventArgs(name, export) { RoutedEvent = ExportRequestedEvent, Source = grid });
        return true;
    }

    /// <summary>
    /// The grid Command+E exports: the focused one (or the one holding focus), else the first exportable grid shown under
    /// <paramref name="scope"/>.
    /// </summary>
    public static DataGrid? Target(Visual? focused, Visual scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (focused?.FindAncestorOfType<DataGrid>(includeSelf: true) is { } grid && GetExportName(grid) is not null)
        {
            return grid;
        }

        return scope.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault(g => g.IsEffectivelyVisible && GetExportName(g) is not null);
    }

    private static void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is DataGrid grid)
        {
            AddMenuItem(grid);
        }
    }

    // Markup may set the context menu after the attached value: add the item once both exist (and at load).
    private static void AddMenuItem(DataGrid grid)
    {
        grid.ContextMenu ??= new ContextMenu();
        var items = grid.ContextMenu.Items;
        if (items.OfType<MenuItem>().Any(i => Equals(i.Tag, typeof(GridExports))))
        {
            return;
        }

        if (items.Count > 0)
        {
            items.Add(new Separator());
        }

        var item = new MenuItem { Header = MenuText, Tag = typeof(GridExports), InputGesture = Avalonia.Input.KeyGesture.Parse(Shortcuts.Gesture(ShortcutKeys.Export)) };
        item.Click += (_, _) => RequestExport(grid);
        items.Add(item);
    }

    /// <summary>
    /// The visible columns and rows of a grid of in-memory rows, read on the dispatcher when the export starts, in the
    /// grid's current sort (its collection view; review).
    /// </summary>
    private sealed class ItemsGridExport(IReadOnlyList<ResultColumn> columns, List<IReadOnlyList<string?>> rows) : IGridExport
    {
        public IReadOnlyList<ResultColumn> Columns { get; } = columns;

        public int RowCount => rows.Count;

        public static ItemsGridExport? From(DataGrid grid, IEnumerable items)
        {
            var shown = grid.Columns
                .Where(c => c.IsVisible)
                .Select(c => (Header: c.Header?.ToString(), Path: PathOf(c), HasValues: GetHasValues(c)))
                .Where(c => c.Header is { Length: > 0 } && c.Path is { Length: > 0 })
                .ToList();
            if (shown.Count == 0)
            {
                return null;
            }

            var rows = new List<IReadOnlyList<string?>>();
            foreach (var item in items)
            {
                rows.Add([.. shown.Select(c => Format(Read(item, c.Path!)))]);
            }

            return new ItemsGridExport([.. shown.Select(c => new ResultColumn(c.Header!, ResultColumnKind.Text, HasValues: c.HasValues))], rows);
        }

        public Task<long> WriteCsvAsync(TextWriter output, bool includeValues, CancellationToken cancellationToken) =>
            CsvExport.WriteAsync(output, Columns, rows, includeValues, cancellationToken);

        private static string? PathOf(DataGridColumn column) =>
            (column as DataGridBoundColumn)?.Binding is Binding { Path: { Length: > 0 } path } ? path : column.SortMemberPath;

        private static object? Read(object? item, string path)
        {
            foreach (var part in path.Split('.'))
            {
                if (item is null)
                {
                    return null;
                }

                item = item.GetType().GetProperty(part)?.GetValue(item);
            }

            return item;
        }

        private static string? Format(object? value) => value switch
        {
            null => null,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }
}

/// <summary>A grid asked to be exported (sprint 2.7).</summary>
public sealed class GridExportRequestedEventArgs(string name, IGridExport export) : RoutedEventArgs
{
    public string Name { get; } = name;

    public IGridExport Export { get; } = export;
}
