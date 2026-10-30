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
using System.Diagnostics;
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
        UntrustedDacException => HeapScopeErrorCode.DacNotTrusted,
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
    /// Expected refusals: the request was understood and answered "no" for a reason the user can act on. They are logged
    /// at Information without a stack trace; other coded failures keep their stack (sprint 2.3.7 review M2).
    /// </summary>
    public static bool IsRefusal(HeapScopeErrorCode code) => code is HeapScopeErrorCode.CapabilityNotSupported
        or HeapScopeErrorCode.InvalidArgument or HeapScopeErrorCode.SnapshotChanged or HeapScopeErrorCode.SnapshotNotLoaded
        or HeapScopeErrorCode.ResultSetNotFound or HeapScopeErrorCode.RuntimeComponentMissing or HeapScopeErrorCode.InsufficientMemory
        or HeapScopeErrorCode.TraceNotLoaded or HeapScopeErrorCode.SnapshotFormat or HeapScopeErrorCode.DacNotTrusted;

    /// <summary>The error data a front end needs to act on a refusal (sprint 3.6: the DAC to trust), or null.</summary>
    public static object? DataOf(Exception exception) => exception switch
    {
        UntrustedDacException { Path: { } path, Sha256: { } sha256 } untrusted => new DacTrustDetails(path, sha256, untrusted.RuntimeVersion ?? ""),
        _ => null,
    };

    /// <summary>
    /// Runs an RPC method body, translating domain exceptions into coded errors. Every outcome except cancellation is
    /// logged once with the operation and the request (<see cref="Provenance"/>): an answer at <paramref name="resultLevel"/>
    /// with what was answered (sprint 2.3.7 review H3), a refusal at Information, a coded failure as a warning with its
    /// stack, an unexpected one as an error (the client only sees the message).
    /// </summary>
    public static async Task<T> GuardAsync<T>(
        Func<Task<T>> body,
        ILogger? logger = null,
        string operation = "Request",
        object? request = null,
        LogLevel resultLevel = LogLevel.Information)
    {
        ArgumentNullException.ThrowIfNull(body);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await body().ConfigureAwait(false);
            if (logger is not null && logger.IsEnabled(resultLevel))
            {
                logger.Log(
                    resultLevel,
                    "{Operation} answered in {Seconds:N2} s: request {Request}; result {Result}",
                    operation,
                    Stopwatch.GetElapsedTime(started).TotalSeconds,
                    Provenance.Describe(request),
                    Provenance.Describe(result));
            }

            return result;
        }
        catch (LocalRpcException ex)
        {
            // Errors the worker creates as codes (an unknown snapshot or result set, a rejected argument) were logged
            // nowhere before sprint 2.3.7.
            logger?.LogInformation("{Operation} refused ({Code}): {Message}; request {Request}", operation, (HeapScopeErrorCode)ex.ErrorCode, ex.Message, Provenance.Describe(request));
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && CodeOf(ex) is { } code)
        {
            if (IsRefusal(code))
            {
                logger?.LogInformation("{Operation} refused ({Code}): {Message}; request {Request}", operation, code, MessageOf(ex, code), Provenance.Describe(request));
            }
            else
            {
                logger?.LogWarning(ex, "{Operation} failed with {Code}; request {Request}", operation, code, Provenance.Describe(request));
            }

            throw new LocalRpcException(MessageOf(ex, code), ex) { ErrorCode = (int)code, ErrorData = DataOf(ex) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "{Operation} failed unexpectedly; request {Request}", operation, Provenance.Describe(request));
            throw;
        }
    }
}
