using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Teleporter.Connection;

namespace Teleporter;

/// <summary>
/// Interactive ssh in its own terminal window. Windows Terminal is used when installed: unlike
/// the classic console window it pastes with Ctrl+V and right-click and supports bracketed paste,
/// which TUI programs on the server rely on.
/// </summary>
internal static class Terminal
{
    private static string? WindowsTerminal()
    {
        string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        return File.Exists(wt) ? wt : null;
    }

    public static async Task OpenAsync(SshClient ssh, string title, string remoteCommand)
    {
        var (sshPath, args) = await ssh.InteractiveArgsAsync(remoteCommand);
        ProcessStartInfo psi;
        if (WindowsTerminal() is { } wt)
        {
            psi = new ProcessStartInfo(wt) { UseShellExecute = false };
            foreach (string a in new[] { "-w", "new", "new-tab", "--title", "Teleporter: " + title, "--", sshPath }) psi.ArgumentList.Add(a);
            // wt splits its command line on ';' (new tab/pane commands) unless escaped.
            foreach (string a in args) psi.ArgumentList.Add(a.Replace(";", "\\;"));
        }
        else
        {
            // A GUI app starting a console program without CreateNoWindow gets it a new console window.
            psi = new ProcessStartInfo(sshPath) { UseShellExecute = false, CreateNoWindow = false };
            foreach (string a in args) psi.ArgumentList.Add(a);
        }
        Process.Start(psi);
        Log.Info($"opened terminal: {title}");
    }

    /// <summary>Shell in a folder, inside the profile's container when there is one.</summary>
    public static string ShellIn(string dir, string? container) =>
        string.IsNullOrWhiteSpace(container)
            ? $"cd {SshClient.ShellQuote(dir)} && exec bash -l"
            : $"cd {SshClient.ShellQuote(dir)} && exec toolbox enter {container}";
}
