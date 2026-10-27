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

using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using HeapScope.Tests.Shared;
using HeapScope.UI.Controls;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.Tests;

/// <summary>Every grid exports to CSV (sprint 2.7 acceptance).</summary>
public sealed partial class ExportTests(UiSession ui)
{
    [Fact]
    public void Every_grid_in_every_view_has_an_export_name()
    {
        var views = Directory.GetFiles(Path.Combine(RepoRoot.Path, "src", "HeapScope.UI", "Views"), "*.axaml", SearchOption.AllDirectories);
        var grids = views.SelectMany(file => GridTag().Matches(File.ReadAllText(file)).Select(m => (File: Path.GetFileName(file), Tag: m.Value))).ToList();

        grids.Count.ShouldBeGreaterThanOrEqualTo(16);
        grids.Where(g => !g.Tag.Contains("GridExports.ExportName=", StringComparison.Ordinal)).ShouldBeEmpty();
        grids.Select(g => ExportName().Match(g.Tag).Groups[1].Value).ShouldBeUnique();
    }

    [Fact]
    public async Task A_grid_of_rows_in_memory_exports_its_visible_columns_from_the_context_menu()
    {
        await ui.RunAsync(async () =>
        {
            var rows = new ObservableCollection<Row> { new("App.Order", 24, "hidden"), new("=cmd", 1_500, "hidden") };
            var grid = new DataGrid { ItemsSource = rows };
            grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Bytes", Binding = new Binding(nameof(Row.Bytes)) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Note", Binding = new Binding(nameof(Row.Note)), IsVisible = false });
            var secret = new DataGridTextColumn { Header = "Secret", Binding = new Binding(nameof(Row.Note)) };
            GridExports.SetHasValues(secret, true);
            grid.Columns.Add(secret);
            GridExports.SetExportName(grid, "Sample");
            var window = new Window { Content = grid, Width = 600, Height = 400 };
            GridExportRequestedEventArgs? requested = null;
            window.AddHandler(GridExports.ExportRequestedEvent, (_, e) => requested = e);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var item = grid.ContextMenu.ShouldNotBeNull().Items.OfType<MenuItem>().Single(i => Equals(i.Header, GridExports.MenuText));
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

            requested.ShouldNotBeNull().Name.ShouldBe("Sample");
            using var writer = new StringWriter();
            (await requested.Export.WriteCsvAsync(writer, includeValues: false, CancellationToken.None)).ShouldBe(2);
            writer.ToString().ShouldBe(string.Join(Environment.NewLine, "Name,Bytes", "App.Order,24", "'=cmd,1500", string.Empty));
            requested.Export.Columns.Single(c => c.Name == "Secret").HasValues.ShouldBeTrue("the person is asked before it is written (review H1)");

            // The grid's own sort is the export's order.
            grid.Columns[1].Sort(System.ComponentModel.ListSortDirection.Descending);
            Dispatcher.UIThread.RunJobs();
            using var sorted = new StringWriter();
            await GridExports.ExportOf(grid)!.WriteCsvAsync(sorted, includeValues: false, CancellationToken.None);
            sorted.ToString().Split(Environment.NewLine)[1].ShouldBe("'=cmd,1500");
            window.Close();
        });
    }

    [Fact]
    public void Columns_that_show_the_targets_data_are_flagged_in_every_view()
    {
        // Review H1: the Processes grid's command line goes through the values question.
        var processes = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "HeapScope.UI", "Views", "Pages", "ProcessesPage.axaml"));
        CommandLineColumn().Match(processes).Value.ShouldContain("GridExports.HasValues=\"True\"");
    }

    [Fact]
    public async Task Command_E_targets_the_focused_grid_else_the_first_one_shown()
    {
        await ui.RunAsync(() =>
        {
            var first = new DataGrid { ItemsSource = new ObservableCollection<Row>() };
            var second = new DataGrid { ItemsSource = new ObservableCollection<Row>() };
            var hidden = new DataGrid { IsVisible = false };
            GridExports.SetExportName(first, "First");
            GridExports.SetExportName(second, "Second");
            GridExports.SetExportName(hidden, "Hidden");
            var panel = new StackPanel { Children = { hidden, first, second } };
            var window = new Window { Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            GridExports.Target(null, panel).ShouldBe(first);
            GridExports.Target(second, panel).ShouldBe(second);
            window.Close();
            return Task.CompletedTask;
        });
    }

    [GeneratedRegex(@"<DataGrid\s[^>]*>", RegexOptions.Singleline)]
    private static partial Regex GridTag();

    [GeneratedRegex("<DataGridTextColumn Header=\"Command line\"[^>]*>")]
    private static partial Regex CommandLineColumn();

    [GeneratedRegex("GridExports.ExportName=\"([^\"]+)\"")]
    private static partial Regex ExportName();

    public sealed record Row(string Name, long Bytes, string Note);
}
