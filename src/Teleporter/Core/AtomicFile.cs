using System;
using System.IO;
using System.Threading;

namespace Teleporter.Core;

internal static class AtomicFile
{
    /// <summary>
    /// Rename <paramref name="tmp"/> over <paramref name="target"/>, retrying briefly: a sync client
    /// (Dropbox, OneDrive) or an antivirus scanner often holds a just-written file open for a moment,
    /// which turns a plain rename into "access denied". Measured: Dropbox did exactly that to the
    /// registry during a transfer.
    /// </summary>
    public static void Replace(string tmp, string target)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 20)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }
}
