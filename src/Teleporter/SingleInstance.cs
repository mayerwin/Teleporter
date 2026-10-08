using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Teleporter;

/// <summary>
/// One Teleporter per Windows user, wherever the exe lives: two copies would fight over VS Code's
/// ssh config and the agent socket. A second launch asks the running one to show its window
/// (creating it if it was closed to the tray, restoring it if minimized) and exits.
///
/// The listener blocks on WaitAny with no timeout (activate or stop), so an idle app gets zero
/// wake-ups from it. Names are Local\ (per user session).
/// </summary>
internal static partial class SingleInstance
{
    private const string MutexName = @"Local\Teleporter-single-instance";
    private const string EventName = @"Local\Teleporter-activate";
    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;
    private static readonly ManualResetEvent Stop = new(false);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    public static event Action? ActivationRequested;

    public static bool TryBecomePrimary()
    {
        try
        {
            _mutex = new Mutex(false, MutexName);
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            try { return _mutex.WaitOne(TimeSpan.Zero); }
            catch (AbandonedMutexException) { return true; }
        }
        catch
        {
            return true;
        }
    }

    public static void SignalPrimary()
    {
        // Windows only lets the foreground process hand focus on; this launch is foreground (the
        // user just started it), so it allows the running instance to bring its window to the front.
        try { AllowSetForegroundWindow(-1); } catch { }
        try { _activate?.Set(); } catch { }
    }

    public static void StartListening()
    {
        if (_activate is null) return;
        var handles = new WaitHandle[] { _activate, Stop };
        new Thread(() =>
        {
            while (WaitHandle.WaitAny(handles) == 0)
                ActivationRequested?.Invoke();
        })
        { IsBackground = true, Name = "SingleInstanceListener" }.Start();
    }

    public static void Release()
    {
        Stop.Set();
        try { _mutex?.ReleaseMutex(); } catch { }
    }
}
