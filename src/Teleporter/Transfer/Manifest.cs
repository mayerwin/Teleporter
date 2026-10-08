using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Teleporter.Transfer;

/// <summary>One regular file: path relative to the project root (always with '/'), size, SHA-256.</summary>
public sealed record ManifestEntry(string Path, long Size, string Sha256, string? Mode = null);

public sealed class Manifest
{
    public Dictionary<string, ManifestEntry> Files { get; } = new(StringComparer.Ordinal);
    /// <summary>Empty directories (relative, '/'), so they survive the trip too.</summary>
    public SortedSet<string> EmptyDirs { get; } = new(StringComparer.Ordinal);
    /// <summary>Symlinks, junctions and other reparse points: reported, never followed or copied.</summary>
    public List<string> Links { get; } = new();
    /// <summary>Files that could not be read (locked by another process, permission denied).</summary>
    public List<string> Unreadable { get; } = new();

    public long TotalBytes => Files.Values.Sum(f => f.Size);

    /// <summary>
    /// Every difference between what was sent and what arrived. Empty means the copy is
    /// byte-for-byte complete: same file set, same sizes, same hashes.
    /// </summary>
    public static List<string> Compare(Manifest source, Manifest dest)
    {
        var problems = new List<string>();
        foreach (var (path, s) in source.Files)
        {
            if (!dest.Files.TryGetValue(path, out var d)) problems.Add($"missing: {path}");
            else if (d.Size != s.Size) problems.Add($"size differs: {path} ({s.Size} vs {d.Size})");
            else if (!string.Equals(d.Sha256, s.Sha256, StringComparison.OrdinalIgnoreCase)) problems.Add($"content differs: {path}");
        }
        foreach (string path in dest.Files.Keys)
            if (!source.Files.ContainsKey(path)) problems.Add($"unexpected extra file: {path}");
        foreach (string dir in source.EmptyDirs)
            if (!dest.EmptyDirs.Contains(dir) && !dest.Files.Keys.Any(f => f.StartsWith(dir + "/", StringComparison.Ordinal)))
                problems.Add($"missing empty folder: {dir}");
        return problems;
    }

    /// <summary>Parse the JSON lines printed by <see cref="RemoteScripts.Manifest"/>.</summary>
    public static Manifest FromJsonLines(string text)
    {
        var m = new Manifest();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            string kind = r.GetProperty("k").GetString() ?? "f";
            string path = r.GetProperty("p").GetString() ?? "";
            switch (kind)
            {
                case "f":
                    m.Files[path] = new ManifestEntry(path, r.GetProperty("s").GetInt64(), r.GetProperty("h").GetString() ?? "",
                        r.TryGetProperty("m", out var mode) ? mode.GetString() : null);
                    break;
                case "d": m.EmptyDirs.Add(path); break;
                case "l": m.Links.Add(path); break;
                case "u": m.Unreadable.Add(path); break;
            }
        }
        return m;
    }

    /// <summary>
    /// Hash a local folder. Reparse points are reported and not followed (a junction into
    /// another tree would otherwise be copied whole). Files are opened sharing read/write,
    /// so one another process holds exclusively shows up as unreadable instead of failing
    /// the whole scan.
    /// </summary>
    public static Manifest BuildLocal(string root, ISet<string> skipDirNames, IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        var m = new Manifest();
        var files = new List<(string full, string rel, long size)>();
        Walk(root, "", skipDirNames, m, files, ct);

        long total = files.Sum(f => f.size), done = 0;
        var results = new ConcurrentBag<ManifestEntry>();
        var unreadable = new ConcurrentBag<string>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount), CancellationToken = ct }, f =>
        {
            try
            {
                using var fs = new FileStream(f.full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
                byte[] hash = SHA256.HashData(fs);
                results.Add(new ManifestEntry(f.rel, fs.Length, Convert.ToHexStringLower(hash)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(f.rel);
            }
            progress?.Report((Interlocked.Add(ref done, f.size), total));
        });
        foreach (var e in results) m.Files[e.Path] = e;
        m.Unreadable.AddRange(unreadable.OrderBy(x => x, StringComparer.Ordinal));
        return m;
    }

    private static void Walk(string dir, string rel, ISet<string> skip, Manifest m, List<(string, string, long)> files, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool any = false;
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            any = true;
            string childRel = rel.Length == 0 ? entry.Name : rel + "/" + entry.Name;
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                m.Links.Add(childRel);
                continue;
            }
            if (entry is DirectoryInfo d)
            {
                if (skip.Contains(d.Name)) continue;
                Walk(d.FullName, childRel, skip, m, files, ct);
            }
            else if (entry is FileInfo f)
            {
                files.Add((f.FullName, childRel, f.Length));
            }
        }
        if (!any && rel.Length > 0) m.EmptyDirs.Add(rel);
    }
}
