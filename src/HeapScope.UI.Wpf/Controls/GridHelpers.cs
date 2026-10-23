using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using HeapScope.Contracts;

namespace HeapScope.UI.Wpf.Controls;

/// <summary>
/// Carries a binding into places outside the visual tree (DataGrid columns), so a column's visibility can follow the
/// view model.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}

/// <summary>
/// Sends a DataGrid's header clicks to a command (with the column's sort member path) instead of sorting in memory: the
/// worker sorts, because the grid only holds the rows on screen (sprint 1.3). The header arrows follow
/// <c>State</c> ("SortMemberPath|asc" or "|desc") from the view model, the single source of truth, and are set again
/// whenever the grid gets new rows (a new ItemsSource clears them).
/// </summary>
public static class GridSorting
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(GridSorting), new PropertyMetadata(null, OnCommandChanged));

    public static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(string), typeof(GridSorting), new PropertyMetadata(null, OnStateChanged));

    private static readonly DependencyPropertyDescriptor s_itemsSource =
        DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid));

    public static string? GetState(DependencyObject element) => (string?)element.GetValue(StateProperty);

    public static void SetState(DependencyObject element, string? value) => element.SetValue(StateProperty, value);

    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);

    public static void SetCommand(DependencyObject element, ICommand? value) => element.SetValue(CommandProperty, value);

    private static void OnCommandChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not DataGrid grid)
        {
            return;
        }

        grid.Sorting -= OnSorting;
        if (e.NewValue is not null)
        {
            grid.Sorting += OnSorting;
        }
    }

    private static void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var grid = (DataGrid)sender;
        var path = e.Column.SortMemberPath;
        if (GetCommand(grid) is { } command && command.CanExecute(path))
        {
            command.Execute(path);
        }
    }

    private static void OnStateChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not DataGrid grid)
        {
            return;
        }

        if (e.OldValue is null)
        {
            s_itemsSource.AddValueChanged(grid, OnItemsSourceChanged);
        }

        ShowArrows(grid);
    }

    private static void OnItemsSourceChanged(object? sender, EventArgs e)
    {
        // The grid clears the arrows while it takes the new source; set them once it is done.
        if (sender is DataGrid grid)
        {
            grid.Dispatcher.BeginInvoke(() => ShowArrows(grid), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private static void ShowArrows(DataGrid grid)
    {
        var parts = GetState(grid)?.Split('|');
        foreach (var column in grid.Columns)
        {
            column.SortDirection = parts is [var path, var direction] && column.SortMemberPath == path
                ? (direction == "desc" ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
    }
}

/// <summary>Shows a name with the characters matched by the type filter in bold (dotMemory's match highlighting).</summary>
public static class HighlightedText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(HighlightedText), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty FilterProperty = DependencyProperty.RegisterAttached(
        "Filter", typeof(string), typeof(HighlightedText), new PropertyMetadata(null, OnChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    public static string? GetFilter(DependencyObject element) => (string?)element.GetValue(FilterProperty);

    public static void SetFilter(DependencyObject element, string? value) => element.SetValue(FilterProperty, value);

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block)
        {
            return;
        }

        var text = GetText(block) ?? string.Empty;
        block.Inlines.Clear();
        var ranges = NameFilter.Match(text, GetFilter(block));
        var position = 0;
        foreach (var (start, length) in ranges ?? [])
        {
            if (start > position)
            {
                block.Inlines.Add(new Run(text[position..start]));
            }

            block.Inlines.Add(new Run(text.Substring(start, length)) { FontWeight = FontWeights.Bold });
            position = start + length;
        }

        if (position < text.Length)
        {
            block.Inlines.Add(new Run(text[position..]));
        }
    }
}

/// <summary>Lays children out side by side, each as wide as its share of the whole (the heap kinds bar).</summary>
public sealed class ProportionalPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(ProportionalPanel), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static double GetWeight(DependencyObject element) => (double)element.GetValue(WeightProperty);

    public static void SetWeight(DependencyObject element, double value) => element.SetValue(WeightProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var total = InternalChildren.Cast<UIElement>().Sum(c => Math.Max(0, GetWeight(Container(c))));
        var x = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            var width = total <= 0 ? 0 : finalSize.Width * Math.Max(0, GetWeight(Container(child))) / total;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }

        return finalSize;
    }

    /// <summary>Items controls wrap items in presenters; the weight is set on the item's root element.</summary>
    private static DependencyObject Container(UIElement child) =>
        child is ContentPresenter presenter && System.Windows.Media.VisualTreeHelper.GetChildrenCount(presenter) > 0
            ? System.Windows.Media.VisualTreeHelper.GetChild(presenter, 0)
            : child;
}

/// <summary>Visible when the value is a non-empty string or a non-null object, collapsed otherwise.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || (value is string text && text.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Looks up a brush resource by key (the fixed heap kind colors of the active theme).</summary>
public sealed class ResourceKeyToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? System.Windows.Application.Current?.TryFindResource(key) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
