using System;
using System.Collections.Generic;
using System.Linq;

namespace Teleporter.Transfer;

/// <summary>
/// Which Linux paths cannot exist on Windows. Checked BEFORE bringing a project back, so a
/// transfer never half-extracts and then fails on one file.
/// </summary>
public static class NameChecks
{
    private static readonly char[] Invalid = { '<', '>', ':', '"', '|', '?', '*', '\\' };
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Why one path segment is not a valid Windows name, or null if it is.</summary>
    public static string? SegmentProblem(string segment)
    {
        if (segment.Length == 0) return "empty name";
        if (segment.IndexOfAny(Invalid) >= 0) return "contains one of < > : \" | ? * \\";
        if (segment.Any(c => c < 32)) return "contains a control character";
        if (segment.EndsWith('.') || segment.EndsWith(' ')) return "ends with a dot or a space";
        string stem = segment.Split('.')[0].TrimEnd(' ');
        if (Reserved.Contains(stem)) return $"'{stem}' is a reserved device name on Windows";
        return null;
    }

    /// <summary>
    /// Every problem for a set of relative paths: invalid segments, and paths that differ only by
    /// letter case (Windows would merge them into one file).
    /// </summary>
    public static List<string> WindowsProblems(IEnumerable<string> relativePaths) =>
        Check(relativePaths, null).Problems;

    /// <summary>Blocking problems, plus warnings (paths over 260 characters still extract, but many tools cannot open them).</summary>
    public static (List<string> Problems, List<string> Warnings) Check(IEnumerable<string> relativePaths, string? destinationRoot)
    {
        var problems = new List<string>();
        var warnings = new List<string>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in relativePaths)
        {
            foreach (string seg in path.Split('/'))
            {
                string? why = SegmentProblem(seg);
                if (why is not null) { problems.Add($"{path}: {why}"); break; }
            }
            if (seen.TryGetValue(path, out string? other) && other != path)
                problems.Add($"{path}: differs from {other} only by letter case");
            else
                seen[path] = path;
            if (destinationRoot is not null && destinationRoot.Length + 1 + path.Length > 259)
                warnings.Add($"{path}: the full path exceeds 260 characters (many Windows tools cannot open it)");
        }
        return (problems, warnings);
    }
}
