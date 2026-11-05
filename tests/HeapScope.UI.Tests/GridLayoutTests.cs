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


using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using HeapScope.Contracts;
using HeapScope.UI.Controls;
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.Tests;

/// <summary>ADR-040: names stay readable and numbers line up with their headers, in every grid.</summary>
public partial class GridLayoutTests(UiSession ui)
{
    [Fact]
    public void Every_star_column_keeps_a_minimum_width()
    {
        var views = Path.Combine(global::HeapScope.Tests.Shared.RepoRoot.Path, "src", "HeapScope.UI", "Views");
        foreach (var file in Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories))
        {
            foreach (Match column in StarColumn().Matches(File.ReadAllText(file)))
            {
                column.Value.ShouldContain("MinWidth", customMessage: $"{Path.GetFileName(file)}: {column.Value}");
            }
        }
    }

    [Fact]
    public async Task A_number_column_s_header_is_right_aligned()
    {
        await ui.RunAsync(async () =>
        {
            var grid = new DataGrid { Width = 400, Height = 200 };
            grid.Columns.Add(new DataGridTextColumn { Header = "Name", Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            var bytes = new DataGridTextColumn { Header = "Bytes", Width = new DataGridLength(100) };
            bytes.CellStyleClasses.Add("number");
            grid.Columns.Add(bytes);
            GridExports.SetExportName(grid, "Test");
            var window = new Window { Width = 500, Height = 300, Content = grid };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()?.Dispose();
                var headers = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().ToList();
                headers.Single(h => Equals(h.Content, "Bytes")).HorizontalContentAlignment.ShouldBe(HorizontalAlignment.Right);
                headers.Single(h => Equals(h.Content, "Name")).HorizontalContentAlignment.ShouldBe(HorizontalAlignment.Left);
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    /// <summary>A column built after the grid loaded (a result set's columns) gets the same alignment.</summary>
    [Fact]
    public async Task A_number_column_added_after_the_grid_loaded_is_right_aligned_too()
    {
        await ui.RunAsync(async () =>
        {
            var grid = new DataGrid { Width = 400, Height = 200 };
            grid.Columns.Add(new DataGridTextColumn { Header = "Name", Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            GridExports.SetExportName(grid, "Test");
            var window = new Window { Width = 500, Height = 300, Content = grid };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var bytes = new DataGridTextColumn { Header = "Bytes", Width = new DataGridLength(100) };
                bytes.CellStyleClasses.Add("number");
                grid.Columns.Add(bytes);
                NumberHeaders.ApplyAfterLayout(grid);
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()?.Dispose();
                Dispatcher.UIThread.RunJobs();
                grid.GetVisualDescendants().OfType<DataGridColumnHeader>().Single(h => Equals(h.Content, "Bytes"))
                    .HorizontalContentAlignment.ShouldBe(HorizontalAlignment.Right);
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    /// <summary>Fit to width stops at a readable scale, so a wider diagram must keep its scroll bar: nothing may be out of reach.</summary>
    [Theory]
    [InlineData(2, true)]
    [InlineData(20, false)]
    public async Task A_fitted_diagram_fits_or_scrolls(int roots, bool fits)
    {
        await ui.RunAsync(async () =>
        {
            // Each root sits beside the others, so the roots set the width.
            var paths = Enumerable.Range(0, roots).Select(static i => new KeyPathDto(
                new PathRootDto(RootKindDto.StaticField, false, "static field s_" + i.ToString(CultureInfo.InvariantCulture)),
                [new PathStepDto("App.Step" + i.ToString(CultureInfo.InvariantCulture), false)],
                "App.Item",
                10,
                100,
                0x1000)).ToList();
            var diagram = KeyPathDiagram.Build(paths, showFramework: false);
            var view = new KeyPathDiagramView { Diagram = diagram, FitWidth = true };
            var viewer = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = view };
            var window = new Window { Width = 800, Height = 600, Content = viewer };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()?.Dispose();
                Dispatcher.UIThread.RunJobs();
                if (fits)
                {
                    viewer.Extent.Width.ShouldBeLessThanOrEqualTo(viewer.Viewport.Width + 1, "this diagram scales to fit");
                }
                else
                {
                    viewer.Extent.Width.ShouldBeGreaterThan(viewer.Viewport.Width, "too wide even at the smallest scale");
                    viewer.HorizontalScrollBarVisibility.ShouldBe(ScrollBarVisibility.Auto);
                    view.Bounds.Width.ShouldBe((diagram.Width * KeyPathDiagram.MinFitScale) + 24, 1, "all of it is laid out, so all of it can be scrolled to");
                }
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    [GeneratedRegex(@"<DataGrid(?:Text|Template)Column\s[^>]*Width=""[0-9.]*\*""[^>]*>")]
    private static partial Regex StarColumn();
}
