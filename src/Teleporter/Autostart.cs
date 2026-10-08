using System;
using Microsoft.Win32;

namespace Teleporter;

/// <summary>
/// "Start with Windows" through the per-user Run key: the one autostart method that
/// writes nothing outside the data folder (a Startup-folder shortcut would live in
/// %APPDATA%).
///
/// Task Manager and Settings > Apps > Startup never delete the Run value; they
/// record "disabled" in Explorer\StartupApproved\Run as a 12-byte value whose first
/// byte is odd (03 = disabled, 02 = enabled). So "is autostart on" must read both,
/// and the app only re-enables an entry the user disabled there when the user turns
/// it on inside the app.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Teleporter";

    private static string Command => $"\"{AppPaths.ExePath}\" --tray";

    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] state || state.Length == 0 || (state[0] & 1) == 0;
        }
    }

    public static void Enable()
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            run.SetValue(ValueName, Command, RegistryValueKind.String);
        using (var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey))
            approved.SetValue(ValueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        Log.Info("autostart enabled");
    }

    public static void Disable()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
        using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true))
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Info("autostart disabled");
    }

    /// <summary>The exe is portable and may have moved since autostart was turned on.</summary>
    public static void RepairPathIfMoved()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            {
                run.SetValue(ValueName, Command, RegistryValueKind.String);
                Log.Info("autostart path updated after the exe moved");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"autostart path check failed: {ex.Message}");
        }
    }
}
