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

using HeapScope.Contracts;

namespace HeapScope.Application.Workers;

/// <summary>The worker process exited or the pipe broke while a call was in flight (ARCHITECTURE.md 4.3).</summary>
public sealed class WorkerCrashedException : Exception
{
    public WorkerCrashedException()
        : this("The analysis worker stopped unexpectedly.")
    {
    }

    public WorkerCrashedException(string message)
        : base(message)
    {
    }

    public WorkerCrashedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WorkerCrashedException(int? exitCode, Exception? innerException)
        : base(Describe(exitCode), innerException)
    {
        ExitCode = exitCode;
    }

    /// <summary>The worker's exit code, when it exited.</summary>
    public int? ExitCode { get; }

    private static string Describe(int? exitCode) => exitCode is int code
        ? $"The analysis worker stopped unexpectedly (exit code {code}). It restarts automatically on the next operation; see the log for its last messages."
        : "The connection to the analysis worker was lost. It restarts automatically on the next operation; see the log for details.";
}

/// <summary>The worker rejected an operation with a <see cref="HeapScopeErrorCode"/>.</summary>
public sealed class WorkerOperationException : Exception
{
    public WorkerOperationException()
    {
    }

    public WorkerOperationException(string message)
        : base(message)
    {
    }

    public WorkerOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WorkerOperationException(HeapScopeErrorCode errorCode, string message, Exception? innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public HeapScopeErrorCode ErrorCode { get; } = HeapScopeErrorCode.Unknown;
}

/// <summary>The worker could not be started or connected.</summary>
public sealed class WorkerStartException : Exception
{
    public WorkerStartException()
    {
    }

    public WorkerStartException(string message)
        : base(message)
    {
    }

    public WorkerStartException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
