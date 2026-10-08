using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace Teleporter;

internal static partial class Idle
{
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr process);

    /// <summary>Collect, compact and hand the working set back after the window closes.</summary>
    public static void Trim()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }
        catch (Exception ex)
        {
            Log.Warn($"trim failed: {ex.Message}");
        }
    }
}
