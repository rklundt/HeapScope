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

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HeapScope.UI.Wpf.ViewModels;

namespace HeapScope.Spikes.AvaloniaGrid;

public partial class App : Avalonia.Application
{
    private readonly string[] _args;

    public App()
        : this([])
    {
    }

    public App(string[] args) => _args = args;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The linked grid view models let a pending render run before binding rows: Avalonia's way.
            TaskSchedulerExtensions.RenderYield = static () => Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background).GetTask();
            desktop.MainWindow = new MainWindow(_args);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
