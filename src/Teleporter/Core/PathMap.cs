using System;
using System.Text.RegularExpressions;

namespace Teleporter.Core;

/// <summary>
/// Path helpers shared by the session mover. Must stay in step with helper.py (canon, norm,
/// map_cwd), which does the same on the server.
/// </summary>
public static partial class PathMap
{
    [GeneratedRegex("^/([a-zA-Z])/(.*)$")]
    private static partial Regex MsysDrive();

    [GeneratedRegex("^[a-zA-Z]:")]
    private static partial Regex WindowsDrive();

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlnum();

    /// <summary>Forward slashes, no trailing slash, MSYS <c>/c/Users/..</c> turned into <c>c:/Users/..</c>; case kept.</summary>
    public static string Canon(string path)
    {
        string p = path.Replace('\\', '/').TrimEnd('/');
        Match m = MsysDrive().Match(p);
        return m.Success ? m.Groups[1].Value + ":/" + m.Groups[2].Value : p;
    }

    public static bool IsWindowsPath(string p) => WindowsDrive().IsMatch(p);

    /// <summary>Comparison key: <see cref="Canon"/>, lower-cased for Windows paths.</summary>
    public static string Norm(string path)
    {
        string p = Canon(path);
        return IsWindowsPath(p) ? p.ToLowerInvariant() : p;
    }

    /// <summary>
    /// Map a session's recorded working directory from the old project root to the new one,
    /// keeping any sub-folder. Returns null when the value is not inside the old root.
    /// </summary>
    public static string? MapCwd(string value, string oldRoot, string newRoot)
    {
        string v = Canon(value), o = Canon(oldRoot);
        string kv = Norm(value), ko = Norm(oldRoot);
        if (kv != ko && !kv.StartsWith(ko + "/", StringComparison.Ordinal)) return null;
        string tail = v[o.Length..].TrimStart('/');
        if (IsWindowsPath(newRoot))
            return newRoot.TrimEnd('\\', '/') + (tail.Length > 0 ? "\\" + tail.Replace('/', '\\') : "");
        return newRoot.TrimEnd('/') + (tail.Length > 0 ? "/" + tail : "");
    }

    /// <summary>
    /// Claude Code's folder name for a working directory: every character that is not an ASCII
    /// letter or digit becomes '-'. Windows drive letters are lower-cased, matching what the VS Code
    /// extension writes (the CLI writes upper case; both are found when reading).
    /// Paths over 200 characters get a hash suffix in Claude Code 2.1.224+, which this does not
    /// reproduce, so they are refused rather than written somewhere Claude will not look.
    /// </summary>
    public static string ClaudeFolderName(string path)
    {
        string p = path;
        if (IsWindowsPath(p)) p = char.ToLowerInvariant(p[0]) + p[1..];
        string name = NonAlnum().Replace(p, "-");
        if (name.Length > 200)
            throw new InvalidOperationException($"The path is too long for Claude Code's session folder naming ({name.Length} > 200 characters).");
        return name;
    }
}
