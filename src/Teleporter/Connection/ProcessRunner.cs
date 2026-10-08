using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Teleporter.Connection;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

internal static class ProcessRunner
{
    /// <summary>
    /// Run a process to completion, optionally feeding stdin, capturing both streams.
    /// Never shows a console window. stdin is the channel for anything sensitive:
    /// command lines are visible to every process on the machine.
    /// </summary>
    public static async Task<ProcResult> RunAsync(string file, IEnumerable<string> args, string? stdin = null,
        IDictionary<string, string>? env = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        Task<string> stdout = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = proc.StandardError.ReadToEndAsync(ct);
        if (stdin is not null)
        {
            // Unix line endings: the far end is usually bash.
            await proc.StandardInput.WriteAsync(stdin.Replace("\r\n", "\n").AsMemory(), ct);
        }
        proc.StandardInput.Close();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is not null) cts.CancelAfter(timeout.Value);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{Path.GetFileName(file)} did not finish in time");
        }
        return new ProcResult(proc.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// How to launch a user-supplied command by extension. A bare <c>bash</c> on many
    /// machines resolves to WSL rather than Git Bash, so .sh files go to Git's bash.exe
    /// explicitly.
    /// </summary>
    public static (string File, List<string> Args) ResolveScript(string command)
    {
        string ext = Path.GetExtension(command).ToLowerInvariant();
        return ext switch
        {
            ".sh" => (GitBash(), new List<string> { command }),
            ".ps1" => ("powershell.exe", new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", command }),
            ".cmd" or ".bat" => ("cmd.exe", new List<string> { "/d", "/c", command }),
            _ => (command, new List<string>()),
        };
    }

    public static string GitBash()
    {
        foreach (string p in new[] { @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files (x86)\Git\bin\bash.exe" })
            if (File.Exists(p)) return p;
        return "bash.exe";
    }
}
