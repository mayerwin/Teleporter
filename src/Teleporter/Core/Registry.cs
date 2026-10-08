using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Teleporter.Core;

/// <summary>What Teleporter remembers about a project it moved.</summary>
public sealed class ProjectRecord
{
    public string Name { get; set; } = "";
    public string ServerId { get; set; } = "";
    /// <summary>Where the project lived on the local PC before it was sent (the default for Bring back).</summary>
    public string? LocalPath { get; set; }
    /// <summary>Older registries' name for <see cref="LocalPath"/>: read, never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LaptopPath { get => null; set => LocalPath ??= value; }
    public bool IsGit { get; set; }
    /// <summary>Repo-local core.autocrlf before Teleporter set it to "input" on the server (null = unset).</summary>
    public string? AutoCrlfLocalBefore { get; set; }
    /// <summary>Linux file modes that differ from 0644/0755-by-git, keyed by relative path, recorded on Bring back.</summary>
    public Dictionary<string, string> Modes { get; set; } = new();
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? BroughtBackAt { get; set; }
}

/// <summary>A copy set aside after a move. Deleted only when the user confirms, after it expires.</summary>
public sealed class ParkedItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>"local" or "server" (older registries say "laptop" for local).</summary>
    public string Where { get; set; } = "local";
    public string ServerId { get; set; } = "";
    public string Project { get; set; } = "";
    /// <summary>"project" or "sessions".</summary>
    public string Kind { get; set; } = "project";
    public string OriginalPath { get; set; } = "";
    public string ParkedPath { get; set; } = "";
    public DateTimeOffset ParkedAt { get; set; } = DateTimeOffset.Now;
    public long Bytes { get; set; }

    public DateTimeOffset ExpiresAt(int days) => ParkedAt.AddDays(days);
}

public sealed class RegistryData
{
    public List<ProjectRecord> Projects { get; set; } = new();
    public List<ParkedItem> Parked { get; set; } = new();
}

/// <summary>
/// projects + set-aside copies, persisted as one JSON file in the data folder. Written
/// atomically (temp file + rename) after every change, so a crash mid-transfer never
/// leaves a half-written registry.
/// </summary>
public sealed class Registry
{
    private readonly string _file;
    private readonly object _gate = new();
    public RegistryData Data { get; private set; }

    public Registry(string file)
    {
        _file = file;
        Data = Load();
    }

    private RegistryData Load()
    {
        try
        {
            if (File.Exists(_file))
                return JsonSerializer.Deserialize(File.ReadAllText(_file), RegistryJsonContext.Default.RegistryData) ?? new RegistryData();
        }
        catch
        {
            // A corrupt registry must not stop the app; keep the bad file for inspection.
            try { File.Copy(_file, _file + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
        }
        return new RegistryData();
    }

    public void Save()
    {
        lock (_gate)
        {
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, RegistryJsonContext.Default.RegistryData));
            AtomicFile.Replace(tmp, _file);
        }
    }

    public ProjectRecord Project(string serverId, string name)
    {
        lock (_gate)
        {
            var p = Data.Projects.FirstOrDefault(x => x.ServerId == serverId && x.Name == name);
            if (p is null)
            {
                p = new ProjectRecord { ServerId = serverId, Name = name };
                Data.Projects.Add(p);
            }
            return p;
        }
    }

    public ProjectRecord? FindProject(string serverId, string name) =>
        Data.Projects.FirstOrDefault(x => x.ServerId == serverId && x.Name == name);

    public void AddParked(ParkedItem item)
    {
        lock (_gate) Data.Parked.Add(item);
        Save();
    }

    public void RemoveParked(string id)
    {
        lock (_gate) Data.Parked.RemoveAll(p => p.Id == id);
        Save();
    }

    public List<ParkedItem> Expired(int days) =>
        Data.Parked.Where(p => p.ExpiresAt(days) <= DateTimeOffset.Now).ToList();
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RegistryData))]
internal partial class RegistryJsonContext : JsonSerializerContext
{
}
