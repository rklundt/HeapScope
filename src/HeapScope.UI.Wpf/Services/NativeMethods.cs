using System.Runtime.InteropServices;

namespace HeapScope.UI.Wpf.Services;

/// <summary>Win32 calls used for diagnostics.</summary>
internal static class NativeMethods
{
    /// <summary>CPU time (kernel plus user, in 100 ns units) the calling thread has used so far.</summary>
    public static long CurrentThreadCpuTime() =>
        GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user) ? kernel + user : 0;

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
}
