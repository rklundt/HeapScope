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
/// What a front end may offer on a snapshot, read from its descriptor's availability list. Kept off the record, as
/// extension members, so the JSON-RPC formatter never sends them.
/// </summary>
public static class SnapshotAvailability
{
    extension(SnapshotDescriptor descriptor)
    {
        /// <summary>Whether the worker listed <paramref name="feature"/> as available.</summary>
        public bool IsAvailable(string feature) => descriptor.WhyUnavailable(feature) is null;

        /// <summary>
        /// Why <paramref name="feature"/> is unavailable, in the worker's words, or null when it is available.
        /// </summary>
        public string? WhyUnavailable(string feature)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            foreach (var entry in descriptor.Availability ?? [])
            {
                if (string.Equals(entry.Feature, feature, StringComparison.Ordinal))
                {
                    return entry.Unavailable;
                }
            }

            // Fail closed: offering a feature the worker never vouched for would end in a refusal.
            return $"The analysis worker did not list the feature \"{feature}\" for this snapshot, so it is not offered.";
        }
    }
}
