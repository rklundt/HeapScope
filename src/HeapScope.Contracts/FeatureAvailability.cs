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

namespace HeapScope.Contracts;

/// <summary>
/// Whether a snapshot offers an optional feature (<see cref="SnapshotFeatureIds"/>), as the worker decides it. The same
/// rule refuses the feature's requests, with <see cref="Unavailable"/> as the message.
/// </summary>
/// <param name="Feature">The feature's id.</param>
/// <param name="Unavailable">Null when the feature is available; otherwise why not, in plain language.</param>
public sealed record FeatureAvailability(string Feature, string? Unavailable);
