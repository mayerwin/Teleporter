using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Teleporter;

/// <summary>One server Teleporter can send projects to.</summary>
public sealed class ServerProfile
{
    /// <summary>Short id, used in the ssh alias (<c>teleporter-&lt;Id&gt;</c>) VS Code connects to.</summary>
    public string Id { get; set; } = "server";
    public string DisplayName { get; set; } = "Server";

    /// <summary><c>openssh</c> (host/user below + your own agent) or <c>external</c> (a provider command).</summary>
    public string Provider { get; set; } = "openssh";

    /// <summary>For <c>external</c>: the command to run, e.g. a script that loads a key from a vault.</summary>
    public string? ProviderCommand { get; set; }

    public string Host { get; set; } = "";
    public string User { get; set; } = "dev";
    public int Port { get; set; } = 22;

    /// <summary>Where projects live on the server. <c>~</c> is the server account's home.</summary>
    public string ProjectsRoot { get; set; } = "~/projects";

    /// <summary>Optional llama-swap base URL, to show and unload loaded models in the server panel.</summary>
    public string? LlamaSwapUrl { get; set; }

    /// <summary>System services whose memory the server panel shows next to this account's, e.g. <c>nginx.service</c>.</summary>
    public List<string> WatchedServices { get; set; } = new();

    /// <summary>
    /// Optional Toolbx container in the server account (e.g. <c>my-toolbox</c>). When set, agents and
    /// terminals run inside it, where the account can install packages with sudo.
    /// </summary>
    public string? Container { get; set; }
}

public sealed class AppSettings
{
    public List<ServerProfile> Servers { get; set; } = new();
    public string? ActiveServerId { get; set; }

    /// <summary>Git for Windows' ssh. Windows' built-in ssh cannot talk to a private ssh-agent socket.</summary>
    public string SshPath { get; set; } = DefaultSshPath();

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Where set-aside local copies go. Empty = <c>%USERPROFILE%\Teleporter-parked</c> (or <c>&lt;drive&gt;:\Teleporter-parked</c> for projects on another drive).</summary>
    public string ParkingFolder { get; set; } = "";
    public int ParkDays { get; set; } = 30;

    /// <summary>
    /// Command that starts the Antigravity IDE (it receives <c>--folder-uri ...</c>). Empty = the IDE's own
    /// <c>antigravity-ide.cmd</c>. Point it at a launcher when the IDE needs extra flags, such as a proxy.
    /// </summary>
    public string AntigravityIdeCommand { get; set; } = "";

    /// <summary>Folder names skipped by default when moving a project: they hold OS-specific binaries that do not run on the other side.</summary>
    public List<string> SkipDirNames { get; set; } = new() { "node_modules", ".venv", "venv" };

    [JsonIgnore]
    public ServerProfile? ActiveServer =>
        Servers.Find(s => s.Id == ActiveServerId) ?? (Servers.Count > 0 ? Servers[0] : null);

    private static string DefaultSshPath()
    {
        string git = @"C:\Program Files\Git\usr\bin\ssh.exe";
        return File.Exists(git) ? git : "ssh";
    }
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        TypeInfoResolver = SettingsJsonContext.Default,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize(File.ReadAllText(AppPaths.SettingsFile), SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Warn($"settings.json unreadable, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        string tmp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        Core.AtomicFile.Replace(tmp, AppPaths.SettingsFile);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext
{
}
