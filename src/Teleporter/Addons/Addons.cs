using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Teleporter.Connection;

namespace Teleporter.Addons;

/// <summary>
/// An add-on: a folder in <c>Teleporter.data/addons/&lt;name&gt;/</c> with an <c>addon.json</c> and
/// optional scripts. Add-ons are data, never code loaded into the app: their scripts run on the
/// server over ssh, and <see cref="AgentEnv"/> is extra environment the app adds when it starts an
/// agent (for example a per-process proxy). This is how infrastructure specific to one person
/// stays out of the public app.
/// </summary>
public sealed class Addon
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    /// <summary>Agent kind ("antigravity", "claude") to environment variables.</summary>
    [JsonPropertyName("agentEnv")] public Dictionary<string, Dictionary<string, string>> AgentEnv { get; set; } = new();
    /// <summary>Script (relative to the add-on folder) run on the server; lines "TELEPORTER:key=value" are shown.</summary>
    [JsonPropertyName("status")] public string? StatusScript { get; set; }
    /// <summary>Optional script run on the server before an agent of the given kind is installed or started.</summary>
    [JsonPropertyName("prepare")] public string? PrepareScript { get; set; }

    [JsonIgnore] public string Folder { get; set; } = "";
}

public sealed record AddonStatus(string Addon, List<(string Key, string Value)> Values, string? Error);

internal sealed class AddonSet
{
    public List<Addon> Items { get; } = new();

    public static AddonSet Load(string dir)
    {
        var set = new AddonSet();
        if (!Directory.Exists(dir)) return set;
        foreach (string folder in Directory.EnumerateDirectories(dir))
        {
            string manifest = Path.Combine(folder, "addon.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                var addon = JsonSerializer.Deserialize(File.ReadAllText(manifest), AddonJsonContext.Default.Addon);
                if (addon is null) continue;
                addon.Folder = folder;
                if (string.IsNullOrWhiteSpace(addon.Name)) addon.Name = Path.GetFileName(folder);
                set.Items.Add(addon);
            }
            catch (Exception ex)
            {
                Log.Warn($"add-on {folder} ignored: {ex.Message}");
            }
        }
        return set;
    }

    /// <summary>Environment every add-on asks for, for one agent kind. Later add-ons win on a clash.</summary>
    public Dictionary<string, string> EnvFor(string agentKind)
    {
        var env = new Dictionary<string, string>();
        foreach (var a in Items)
            if (a.AgentEnv.TryGetValue(agentKind, out var vars))
                foreach (var kv in vars) env[kv.Key] = kv.Value;
        return env;
    }

    public async Task<List<AddonStatus>> StatusAsync(SshClient ssh)
    {
        var results = new List<AddonStatus>();
        foreach (var a in Items.Where(a => a.StatusScript is not null))
        {
            try
            {
                string script = await File.ReadAllTextAsync(Path.Combine(a.Folder, a.StatusScript!));
                var r = await ssh.RunScriptAsync(script, timeout: TimeSpan.FromSeconds(30));
                var values = r.StdOut.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.StartsWith("TELEPORTER:", StringComparison.Ordinal))
                    .Select(l => l["TELEPORTER:".Length..].Split('=', 2))
                    .Where(p => p.Length == 2)
                    .Select(p => (p[0], p[1]))
                    .ToList();
                results.Add(new AddonStatus(a.Name, values, r.Ok ? null : r.StdErr.Trim()));
            }
            catch (Exception ex)
            {
                results.Add(new AddonStatus(a.Name, new(), ex.Message));
            }
        }
        return results;
    }

    public async Task PrepareAsync(SshClient ssh, string agentKind)
    {
        foreach (var a in Items.Where(a => a.PrepareScript is not null && a.AgentEnv.ContainsKey(agentKind)))
        {
            string script = await File.ReadAllTextAsync(Path.Combine(a.Folder, a.PrepareScript!));
            var r = await ssh.RunScriptAsync(script, timeout: TimeSpan.FromMinutes(5));
            if (!r.Ok) throw new InvalidOperationException($"Add-on '{a.Name}' could not prepare: {r.StdErr.Trim()}");
        }
    }
}

[JsonSerializable(typeof(Addon))]
internal partial class AddonJsonContext : JsonSerializerContext
{
}
