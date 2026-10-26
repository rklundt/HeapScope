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

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HeapScope.Application.Jobs;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.UI.Wpf.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HeapScope.Spikes.AvaloniaGrid;

/// <summary>
/// The prototype's one window: the types grid and a type's objects over the real snapshot session, with the WPF app's
/// view models. With <c>--measure</c> it drives itself (open, show, scroll a type's objects end to end, switch retained
/// sizes on), records the longest UI thread stall, saves a screenshot and exits.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan s_tick = TimeSpan.FromMilliseconds(15);

    private readonly string[] _args;
    private readonly WorkerHost _host;
    private readonly SnapshotSession _session;
    private readonly TypeSummaryViewModel _types;
    private readonly StringBuilder _report = new();
    private InstancesViewModel? _instances;
    private SnapshotHandle? _snapshot;
    private DateTime _lastTick;
    private TimeSpan _maxLag;

    public MainWindow()
        : this([])
    {
    }

    public MainWindow(string[] args)
    {
        InitializeComponent();
        _args = args;
        _host = new WorkerHost(Options.Create(new WorkerHostOptions()), NullLoggerFactory.Instance);
        _session = new SnapshotSession(_host, new JobTracker(TimeProvider.System));
        _types = new TypeSummaryViewModel(_session);
        _types.PropertyChanged += OnTypesChanged;
        FilterBox.TextChanged += (_, _) => _types.Filter = FilterBox.Text;
        RetainedBox.IsCheckedChanged += (_, _) =>
        {
            _types.ShowRetained = RetainedBox.IsChecked == true;
            if (_instances is not null)
            {
                _instances.ShowRetained = _types.ShowRetained;
            }
        };
        TypesGrid.DoubleTapped += (_, _) => OpenType(TypesGrid.SelectedItem as TypeRowItem);
        TypesGrid.Sorting += (_, e) =>
        {
            e.Handled = true;
            _types.SortByCommand.Execute(e.Column.SortMemberPath);
        };
        InstancesGrid.Sorting += (_, e) =>
        {
            e.Handled = true;
            _instances?.SortByCommand.Execute(e.Column.SortMemberPath);
        };

        // The UI thread watchdog: a timer every 15 ms; any later tick is a stall.
        var watchdog = new DispatcherTimer(s_tick, DispatcherPriority.Background, (_, _) =>
        {
            var now = DateTime.UtcNow;
            var lag = now - _lastTick - s_tick;
            _maxLag = lag > _maxLag ? lag : _maxLag;
            _lastTick = now;
        });
        _lastTick = DateTime.UtcNow;
        watchdog.Start();
        Opened += async (_, _) => await StartAsync();
        Closed += async (_, _) => await _host.DisposeAsync();
    }

    private bool MeasureMode => _args.Contains("--measure");

    private async Task StartAsync()
    {
        var dump = _args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a));
        if (dump is null)
        {
            StatusText.Text = "Usage: AvaloniaGrid <dump> [--measure] [--screenshot <png>]";
            return;
        }

        try
        {
            StatusText.Text = "Opening " + Path.GetFileName(dump) + "...";
            var open = Stopwatch.StartNew();
            _snapshot = await _session.StartOpen(dump).Result;
            Line("Open (worker, index cache)", open.Elapsed);

            ResetLag();
            var show = Stopwatch.StartNew();
            _types.Show(_snapshot);
            await _types.QueryTask;
            await Render();
            Line("Types grid first shown", show.Elapsed, _maxLag);
            StatusText.Text = Path.GetFileName(dump) + " opened. Double-click a type to list its objects.";
            if (MeasureMode)
            {
                await MeasureAsync();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            _report.AppendLine("FAILED: " + ex);
            if (MeasureMode)
            {
                Finish();
            }
        }
    }

    private async Task MeasureAsync()
    {
        // The type with the most objects: its list is the longest a grid has to page through.
        _types.SortByCommand.Execute("Objects");
        await _types.QueryTask;
        await _types.Rows.WhenLoadedAsync();
        var largest = _types.Rows.GetRow(0);
        ResetLag();
        var list = Stopwatch.StartNew();
        OpenType(largest);
        await _instances!.QueryTask;
        await Render();
        Line($"Objects of {largest.Name} first shown ({_instances.Rows.Count:N0} rows)", list.Elapsed, _maxLag);

        // End to end in 100 jumps, then 300 small steps (a held-down arrow or wheel), each followed by a render.
        var rows = _instances.Rows;
        ResetLag();
        var slowest = TimeSpan.Zero;
        var scroll = Stopwatch.StartNew();
        for (var k = 0; k <= 100; k++)
        {
            slowest = Max(slowest, await ScrollTo((int)((long)(rows.Count - 1) * k / 100)));
        }

        for (var k = 0; k < 300; k++)
        {
            slowest = Max(slowest, await ScrollTo(Math.Min(rows.Count - 1, (rows.Count / 2) + (k * 20))));
        }

        Line("Scrolled 401 positions", scroll.Elapsed, _maxLag);
        _report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  probe: {RowListProbe.Enumerations} enumerations ({RowListProbe.EnumeratedItems:N0} items), {RowListProbe.CopyTos} CopyTo, {RowListProbe.IndexerReads:N0} indexer reads"));
        _report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  slowest single scroll and render: {slowest.TotalMilliseconds:N0} ms; pages held: {rows.RequestedPages.Count}"));

        ResetLag();
        var retained = Stopwatch.StartNew();
        RetainedBox.IsChecked = true;
        await Task.Delay(50);
        await _types.QueryTask;
        await _instances.QueryTask;
        await Render();
        Line("Retained sizes switched on (types and objects)", retained.Elapsed, _maxLag);
        using (var process = Process.GetCurrentProcess())
        {
            _report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"UI process: working set {process.WorkingSet64 / (1024 * 1024):N0} MB, private {process.PrivateMemorySize64 / (1024 * 1024):N0} MB"));
        }

        Finish();
    }

    private void OpenType(TypeRowItem? row)
    {
        if (row is not { IsLoaded: true, TypeName: { } typeName } || _snapshot is null)
        {
            return;
        }

        if (_instances is not null)
        {
            _instances.PropertyChanged -= OnInstancesChanged;
            _instances.Close();
        }

        _instances = new InstancesViewModel(_session, _snapshot, typeName, row.Module) { ShowRetained = _types.ShowRetained };
        _instances.PropertyChanged += OnInstancesChanged;
        InstancesTitle.Text = _instances.Title;
        _instances.Start();
    }

    private void OnTypesChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TypeSummaryViewModel.Rows):
                TypesGrid.ItemsSource = new VirtualCollectionView<TypeRowItem>(new RowListProbe(_types.Rows));
                break;
            case nameof(TypeSummaryViewModel.Totals):
                TypesTotals.Text = _types.Totals;
                break;
            case nameof(TypeSummaryViewModel.RetainedColumnShown):
                Column(TypesGrid, "Retained").IsVisible = _types.RetainedColumnShown;
                break;
            case nameof(TypeSummaryViewModel.Status) or nameof(TypeSummaryViewModel.RetainedNote):
                StatusText.Text = _types.Status ?? _types.RetainedNote ?? StatusText.Text;
                break;
        }
    }

    private void OnInstancesChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(InstancesViewModel.Rows):
                InstancesGrid.ItemsSource = new VirtualCollectionView<InstanceRowItem>(new RowListProbe(_instances!.Rows));
                break;
            case nameof(InstancesViewModel.Totals):
                InstancesTotals.Text = _instances!.Totals;
                break;
            case nameof(InstancesViewModel.RetainedColumnShown):
                Column(InstancesGrid, "Retained").IsVisible = _instances!.RetainedColumnShown;
                break;
        }
    }

    private async Task<TimeSpan> ScrollTo(int index)
    {
        var watch = Stopwatch.StartNew();
        InstancesGrid.ScrollIntoView(_instances!.Rows[index], null);
        await Render();
        return watch.Elapsed;
    }

    private static Task Render() => Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background).GetTask();

    private void ResetLag()
    {
        _maxLag = TimeSpan.Zero;
        _lastTick = DateTime.UtcNow;
    }

    private void Line(string what, TimeSpan time, TimeSpan? lag = null) =>
        _report.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{what}: {time.TotalMilliseconds:N0} ms{(lag is { } l ? $"; longest UI thread stall {Math.Max(0, l.TotalMilliseconds):N0} ms" : string.Empty)}"));

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static DataGridColumn Column(DataGrid grid, string header) => grid.Columns.First(c => Equals(c.Header, header));

    private void Finish()
    {
        var index = Array.IndexOf(_args, "--screenshot");
        if (index >= 0 && index + 1 < _args.Length && Bounds.Width > 0)
        {
            var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size);
            bitmap.Render(this);
            bitmap.Save(_args[index + 1]);
            _report.AppendLine("Screenshot: " + _args[index + 1]);
        }

        var output = Array.IndexOf(_args, "--report") is >= 0 and var r && r + 1 < _args.Length ? _args[r + 1] : null;
        if (output is not null)
        {
            File.WriteAllText(output, _report.ToString());
        }

        Console.Out.Write(_report.ToString());
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
