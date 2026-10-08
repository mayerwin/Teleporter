using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Teleporter.Connection;

namespace Teleporter;

/// <summary>
/// Opens a server folder in VS Code over Remote-SSH. Teleporter keeps its own ssh config
/// (in the data folder) with one <c>Host teleporter-&lt;id&gt;</c> entry per server, which carries
/// the private agent socket. VS Code is pointed at it once, with the user's consent,
/// through two user settings: <c>remote.SSH.configFile</c> and <c>remote.SSH.path</c>
/// (Git's ssh, because Windows' ssh cannot use that socket).
/// </summary>
internal static class VsCode
{
    public static string HostAlias(ServerProfile p) => "teleporter-" + p.Id;

    private static string UserSettingsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "settings.json");

    public static void WriteSshConfig(ServerProfile p, SshTarget t)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Written by Teleporter. Regenerated on every connection; edits are lost.");
        sb.AppendLine($"Host {HostAlias(p)}");
        sb.AppendLine($"  HostName {t.Host}");
        sb.AppendLine($"  User {t.User}");
        sb.AppendLine($"  Port {t.Port}");
        sb.AppendLine($"  UserKnownHostsFile \"{SshClient.ToSshPath(SshClient.KnownHostsFile)}\"");
        sb.AppendLine("  StrictHostKeyChecking accept-new");
        sb.AppendLine("  ServerAliveInterval 15");
        if (t.IdentityAgent is not null)
            sb.AppendLine($"  IdentityAgent \"{SshClient.ToSshPath(t.IdentityAgent)}\"");
        File.WriteAllText(AppPaths.SshConfigFile, sb.ToString());
    }

    /// <summary>True when VS Code already uses Teleporter's ssh config and ssh.exe.</summary>
    public static bool IsConfigured(string sshPath)
    {
        try
        {
            if (!File.Exists(UserSettingsFile)) return false;
            string text = File.ReadAllText(UserSettingsFile);
            return text.Contains(JsonEscape(SshClient.ToSshPath(AppPaths.SshConfigFile)), StringComparison.OrdinalIgnoreCase)
                && text.Contains(JsonEscape(sshPath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Set the two Remote-SSH settings, keeping a timestamped backup of settings.json.
    /// The file is JSON with comments, so it is edited as text (replace the value when the
    /// key exists, otherwise insert after the opening brace) rather than re-serialised,
    /// which would drop the user's comments.
    /// </summary>
    public static string Configure(string sshPath, string hostAlias)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserSettingsFile)!);
        string text = File.Exists(UserSettingsFile) ? File.ReadAllText(UserSettingsFile) : "{\n}\n";
        string backup = UserSettingsFile + $".teleporter-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        if (File.Exists(UserSettingsFile)) File.Copy(UserSettingsFile, backup);

        text = SetKey(text, "remote.SSH.configFile", SshClient.ToSshPath(AppPaths.SshConfigFile));
        text = SetKey(text, "remote.SSH.path", sshPath);
        // Without this, Remote-SSH stops on a "Select the platform of the remote host" prompt.
        text = SetMapEntry(text, "remote.SSH.remotePlatform", hostAlias, "linux");
        // Extensions that run on the remote side (Claude Code is one) must be installed on each
        // server; this makes Remote-SSH install it the first time it connects to a new one.
        text = AddToArray(text, "remote.SSH.defaultExtensions", "anthropic.claude-code");
        File.WriteAllText(UserSettingsFile, text);
        Log.Info("VS Code Remote-SSH settings configured");
        return backup;
    }

    internal static string SetKey(string text, string key, string value)
    {
        string jsonValue = "\"" + JsonEscape(value) + "\"";
        var existing = new Regex("\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?:[^\"\\\\]|\\\\.)*\"");
        if (existing.IsMatch(text))
            return existing.Replace(text, $"\"{key}\": {jsonValue}", 1);
        int brace = text.IndexOf('{');
        if (brace < 0) throw new InvalidOperationException("VS Code settings.json has no opening brace.");
        bool empty = text[(brace + 1)..].TrimStart().StartsWith('}');
        string insert = $"\n    \"{key}\": {jsonValue}" + (empty ? "\n" : ",");
        return text.Insert(brace + 1, insert);
    }

    private static string SetMapEntry(string text, string key, string entry, string value)
    {
        string pair = $"\"{JsonEscape(entry)}\": \"{JsonEscape(value)}\"";
        var map = new Regex("\"" + Regex.Escape(key) + @"""\s*:\s*\{");
        Match m = map.Match(text);
        if (!m.Success)
        {
            int brace = text.IndexOf('{');
            if (brace < 0) throw new InvalidOperationException("VS Code settings.json has no opening brace.");
            return text.Insert(brace + 1, $"\n    \"{key}\": {{ {pair} }},");
        }
        int open = m.Index + m.Length;
        int close = text.IndexOf('}', open);
        string body = text[open..close];
        var existing = new Regex("\"" + Regex.Escape(entry) + @"""\s*:\s*""[^""]*""");
        if (existing.IsMatch(body))
            return text[..open] + existing.Replace(body, pair, 1) + text[close..];
        bool empty = body.Trim().Length == 0;
        return text.Insert(open, " " + pair + (empty ? " " : ","));
    }

    private static string AddToArray(string text, string key, string value)
    {
        string item = "\"" + JsonEscape(value) + "\"";
        var arr = new Regex("\"" + Regex.Escape(key) + @"""\s*:\s*\[");
        Match m = arr.Match(text);
        if (!m.Success)
        {
            int brace = text.IndexOf('{');
            if (brace < 0) throw new InvalidOperationException("VS Code settings.json has no opening brace.");
            return text.Insert(brace + 1, $"\n    \"{key}\": [{item}],");
        }
        int open = m.Index + m.Length;
        int close = text.IndexOf(']', open);
        string body = text[open..close];
        if (body.Contains(item, StringComparison.OrdinalIgnoreCase)) return text;
        bool empty = body.Trim().Length == 0;
        return text.Insert(open, item + (empty ? "" : ", "));
    }

    private static string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public static void OpenFolder(ServerProfile p, string remotePath)
    {
        string uri = $"vscode-remote://ssh-remote+{HostAlias(p)}{remotePath}";
        // code.cmd is a batch file; Process.Start cannot run it without the shell.
        Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/d", "/c", "code", "--folder-uri", uri },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Log.Info($"opened VS Code on {HostAlias(p)}:{remotePath}");
    }
}
