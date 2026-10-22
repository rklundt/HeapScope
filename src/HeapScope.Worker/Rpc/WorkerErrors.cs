using HeapScope.Contracts;
using HeapScope.Core;
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
        OutOfMemoryException or InsufficientExecutionStackException => HeapScopeErrorCode.InsufficientMemory,
        ArgumentException => HeapScopeErrorCode.InvalidArgument,
        _ => null,
    };

    /// <summary>Runs an RPC method body, translating domain exceptions into coded errors.</summary>
    public static async Task<T> GuardAsync<T>(Func<Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not LocalRpcException and not OperationCanceledException && CodeOf(ex) is { } code)
        {
            throw new LocalRpcException(ex.Message, ex) { ErrorCode = (int)code };
        }
    }
}
