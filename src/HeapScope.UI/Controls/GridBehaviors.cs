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
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HeapScope.Contracts;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.Controls;

/// <summary>
/// What HeapScope's grids add to the DataGrid (sprint 2.3.6, from the WPF grid helpers of sprints 1.3 to 1.6):
/// <list type="bullet">
/// <item><c>Rows</c>: the rows, a data-virtualized row list shown through <see cref="VirtualCollectionView{TRow}"/>.</item>
/// <item><c>SortCommand</c> and <c>SortState</c>: a header click asks the view model (the worker sorts, since the grid
/// holds only the rows on screen); the header arrow follows the view model's state ("Bytes|desc").</item>
/// <item><c>OpenCommand</c>: double-click on a row, or Enter, opens it (dotMemory's one drill-down action).</item>
/// <item><c>ShowRetained</c> and <c>ShowGenerations</c>: show the columns tagged <c>Retained</c> or
/// <c>Generations</c> (columns are outside the visual tree, so they cannot bind themselves).</item>
/// <item><c>ResultColumns</c>: columns built from a result set's column list (inspections, sprint 1.6).</item>
/// </list>
/// </summary>
public static class GridBehaviors
{
    public static readonly AttachedProperty<IList?> RowsProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, IList?>("Rows", typeof(GridBehaviors));

    public static readonly AttachedProperty<ICommand?> SortCommandProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, ICommand?>("SortCommand", typeof(GridBehaviors));

    public static readonly AttachedProperty<string?> SortStateProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, string?>("SortState", typeof(GridBehaviors));

    public static readonly AttachedProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, ICommand?>("OpenCommand", typeof(GridBehaviors));

    public static readonly AttachedProperty<bool> ShowRetainedProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>("ShowRetained", typeof(GridBehaviors));

    public static readonly AttachedProperty<bool> ShowGenerationsProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>("ShowGenerations", typeof(GridBehaviors));

    public static readonly AttachedProperty<IReadOnlyList<(ResultColumn Column, int Index)>?> ResultColumnsProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, IReadOnlyList<(ResultColumn Column, int Index)>?>("ResultColumns", typeof(GridBehaviors));

    private static readonly AttachedProperty<bool> s_columnsWatched =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>("ColumnsWatched", typeof(GridBehaviors));

    public const string RetainedTag = "Retained";

    public const string GenerationsTag = "Generations";

    static GridBehaviors()
    {
        RowsProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            grid.ItemsSource = VirtualCollectionViews.For(e.NewValue as IList);
            ShowSort(grid);
        });
        SortCommandProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            grid.Sorting -= OnSorting;
            if (e.NewValue is not null)
            {
                grid.Sorting += OnSorting;
            }
        });
        SortStateProperty.Changed.AddClassHandler<DataGrid>((grid, _) => ShowSort(grid));
        OpenCommandProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            grid.DoubleTapped -= OnDoubleTapped;
            grid.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            if (e.NewValue is not null)
            {
                grid.DoubleTapped += OnDoubleTapped;
                grid.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            }
        });
        ShowRetainedProperty.Changed.AddClassHandler<DataGrid>((grid, _) => WatchColumns(grid));
        ShowGenerationsProperty.Changed.AddClassHandler<DataGrid>((grid, _) => WatchColumns(grid));
        ResultColumnsProperty.Changed.AddClassHandler<DataGrid>((grid, e) => BuildColumns(grid, e.NewValue as IReadOnlyList<(ResultColumn Column, int Index)>));
    }

    public static IList? GetRows(DataGrid grid) => grid?.GetValue(RowsProperty);

    public static void SetRows(DataGrid grid, IList? value) => grid?.SetValue(RowsProperty, value);

    public static ICommand? GetSortCommand(DataGrid grid) => grid?.GetValue(SortCommandProperty);

    public static void SetSortCommand(DataGrid grid, ICommand? value) => grid?.SetValue(SortCommandProperty, value);

    public static string? GetSortState(DataGrid grid) => grid?.GetValue(SortStateProperty);

    public static void SetSortState(DataGrid grid, string? value) => grid?.SetValue(SortStateProperty, value);

    public static ICommand? GetOpenCommand(DataGrid grid) => grid?.GetValue(OpenCommandProperty);

    public static void SetOpenCommand(DataGrid grid, ICommand? value) => grid?.SetValue(OpenCommandProperty, value);

    public static bool GetShowRetained(DataGrid grid) => grid?.GetValue(ShowRetainedProperty) ?? false;

    public static void SetShowRetained(DataGrid grid, bool value) => grid?.SetValue(ShowRetainedProperty, value);

    public static bool GetShowGenerations(DataGrid grid) => grid?.GetValue(ShowGenerationsProperty) ?? false;

    public static void SetShowGenerations(DataGrid grid, bool value) => grid?.SetValue(ShowGenerationsProperty, value);

    public static IReadOnlyList<(ResultColumn Column, int Index)>? GetResultColumns(DataGrid grid) => grid?.GetValue(ResultColumnsProperty);

    public static void SetResultColumns(DataGrid grid, IReadOnlyList<(ResultColumn Column, int Index)>? value) => grid?.SetValue(ResultColumnsProperty, value);

    /// <summary>Applies the column groups once the columns exist (they are added after the attached values in markup).</summary>
    public static void ApplyColumnGroups(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ShowColumns(grid, RetainedTag, GetShowRetained(grid));
        ShowColumns(grid, GenerationsTag, GetShowGenerations(grid));
    }

    // Markup sets the attached values before it adds the columns: apply the groups now and whenever the columns change.
    private static void WatchColumns(DataGrid grid)
    {
        if (!grid.GetValue(s_columnsWatched))
        {
            grid.SetValue(s_columnsWatched, true);
            grid.Columns.CollectionChanged += (_, _) => ApplyColumnGroups(grid);
        }

        ApplyColumnGroups(grid);
    }

    private static void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (sender is DataGrid grid && GetSortCommand(grid) is { } command && e.Column.SortMemberPath is { Length: > 0 } path && command.CanExecute(path))
        {
            command.Execute(path);
        }
    }

    private static void ShowSort(DataGrid grid)
    {
        if (grid.ItemsSource is ISortArrows view)
        {
            view.ShowSort(GetSortState(grid));
        }
    }

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only a double-click on a row opens it, not one on a header or the scroll bar.
        if (sender is DataGrid grid && (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is { DataContext: { } item })
        {
            Open(grid, item);
            e.Handled = true;
        }
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is DataGrid { SelectedItem: { } item } grid)
        {
            Open(grid, item);
            e.Handled = true;
        }
    }

    private static void Open(DataGrid grid, object item)
    {
        if (GetOpenCommand(grid) is { } command && command.CanExecute(item))
        {
            command.Execute(item);
        }
    }

    private static void ShowColumns(DataGrid grid, string tag, bool shown)
    {
        foreach (var column in grid.Columns)
        {
            if (column.Tag is string columnTag && columnTag == tag)
            {
                column.IsVisible = shown;
            }
        }
    }

    private static void BuildColumns(DataGrid grid, IReadOnlyList<(ResultColumn Column, int Index)>? columns)
    {
        grid.Columns.Clear();
        foreach (var (column, index) in columns ?? [])
        {
            var numeric = column.Kind is ResultColumnKind.Integer or ResultColumnKind.Bytes or ResultColumnKind.Percent or ResultColumnKind.SignedInteger;
            var text = new DataGridTextColumn
            {
                Header = ColumnHeaders.Header(column.Name),
                // The cells are a read-only list whose runtime type may have no public indexer: pick the cell in a converter.
                Binding = new Binding(nameof(InspectionRowItem.Display)) { Converter = CellAt.Instance, ConverterParameter = index, Mode = BindingMode.OneWay },
                Width = column.Kind == ResultColumnKind.Address ? new DataGridLength(150)
                    : numeric ? new DataGridLength(110)
                    : new DataGridLength(1, DataGridLengthUnitType.Star),
                CanUserSort = false,
            };
            if (numeric)
            {
                text.CellStyleClasses.Add("number");
            }

            if (column.Kind == ResultColumnKind.Address)
            {
                text.CellStyleClasses.Add("address");
            }

            grid.Columns.Add(text);
        }
    }

    /// <summary>The cell at the column's index of a row's display cells.</summary>
    private sealed class CellAt : Avalonia.Data.Converters.IValueConverter
    {
        public static CellAt Instance { get; } = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is IReadOnlyList<string> cells && parameter is int index && index >= 0 && index < cells.Count ? cells[index] : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
