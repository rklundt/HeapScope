using System.Diagnostics;

namespace Fixtures.Leaks;

/// <summary>Ends a fixture process when the process that started it exits, so aborted runs leave no orphans.</summary>
public static class ParentWatch
{
    public static void ExitWhenParentExits(int? parentPid)
    {
        if (parentPid is not int pid)
        {
            return;
        }

        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            Environment.Exit(3);
            return;
        }

        parent.EnableRaisingEvents = true;
        parent.Exited += (_, _) => Environment.Exit(3);
        if (parent.HasExited)
        {
            Environment.Exit(3);
        }
    }
}
