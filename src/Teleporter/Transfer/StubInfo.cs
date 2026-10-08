using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Teleporter.Transfer;

/// <summary>
/// The small folder left where a project used to be on the local PC: a README, an
/// "Open on server" launcher and a marker. It tells anyone (and Dropbox) the project moved,
/// and lets Bring back put the project exactly where it was. Teleporter only ever deletes a
/// folder when it contains nothing but these three files.
/// </summary>
public sealed class StubInfo
{
    public const string MarkerName = ".teleporter-stub.json";
    public const string ReadmeName = "README - moved to the server.txt";
    public const string LauncherName = "Open on server.cmd";

    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Project { get; set; } = "";
    public string ServerPath { get; set; } = "";
    public DateTimeOffset MovedAt { get; set; }

    public static StubInfo? Read(string folder)
    {
        try
        {
            string marker = Path.Combine(folder, MarkerName);
            return File.Exists(marker) ? JsonSerializer.Deserialize(File.ReadAllText(marker), StubJsonContext.Default.StubInfo) : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Write(string folder, ServerProfile server, string project, string serverPath)
    {
        Directory.CreateDirectory(folder);
        var info = new StubInfo { ServerId = server.Id, ServerName = server.DisplayName, Project = project, ServerPath = serverPath, MovedAt = DateTimeOffset.Now };
        File.WriteAllText(Path.Combine(folder, MarkerName), JsonSerializer.Serialize(info, StubJsonContext.Default.StubInfo));
        File.WriteAllText(Path.Combine(folder, ReadmeName),
            $"This project was moved to {server.DisplayName} ({server.User}@{server.Host}) on {info.MovedAt:yyyy-MM-dd HH:mm}.\r\n" +
            $"It now lives at: {serverPath}\r\n\r\n" +
            "Open it with Teleporter (Projects tab), or double-click \"Open on server.cmd\".\r\n" +
            "To bring it back, use Teleporter's Bring back; it will return to this folder.\r\n" +
            "The local copy was set aside, not deleted: see Teleporter's \"Set-aside copies\".\r\n");
        File.WriteAllText(Path.Combine(folder, LauncherName),
            $"@echo off\r\nstart \"\" \"{AppPaths.ExePath}\" --open \"{server.Id}/{project}\"\r\n");
        try { File.SetAttributes(Path.Combine(folder, MarkerName), FileAttributes.Hidden); } catch { }
    }

    /// <summary>Delete the stub, but only if it holds nothing else (anything added since is kept and reported).</summary>
    public static void Remove(string folder)
    {
        var known = new[] { MarkerName, ReadmeName, LauncherName, "desktop.ini" };
        var others = Directory.EnumerateFileSystemEntries(folder).Where(e => !known.Contains(Path.GetFileName(e), StringComparer.OrdinalIgnoreCase)).ToList();
        if (others.Count > 0)
            throw new InvalidOperationException($"The stub folder {folder} now contains other files ({string.Join(", ", others.Select(Path.GetFileName).Take(3))}). Move them out first.");
        foreach (string f in Directory.EnumerateFiles(folder))
        {
            File.SetAttributes(f, FileAttributes.Normal);
            File.Delete(f);
        }
        Directory.Delete(folder);
    }
}

[JsonSerializable(typeof(StubInfo))]
internal partial class StubJsonContext : JsonSerializerContext
{
}
