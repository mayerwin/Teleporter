using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Teleporter.Core;

namespace Teleporter.Sessions;

public sealed record SessionSummary(List<string> Folders, int Sessions, bool HasMemory, DateTime? LastWrite);

/// <summary>
/// The local side of moving Claude Code sessions and memory with a project. Claude keeps
/// them in <c>~/.claude/projects/&lt;encoded working dir&gt;/</c>: one <c>&lt;session&gt;.jsonl</c> per
/// session, a <c>&lt;session&gt;/</c> folder (tool results, subagents), and <c>memory/</c>. Edit
/// checkpoints live in <c>~/.claude/file-history/&lt;session&gt;/</c>. Folders are found by the
/// <c>cwd</c> recorded inside the transcripts, not by guessing the encoded name, because the same
/// project can have both a <c>C--</c> (CLI) and a <c>c--</c> (VS Code) folder.
/// </summary>
public sealed class ClaudeSessions(string claudeRoot)
{
    public static ClaudeSessions ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));

    public string Root => claudeRoot;

    public List<string> FindProjectFolders(string projectPath)
    {
        var found = new List<string>();
        string projects = Path.Combine(claudeRoot, "projects");
        if (!Directory.Exists(projects)) return found;
        string key = PathMap.Norm(projectPath);
        foreach (string dir in Directory.EnumerateDirectories(projects))
        {
            foreach (string jsonl in Directory.EnumerateFiles(dir, "*.jsonl").Take(5))
            {
                string? cwd = FirstCwd(jsonl);
                if (cwd is null) continue;
                if (PathMap.Norm(cwd) == key) found.Add(dir);
                break;
            }
        }
        return found;
    }

    public SessionSummary Summarize(string projectPath)
    {
        var folders = FindProjectFolders(projectPath);
        var jsonls = folders.SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl")).ToList();
        DateTime? last = jsonls.Count == 0 ? null : jsonls.Max(File.GetLastWriteTime);
        return new SessionSummary(folders.Select(Path.GetFileName).ToList()!, jsonls.Count,
            folders.Any(d => Directory.Exists(Path.Combine(d, "memory"))), last);
    }

    /// <summary>
    /// Copy the project's sessions into <paramref name="staging"/> laid out as they must land
    /// in the destination home (<c>.claude/projects/&lt;targetFolder&gt;/…</c>, <c>.claude/file-history/…</c>),
    /// with every transcript's <c>cwd</c> rewritten from the local path to the server path.
    /// </summary>
    public int Stage(string projectPath, string newRoot, string targetFolder, string staging)
    {
        string destProj = Path.Combine(staging, ".claude", "projects", targetFolder);
        Directory.CreateDirectory(destProj);
        var sessions = new List<string>();
        foreach (string dir in FindProjectFolders(projectPath))
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                string name = Path.GetFileName(file);
                string dst = Path.Combine(destProj, name);
                if (name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                {
                    sessions.Add(name[..^6]);
                    RewriteJsonl(file, dst, projectPath, newRoot);
                }
                else if (!File.Exists(dst))
                {
                    File.Copy(file, dst);
                }
            }
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                string dst = Path.Combine(destProj, Path.GetFileName(sub));
                if (!Directory.Exists(dst)) CopyTree(sub, dst);
            }
        }
        foreach (string id in sessions)
        {
            string fh = Path.Combine(claudeRoot, "file-history", id);
            if (Directory.Exists(fh)) CopyTree(fh, Path.Combine(staging, ".claude", "file-history", id));
        }
        return sessions.Count;
    }

    /// <summary>Move the project's folders (and their file-history) into <paramref name="parkDir"/>.</summary>
    public void Park(string projectPath, string parkDir)
    {
        foreach (string dir in FindProjectFolders(projectPath))
        {
            var ids = Directory.EnumerateFiles(dir, "*.jsonl").Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
            string target = Path.Combine(parkDir, "projects", Path.GetFileName(dir));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(dir, target);
            foreach (string id in ids)
            {
                string fh = Path.Combine(claudeRoot, "file-history", id);
                if (!Directory.Exists(fh)) continue;
                string fhTarget = Path.Combine(parkDir, "file-history", id);
                Directory.CreateDirectory(Path.GetDirectoryName(fhTarget)!);
                Directory.Move(fh, fhTarget);
            }
        }
    }

    /// <summary>
    /// Move a staged <c>.claude</c> tree into this user's <c>~/.claude</c> without overwriting:
    /// a clash is kept beside the existing file as <c>name.teleporter-&lt;time&gt;</c>.
    /// </summary>
    public int MergeFromStaging(string stagedClaude)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        int renamed = 0;
        foreach (string file in Directory.EnumerateFiles(stagedClaude, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(stagedClaude, file);
            string dst = Path.Combine(claudeRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (File.Exists(dst)) { dst += ".teleporter-" + stamp; renamed++; }
            File.Move(file, dst);
        }
        return renamed;
    }

    /// <summary>Rewrite top-level <c>cwd</c> fields; every other byte of the transcript is kept as is.</summary>
    public static void RewriteJsonl(string src, string dst, string oldRoot, string newRoot)
    {
        using var reader = new StreamReader(src, new UTF8Encoding(false));
        using var writer = new StreamWriter(dst, false, new UTF8Encoding(false)) { NewLine = "\n" };
        string? line;
        while ((line = reader.ReadLine()) is not null)
            writer.WriteLine(RewriteLine(line, oldRoot, newRoot));
    }

    public static string RewriteLine(string line, string oldRoot, string newRoot)
    {
        if (!line.Contains("\"cwd\"", StringComparison.Ordinal)) return line;
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return line; }
        if (node is not JsonObject obj || obj["cwd"] is not JsonValue v || !v.TryGetValue(out string? cwd)) return line;
        string? mapped = PathMap.MapCwd(cwd, oldRoot, newRoot);
        if (mapped is null) return line;
        obj["cwd"] = mapped;
        return obj.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static string? FirstCwd(string jsonl)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(jsonl, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            for (int i = 0; i < 200 && reader.ReadLine() is { } line; i++)
            {
                if (!line.Contains("\"cwd\"", StringComparison.Ordinal)) continue;
                try
                {
                    if (JsonNode.Parse(line) is JsonObject o && o["cwd"] is JsonValue v && v.TryGetValue(out string? cwd)) return cwd;
                }
                catch (JsonException) { }
            }
        }
        catch (IOException) { }
        return null;
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }
}
