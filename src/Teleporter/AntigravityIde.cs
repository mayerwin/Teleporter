using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Teleporter.Connection;

namespace Teleporter;

/// <summary>
/// Opens a server folder in the Antigravity IDE (a VS Code fork) over its own Remote-SSH
/// extension (<c>antigravity-remote-openssh</c>). Three quirks of that extension, all checked in
/// its source (2026-10-06):
///  - its settings are <c>remote.antigravitySSH.configFile</c> / <c>.path</c>;
///  - it runs <c>"{path} -V"</c> through a shell without quotes, so a path with a space
///    ("C:\Program Files\Git\...") fails: the 8.3 short path is written instead;
///  - it reads the config file to look up the host, but launches ssh WITHOUT <c>-F</c>, so ssh
///    only sees <c>~/.ssh/config</c>: an <c>Include</c> of Teleporter's config goes there, in the
///    MSYS spelling (<c>/c/Users/...</c>) that Git's ssh reads and Windows' own ssh ignores.
///    Windows' ssh would otherwise read the file, reject it over its folder permissions, and refuse
///    every connection.
/// The Antigravity agent in such a window runs on the local PC (its language server is the
/// Windows one), so no server-side proxy is needed for the IDE.
/// </summary>
internal static partial class AntigravityIde
{
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string SettingsFile => Path.Combine(Roaming, "Antigravity IDE", "User", "settings.json");
    private static string DefaultLauncher => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Antigravity IDE", "bin", "antigravity-ide.cmd");
    private static string UserSshConfig => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    public static bool Installed => File.Exists(DefaultLauncher) || Directory.Exists(Path.GetDirectoryName(SettingsFile));

    public static bool IsConfigured()
    {
        try
        {
            return File.Exists(SettingsFile) && File.ReadAllText(SettingsFile).Contains("remote.antigravitySSH.configFile", StringComparison.Ordinal)
                && File.Exists(UserSshConfig) && File.ReadAllText(UserSshConfig).Contains(MsysPath(AppPaths.SshConfigFile), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Set the extension's two settings and the ~/.ssh/config Include. Backups are kept beside both files.</summary>
    public static string Configure(string sshPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        string text = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : "{\n}\n";
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        if (File.Exists(SettingsFile)) File.Copy(SettingsFile, SettingsFile + $".teleporter-{stamp}.bak");
        text = VsCode.SetKey(text, "remote.antigravitySSH.configFile", SshClient.ToSshPath(AppPaths.SshConfigFile));
        text = VsCode.SetKey(text, "remote.antigravitySSH.path", ShortPath(sshPath));
        File.WriteAllText(SettingsFile, text);

        string include = "Include " + MsysPath(AppPaths.SshConfigFile);
        Directory.CreateDirectory(Path.GetDirectoryName(UserSshConfig)!);
        string existing = File.Exists(UserSshConfig) ? File.ReadAllText(UserSshConfig) : "";
        if (!existing.Contains(include, StringComparison.OrdinalIgnoreCase))
        {
            if (existing.Length > 0) File.Copy(UserSshConfig, UserSshConfig + $".teleporter-{stamp}.bak");
            // At the top: an Include after a Host line would only apply inside that Host block.
            var sb = new StringBuilder();
            sb.Append("# Added by Teleporter: its servers (teleporter-*), for tools that only read this file (Antigravity IDE).\n");
            sb.Append("# MSYS path on purpose: Git for Windows' ssh reads it; Windows' own ssh ignores it.\n");
            sb.Append(include).Append('\n');
            if (existing.Length > 0) sb.Append('\n').Append(existing);
            File.WriteAllText(UserSshConfig, sb.ToString());
        }
        Log.Info("Antigravity IDE Remote-SSH configured");
        return SettingsFile + $".teleporter-{stamp}.bak";
    }

    /// <summary>Open the folder through the configured launcher (e.g. one that adds a proxy), or the IDE's own CLI.</summary>
    public static void OpenFolder(ServerProfile p, string remotePath, string? launcher)
    {
        string command = string.IsNullOrWhiteSpace(launcher) ? DefaultLauncher : launcher;
        string uri = $"vscode-remote://ssh-remote+{VsCode.HostAlias(p)}{remotePath}";
        Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/d", "/c", command, "--folder-uri", uri },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Log.Info($"opened Antigravity IDE on {VsCode.HostAlias(p)}:{remotePath}");
    }

    private static string MsysPath(string windowsPath)
    {
        string p = Path.GetFullPath(windowsPath).Replace('\\', '/');
        return p.Length > 2 && p[1] == ':' ? "/" + char.ToLowerInvariant(p[0]) + p[2..] : p;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetShortPathName(string longPath, [Out] char[] shortPath, uint size);

    /// <summary>8.3 form of a path with spaces, when the volume has short names; otherwise the path unchanged.</summary>
    private static string ShortPath(string path)
    {
        if (!path.Contains(' ')) return path;
        var buffer = new char[512];
        uint n = GetShortPathName(path, buffer, (uint)buffer.Length);
        string shortPath = n > 0 && n < buffer.Length ? new string(buffer, 0, (int)n) : path;
        if (shortPath.Contains(' ')) Log.Warn("no 8.3 name for the ssh path; Antigravity's Remote-SSH will fail to run it");
        return shortPath;
    }
}
