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

namespace HeapScope.UI.ViewModels;

/// <summary>What a <see cref="NavigationTarget"/> opens (sprint 2.4, 2.3.7 review M8).</summary>
public enum NavigationKind
{
    /// <summary>One object, on the Snapshot tab.</summary>
    Object = 0,

    /// <summary>The objects of a type (in a scope), on the Snapshot tab.</summary>
    Instances = 1,

    /// <summary>Who references the objects of a type, on the Snapshot tab.</summary>
    TypeReferrers = 2,

    /// <summary>Why the objects of a type are alive, on the Snapshot tab.</summary>
    TypePaths = 3,

    /// <summary>The dominator tree, at a node (or its root), on the Retainers tab.</summary>
    Retainers = 4,

    /// <summary>The objects a type's objects retain together (exact set retention), on the Retainers tab.</summary>
    RetainedBySet = 5,
}
