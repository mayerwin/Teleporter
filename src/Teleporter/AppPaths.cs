using System;
using System.Diagnostics;
using System.IO;

namespace Teleporter;

/// <summary>
/// Where the app keeps its files. Portable by design: everything lives in a
/// <c>Teleporter.data</c> folder beside the exe, so moving or deleting the pair
/// takes the whole installation with it. Roaming AppData is only a fallback for
/// an exe in a read-only place (Program Files, a network share), decided by
/// actually writing a probe file rather than guessing from the path.
/// </summary>
internal static class AppPaths
{
    public static string Root { get; }
    public static bool IsPortable { get; }

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogFile => Path.Combine(Root, "teleporter.log");
    public static string SshConfigFile => Path.Combine(Root, "ssh_config");
    public static string AddonsDir => Path.Combine(Root, "addons");

    /// <summary>Full path of the running exe (the single-file bundle, not its temp extraction).</summary>
    public static string? ExePath { get; }

    static AppPaths()
    {
        ExePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        string? exeDir = ExePath is null ? null : Path.GetDirectoryName(ExePath);
        string name = Path.GetFileNameWithoutExtension(ExePath ?? "Teleporter");

        if (exeDir is not null && TryWritable(Path.Combine(exeDir, name + ".data")))
        {
            Root = Path.Combine(exeDir, name + ".data");
            IsPortable = true;
        }
        else
        {
            Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name);
            Directory.CreateDirectory(Root);
        }
    }

    private static bool TryWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
