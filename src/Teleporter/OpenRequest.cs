using System.IO;

namespace Teleporter;

/// <summary>
/// "Open this project" handed from a launch to the running instance (the stub folder's
/// "Open on server.cmd" runs <c>Teleporter.exe --open server/project</c>). The single-instance
/// signal carries no data, so the request waits in a file next to the settings.
/// </summary>
internal static class OpenRequest
{
    private static string File => Path.Combine(AppPaths.Root, "open-request.txt");

    public static void Write(string target)
    {
        try { System.IO.File.WriteAllText(File, target); } catch { }
    }

    /// <summary>The pending (server id, project) and clears it, or null.</summary>
    public static (string? Server, string Project)? Take()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return null;
            string text = System.IO.File.ReadAllText(File).Trim();
            System.IO.File.Delete(File);
            int slash = text.IndexOf('/');
            return slash > 0 ? (text[..slash], text[(slash + 1)..]) : (null, text);
        }
        catch
        {
            return null;
        }
    }
}
