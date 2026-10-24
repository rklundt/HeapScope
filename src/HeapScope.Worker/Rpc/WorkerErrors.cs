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
using HeapScope.Core;
using HeapScope.Loaders.ClrMd;
using HeapScope.Storage;
using HeapScope.Worker.Snapshots;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// Creates JSON-RPC errors carrying a <see cref="HeapScopeErrorCode"/> in the error code (ARCHITECTURE.md 11), and
/// maps domain exceptions to codes in one place.
/// </summary>
internal static class WorkerErrors
{
    public static LocalRpcException Create(HeapScopeErrorCode code, string message) =>
        new(message) { ErrorCode = (int)code };

    /// <summary>Maps a domain exception to its error code; null when it has no specific code.</summary>
    public static HeapScopeErrorCode? CodeOf(Exception exception) => exception switch
    {
        CapabilityNotSupportedException => HeapScopeErrorCode.CapabilityNotSupported,
        RuntimeComponentMissingException => HeapScopeErrorCode.RuntimeComponentMissing,
        SnapshotFormatException => HeapScopeErrorCode.SnapshotFormat,
        SnapshotChangedException => HeapScopeErrorCode.SnapshotChanged,
        IndexCacheInUseException or IOException or UnauthorizedAccessException => HeapScopeErrorCode.SnapshotUnavailable,
        OutOfMemoryException or InsufficientExecutionStackException => HeapScopeErrorCode.InsufficientMemory,
        ArgumentException => HeapScopeErrorCode.InvalidArgument,
        _ => null,
    };

    /// <summary>The message users see for an exception with <paramref name="code"/>: plain language and a next step.</summary>
    public static string MessageOf(Exception exception, HeapScopeErrorCode code) => code switch
    {
        HeapScopeErrorCode.SnapshotUnavailable when exception is not IndexCacheInUseException =>
            $"The file could not be read: {exception.Message} It may still be being written (wait until the capture finishes) or access may be denied.",
        _ => exception.Message,
    };

    /// <summary>
    /// Runs an RPC method body, translating domain exceptions into coded errors. Every failure except cancellation is
    /// logged with its stack trace: coded ones as warnings, unexpected ones as errors (the client only sees the message).
    /// </summary>
    public static async Task<T> GuardAsync<T>(Func<Task<T>> body, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not LocalRpcException and not OperationCanceledException && CodeOf(ex) is { } code)
        {
            logger?.LogWarning(ex, "Request failed with {Code}", code);
            throw new LocalRpcException(MessageOf(ex, code), ex) { ErrorCode = (int)code };
        }
        catch (Exception ex) when (ex is not LocalRpcException and not OperationCanceledException)
        {
            logger?.LogError(ex, "Request failed unexpectedly");
            throw;
        }
    }
}
