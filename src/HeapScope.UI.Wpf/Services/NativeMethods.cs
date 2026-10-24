using System.Runtime.InteropServices;

namespace HeapScope.UI.Wpf.Services;

/// <summary>Win32 calls used for diagnostics and scheduling.</summary>
internal static class NativeMethods
{
    private const int ProcessPowerThrottling = 4;
    private const uint ExecutionSpeedThrottling = 0x1;

    /// <summary>
    /// Opts this process out of execution speed throttling (EcoQoS): Windows then keeps its threads on performance cores
    /// even when its window is not in the foreground. On a hybrid CPU the UI thread on an efficiency core took 2.5 to 5
    /// times as long for the same layout (sprint 1.5 smoke runs). Returns false where Windows does not support it.
    /// </summary>
    public static bool DisableExecutionSpeedThrottling()
    {
        var state = new PowerThrottlingState { Version = 1, ControlMask = ExecutionSpeedThrottling, StateMask = 0 };
        return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
    }

    /// <summary>CPU time (kernel plus user, in 100 ns units) the calling thread has used so far.</summary>
    public static long CurrentThreadCpuTime() =>
        GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user) ? kernel + user : 0;

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
}
