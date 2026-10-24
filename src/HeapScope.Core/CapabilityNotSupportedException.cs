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

namespace HeapScope.Core;

/// <summary>Thrown when an operation needs a capability the snapshot does not have (ADR-003).</summary>
public sealed class CapabilityNotSupportedException : Exception
{
    public CapabilityNotSupportedException()
        : this(SnapshotCapabilities.None)
    {
    }

    public CapabilityNotSupportedException(string message)
        : base(message)
    {
    }

    public CapabilityNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CapabilityNotSupportedException(SnapshotCapabilities missing)
        : base($"This snapshot does not support: {missing}.")
    {
        Missing = missing;
    }

    /// <summary>The missing capabilities, with the explanation for the snapshot's kind (sprint 1.8).</summary>
    public CapabilityNotSupportedException(SnapshotCapabilities missing, string message)
        : base(message)
    {
        Missing = missing;
    }

    /// <summary>The capabilities that were required but missing.</summary>
    public SnapshotCapabilities Missing { get; }
}
