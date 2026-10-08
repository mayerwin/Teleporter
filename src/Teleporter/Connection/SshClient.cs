using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Teleporter.Connection;

/// <summary>
/// Runs things on the server through the configured ssh.exe. Scripts go over stdin to
/// <c>bash -s</c>, which sidesteps every layer of quoting between Windows and the
/// remote shell. Host keys are pinned in Teleporter's own known_hosts (accept on first
/// contact), so Teleporter never edits the user's ~/.ssh.
/// </summary>
internal sealed class SshClient(IConnectionProvider provider)
{
    public static string KnownHostsFile => Path.Combine(AppPaths.Root, "known_hosts");

    public async Task<ProcResult> RunScriptAsync(string script, IDictionary<string, string>? remoteEnv = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        SshTarget t = await provider.PrepareAsync(ct);
        string envPrefix = "";
        if (remoteEnv is not null)
            foreach (var kv in remoteEnv)
                envPrefix += $"{kv.Key}={ShellQuote(kv.Value)} ";
        var args = BaseArgs(t);
        args.Add($"{envPrefix}bash -s");
        return await ProcessRunner.RunAsync(t.SshPath, args, stdin: script, timeout: timeout ?? TimeSpan.FromSeconds(30), ct: ct);
    }

    /// <summary>Run one remote command line (already quoted for the remote shell), optional stdin.</summary>
    public async Task<ProcResult> RunAsync(string remoteCommand, string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        SshTarget t = await provider.PrepareAsync(ct);
        var args = BaseArgs(t);
        args.Add(remoteCommand);
        return await ProcessRunner.RunAsync(t.SshPath, args, stdin: stdin, timeout: timeout ?? TimeSpan.FromSeconds(60), ct: ct);
    }

    /// <summary>Start ssh with raw stdin/stdout streams, for piping tar archives.</summary>
    public async Task<System.Diagnostics.Process> StartStreamingAsync(string remoteCommand, CancellationToken ct)
    {
        SshTarget t = await provider.PrepareAsync(ct);
        var psi = new System.Diagnostics.ProcessStartInfo(t.SshPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in BaseArgs(t)) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(remoteCommand);
        return System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("ssh did not start");
    }

    /// <summary>Arguments for an interactive session in a console window (<c>-t</c>), running <paramref name="remoteCommand"/>.</summary>
    public async Task<(string SshPath, List<string> Args)> InteractiveArgsAsync(string remoteCommand)
    {
        SshTarget t = await provider.PrepareAsync(default);
        var args = BaseArgs(t, batch: false);
        args.Insert(0, "-t");
        args.Add(remoteCommand);
        return (t.SshPath, args);
    }

    public static List<string> BaseArgs(SshTarget t, bool batch = true)
    {
        var args = new List<string>
        {
            "-o", batch ? "BatchMode=yes" : "BatchMode=no",
            "-o", "ConnectTimeout=10",
            "-o", "ServerAliveInterval=15",
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", $"UserKnownHostsFile={ToSshPath(KnownHostsFile)}",
            "-p", t.Port.ToString(),
        };
        if (t.IdentityAgent is not null)
        {
            args.Add("-o");
            args.Add($"IdentityAgent={ToSshPath(t.IdentityAgent)}");
        }
        args.Add($"{t.User}@{t.Host}");
        return args;
    }

    /// <summary>Git's ssh is an MSYS program: forward slashes work everywhere, backslashes do not.</summary>
    public static string ToSshPath(string windowsPath) => windowsPath.Replace('\\', '/');

    /// <summary>Single-quote for POSIX sh.</summary>
    public static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
