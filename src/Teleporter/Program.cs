using System;
using Avalonia;

namespace Teleporter;

internal static class Program
{
    /// <summary>True when launched by "Start with Windows" (<c>--tray</c>): start with no window.</summary>
    public static bool StartInTray { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        StartInTray = Array.Exists(args, a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        int open = Array.FindIndex(args, a => a.Equals("--open", StringComparison.OrdinalIgnoreCase));
        if (open >= 0 && open + 1 < args.Length) OpenRequest.Write(args[open + 1]);

        if (!SingleInstance.TryBecomePrimary())
        {
            SingleInstance.SignalPrimary();
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"FATAL {e.ExceptionObject}");
        Log.Info($"Teleporter starting (data: {AppPaths.Root}, portable: {AppPaths.IsPortable})");
        Autostart.RepairPathIfMoved();

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            SingleInstance.Release();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Measured 2026-10-06: with the default DirectComposition mode, Avalonia keeps a
            // vblank-waiting render thread alive even with no window open (31 ms CPU per
            // minute, tray only). The redirection surface has no such thread: 0 ms per minute.
            // Software rendering too: once a window had opened on the GPU path, the D3D driver's
            // threads (~50) stayed behind after it closed and kept waking (47 ms per minute).
            // A settings-and-status window paints fine on the CPU, and only while visible.
            .With(new Win32PlatformOptions
            {
                CompositionMode = new[] { Win32CompositionMode.RedirectionSurface },
                RenderingMode = new[] { Win32RenderingMode.Software },
            })
            .WithInterFont();
}
