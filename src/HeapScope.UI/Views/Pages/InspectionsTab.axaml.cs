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


using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace HeapScope.UI.Views.Pages;

/// <summary>View only; behavior lives in the view model (ARCHITECTURE.md 8).</summary>
public partial class InspectionsTab : UserControl
{
    public InspectionsTab()
    {
        InitializeComponent();

        // A group heading shares its tile's list item but is not part of it: a click on it selects nothing (2.6 review L13).
        TileList.AddHandler(PointerPressedEvent, OnTilePointerPressed, RoutingStrategies.Tunnel);
    }

    private static void OnTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<TextBlock>(includeSelf: true) is { } text && text.Classes.Contains("groupHeading"))
        {
            e.Handled = true;
        }
    }
}
