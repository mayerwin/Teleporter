using System;
using System.IO;

namespace Teleporter;

/// <summary>
/// Append-only text log in the data folder. Kept deliberately dumb: one lock, one
/// file, rotated at 2 MB, never throws. Secrets must never be passed in; the SSH
/// layer logs commands by name, not by content.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > MaxBytes)
                {
                    string old = AppPaths.LogFile + ".1";
                    File.Delete(old);
                    File.Move(AppPaths.LogFile, old);
                }
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
