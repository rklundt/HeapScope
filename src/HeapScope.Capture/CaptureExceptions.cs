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

namespace HeapScope.Capture;

/// <summary>The target process exited or stopped answering on its diagnostics port.</summary>
public sealed class TargetUnavailableException : Exception
{
    public TargetUnavailableException()
    {
    }

    public TargetUnavailableException(string message)
        : base(message)
    {
    }

    public TargetUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The dump file already exists; HeapScope never overwrites a dump.</summary>
public sealed class DumpFileExistsException : Exception
{
    public DumpFileExistsException()
    {
    }

    public DumpFileExistsException(string message)
        : base(message)
    {
    }

    public DumpFileExistsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The runtime could not write the dump (disk full, the target's account cannot write the folder, and so on).</summary>
public sealed class DumpWriteFailedException : Exception
{
    public DumpWriteFailedException()
    {
    }

    public DumpWriteFailedException(string message)
        : base(message)
    {
    }

    public DumpWriteFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
