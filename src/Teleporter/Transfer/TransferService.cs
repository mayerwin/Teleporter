using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Teleporter.Connection;
using Teleporter.Core;
using Teleporter.Sessions;

namespace Teleporter.Transfer;

public sealed record TransferUpdate(string Step, double? Fraction = null, string? Detail = null);

public sealed class SendOptions
{
    public required string LocalPath { get; init; }
    public required string Name { get; init; }
    public bool SkipDependencyFolders { get; init; } = true;
    public bool MoveSessions { get; init; }
    public bool SetAsideLocalCopy { get; init; } = true;
}

public sealed class BringBackOptions
{
    public required string Name { get; init; }
    public required string Destination { get; init; }
    public bool SkipDependencyFolders { get; init; } = true;
    public bool MoveSessions { get; init; }
    public bool SetAsideServerCopy { get; init; } = true;
}

/// <summary>What a pre-check found. Problems block the move; warnings need the user's OK.</summary>
public sealed class PreCheck
{
    public List<string> Problems { get; } = new();
    public List<string> Warnings { get; } = new();
    public long Bytes { get; set; }
    public int Files { get; set; }
}

/// <summary>
/// Moves a project folder between the local PC and the server with nothing left to chance:
/// every file is hashed at the source, streamed as one tar archive over ssh into a staging
/// folder, hashed again at the destination, and only an exact match (same file set, sizes and
/// SHA-256) is renamed into place. The source is set aside only after that.
/// </summary>
internal sealed class TransferService(ServerProfile profile, AppSettings settings, SshClient ssh, Registry registry)
{
    private const string HelperVersion = "4";
    private const string Helper = "$HOME/.teleporter/bin/helper.py";
    private static readonly string TarExe = Path.Combine(Environment.SystemDirectory, "tar.exe");
    private string? _home;

    // ─────────────────────────── shared ───────────────────────────

    private async Task<string> HomeAsync(CancellationToken ct)
    {
        if (_home is not null) return _home;
        var r = await ssh.RunAsync("printf %s \"$HOME\"", ct: ct);
        if (!r.Ok || r.StdOut.Length == 0) throw new InvalidOperationException("Could not read the server account's home: " + r.StdErr.Trim());
        return _home = r.StdOut.Trim();
    }

    public async Task<string> ServerProjectPathAsync(string name, CancellationToken ct = default)
    {
        string home = await HomeAsync(ct);
        string root = profile.ProjectsRoot.StartsWith('~') ? home + profile.ProjectsRoot[1..] : profile.ProjectsRoot;
        return root.TrimEnd('/') + "/" + name;
    }

    /// <summary>Upload helper.py once per version (it is embedded in the exe).</summary>
    private async Task EnsureHelperAsync(CancellationToken ct)
    {
        var v = await ssh.RunAsync($"python3 {Helper} version 2>/dev/null", ct: ct);
        if (v.Ok && v.StdOut.Trim() == HelperVersion) return;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Teleporter.helper.py")
                           ?? throw new InvalidOperationException("helper.py is missing from the build");
        string code = new StreamReader(stream).ReadToEnd();
        var r = await ssh.RunAsync($"command -v python3 >/dev/null || {{ echo 'python3 is required on the server' >&2; exit 3; }}; mkdir -p \"$HOME/.teleporter/bin\" && cat > {Helper}", code, ct: ct);
        if (!r.Ok) throw new InvalidOperationException("Could not install the server helper: " + r.StdErr.Trim());
    }

    private static string Q(string s) => SshClient.ShellQuote(s);

    private List<string> SkipNames(bool skipDeps) => skipDeps ? settings.SkipDirNames : new List<string>();

    private async Task<Manifest> RemoteManifestAsync(string dir, IEnumerable<string> skip, CancellationToken ct)
    {
        string args = string.Join(' ', skip.Select(Q));
        var r = await ssh.RunAsync($"python3 {Helper} manifest {Q(dir)} {args}", timeout: TimeSpan.FromHours(2), ct: ct);
        if (!r.Ok) throw new InvalidOperationException("Server manifest failed: " + r.StdErr.Trim());
        return Manifest.FromJsonLines(r.StdOut);
    }

    /// <summary>Pipe <paramref name="producer"/>'s stdout into <paramref name="consumer"/>'s stdin, counting bytes.</summary>
    private static async Task PipeAsync(Process producer, Process consumer, long expected, IProgress<TransferUpdate> progress, string step, CancellationToken ct)
    {
        Task<string> producerErr = producer.StandardError.ReadToEndAsync(ct);
        Task<string> consumerErr = consumer.StandardError.ReadToEndAsync(ct);
        Task<string> consumerOut = consumer.StandardOutput.ReadToEndAsync(ct);
        byte[] buffer = new byte[1 << 20];
        long copied = 0;
        var watch = Stopwatch.StartNew();
        try
        {
            Stream src = producer.StandardOutput.BaseStream, dst = consumer.StandardInput.BaseStream;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                copied += n;
                if (watch.ElapsedMilliseconds > 250)
                {
                    watch.Restart();
                    progress.Report(new TransferUpdate(step, expected > 0 ? Math.Min(1, copied / (double)expected) : null, $"{copied / 1048576.0:0.0} MB"));
                }
            }
            dst.Close();
            await Task.WhenAll(producer.WaitForExitAsync(ct), consumer.WaitForExitAsync(ct));
        }
        catch
        {
            try { producer.Kill(true); } catch { }
            try { consumer.Kill(true); } catch { }
            throw;
        }
        if (producer.ExitCode != 0) throw new InvalidOperationException($"Archive creation failed: {(await producerErr).Trim()}");
        if (consumer.ExitCode != 0) throw new InvalidOperationException($"Archive extraction failed: {(await consumerErr).Trim()} {(await consumerOut).Trim()}");
    }

    /// <summary>
    /// bsdtar arguments for a whole folder minus the skipped names and the links the manifest
    /// left out. Not a file list (<c>-T</c>): Windows' bsdtar reads list files in the ANSI code page,
    /// so any non-ASCII file name would fail; walking the folder itself uses Unicode APIs.
    /// The manifest applies the same rules, and verification proves the two agreed.
    /// </summary>
    private static List<string> LocalTarArgs(string root, IEnumerable<string> skip, IEnumerable<string> links)
    {
        var args = new List<string> { "-c", "-f", "-", "-C", root };
        foreach (string s in skip) { args.Add("--exclude"); args.Add(s); }
        foreach (string l in links) { args.Add("--exclude"); args.Add(l); }
        args.Add(".");
        return args;
    }

    private static Process StartLocal(string file, IEnumerable<string> args, bool redirectIn, bool redirectOut)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectIn,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new InvalidOperationException(file + " did not start");
    }

    private string TempDir()
    {
        string dir = Path.Combine(AppPaths.Root, "tmp", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string ParkingRootFor(string path)
    {
        if (!string.IsNullOrWhiteSpace(settings.ParkingFolder)) return settings.ParkingFolder;
        string profileDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        return string.Equals(root, Path.GetPathRoot(profileDir), StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(profileDir, "Teleporter-parked")
            : Path.Combine(root ?? profileDir, "Teleporter-parked");
    }

    // ─────────────────────────── send ───────────────────────────

    public PreCheck PreCheckSend(SendOptions o, Manifest m)
    {
        var pc = new PreCheck { Bytes = m.TotalBytes, Files = m.Files.Count };
        if (!Server.ServerService.IsValidName(o.Name)) pc.Problems.Add("Project name: use letters, digits, dot, dash or underscore.");
        foreach (string u in m.Unreadable.Take(20)) pc.Problems.Add($"Cannot read (locked or no permission): {u}");
        if (m.Links.Count > 0)
            pc.Warnings.Add($"{m.Links.Count} link(s)/junction(s) will not be copied: {string.Join(", ", m.Links.Take(5))}{(m.Links.Count > 5 ? ", …" : "")}");
        SessionSummary sessions = ClaudeSessions.ForCurrentUser().Summarize(o.LocalPath);
        if (sessions.LastWrite is { } last && DateTime.Now - last < TimeSpan.FromMinutes(2))
            pc.Warnings.Add("A Claude Code session in this folder wrote to its transcript less than 2 minutes ago. Close it before moving.");
        if (o.MoveSessions && sessions.Sessions == 0)
            pc.Warnings.Add("No Claude Code sessions were found for this folder; only the files will move.");
        if (StubInfo.Read(o.LocalPath) is not null) pc.Problems.Add("This folder is a Teleporter stub (the project already lives on a server).");
        return pc;
    }

    public Manifest ScanLocal(string path, bool skipDeps, IProgress<TransferUpdate> progress, CancellationToken ct)
    {
        var skip = new HashSet<string>(SkipNames(skipDeps), StringComparer.Ordinal);
        return Manifest.BuildLocal(path, skip, new Progress<(long done, long total)>(p =>
            progress.Report(new TransferUpdate("Hashing files on this PC", p.total > 0 ? p.done / (double)p.total : null))), ct);
    }

    /// <summary>
    /// Once the verified copy is in place the move has succeeded; what follows (permissions,
    /// sessions, setting the source aside) is best-effort. A failure there is reported as a
    /// warning instead of aborting, because both copies are intact and nothing was lost.
    /// </summary>
    private static async Task AfterCommitAsync(List<string> warnings, string what, Func<Task> step)
    {
        try { await step(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"{what}: {ex.Message}");
            Log.Warn($"{what} failed: {ex}");
        }
    }

    public async Task<List<string>> SendAsync(SendOptions o, Manifest local, IProgress<TransferUpdate> progress, CancellationToken ct)
    {
        var warnings = new List<string>();
        string serverPath = await ServerProjectPathAsync(o.Name, ct);
        string staging = serverPath + ".teleporter-incoming";
        progress.Report(new TransferUpdate("Preparing the server"));
        await EnsureHelperAsync(ct);
        var exists = await ssh.RunAsync($"test -e {Q(serverPath)} && echo EXISTS; rm -rf {Q(staging)}; mkdir -p {Q(staging)}", ct: ct);
        if (exists.StdOut.Contains("EXISTS")) throw new InvalidOperationException($"'{o.Name}' already exists on the server.");

        try
        {
            var tar = StartLocal(TarExe, LocalTarArgs(o.LocalPath, SkipNames(o.SkipDependencyFolders), local.Links), false, true);
            var remote = await ssh.StartStreamingAsync($"tar -x -f - -C {Q(staging)} --no-same-owner", ct);
            await PipeAsync(tar, remote, local.TotalBytes, progress, "Copying to the server", ct);

            progress.Report(new TransferUpdate("Verifying every file on the server"));
            Manifest arrived = await RemoteManifestAsync(staging, Array.Empty<string>(), ct);
            var diff = Manifest.Compare(local, arrived);
            if (diff.Count > 0)
                throw new InvalidOperationException("Verification failed, nothing was moved:\n" + string.Join("\n", diff.Take(20)));

            var commit = await ssh.RunAsync($"mv -T {Q(staging)} {Q(serverPath)}", ct: ct);
            if (!commit.Ok) throw new InvalidOperationException("Could not move the verified copy into place: " + commit.StdErr.Trim());
        }
        catch
        {
            await ssh.RunAsync($"rm -rf {Q(staging)}", ct: CancellationToken.None);
            throw;
        }

        ProjectRecord rec = registry.Project(profile.Id, o.Name);
        rec.LocalPath = Path.GetFullPath(o.LocalPath);
        rec.IsGit = Directory.Exists(Path.Combine(o.LocalPath, ".git"));
        rec.SentAt = DateTimeOffset.Now;
        registry.Save();

        progress.Report(new TransferUpdate("Restoring Linux permissions and git settings"));
        await AfterCommitAsync(warnings, "Permissions and git settings", () => FixUpAfterSendAsync(o, rec, serverPath, ct));

        if (o.MoveSessions)
        {
            progress.Report(new TransferUpdate("Moving Claude Code sessions and memory"));
            await AfterCommitAsync(warnings, "Sessions (left on this PC)", () => SendSessionsAsync(o.LocalPath, serverPath, o.Name, progress, ct));
        }

        if (o.SetAsideLocalCopy)
        {
            progress.Report(new TransferUpdate("Setting the local copy aside"));
            await AfterCommitAsync(warnings, "Setting the local copy aside (it is still in place; close programs using it and move it yourself)", () =>
            {
                SetAsideLocalFolder(o.LocalPath, o.Name, "project", local.TotalBytes);
                StubInfo.Write(o.LocalPath, profile, o.Name, serverPath);
                return Task.CompletedTask;
            });
        }
        progress.Report(new TransferUpdate("Done", 1));
        Log.Info($"sent {o.LocalPath} -> {profile.Id}:{serverPath} ({local.Files.Count} files, {local.TotalBytes} bytes, sessions={o.MoveSessions}, warnings={warnings.Count})");
        return warnings;
    }

    private async Task FixUpAfterSendAsync(SendOptions o, ProjectRecord rec, string serverPath, CancellationToken ct)
    {
        // Windows has no exec bit: restore what we recorded last time it came back, then git's view.
        if (rec.Modes.Count > 0)
            await ssh.RunAsync($"python3 {Helper} chmod {Q(serverPath)}", JsonSerializer.Serialize(rec.Modes, ModesJsonContext.Default.DictionaryStringString), ct: ct);
        if (!rec.IsGit) return;
        await ssh.RunAsync($"python3 {Helper} git-exec-bits {Q(serverPath)}", ct: ct);

        // A repo checked out with core.autocrlf=true has CRLF in the working tree; without a
        // matching setting git on Linux reports every text file as modified.
        string? effective = LocalGit(o.LocalPath, "config --get core.autocrlf");
        if (string.Equals(effective, "true", StringComparison.OrdinalIgnoreCase))
        {
            rec.AutoCrlfLocalBefore = LocalGit(o.LocalPath, "config --local --get core.autocrlf");
            registry.Save();
            await ssh.RunAsync($"cd {Q(serverPath)} && git config core.autocrlf input", ct: ct);
        }
        await ssh.RunAsync($"cd {Q(serverPath)} && git update-index -q --refresh >/dev/null 2>&1; true", ct: ct);
    }

    private static string? LocalGit(string repo, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", $"-C \"{repo}\" {args}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private void SetAsideLocalFolder(string path, string name, string kind, long bytes)
    {
        string parkRoot = ParkingRootFor(path);
        Directory.CreateDirectory(parkRoot);
        string target = Path.Combine(parkRoot, $"{name}-{kind}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.Move(path, target);
        registry.AddParked(new ParkedItem { Where = "local", ServerId = profile.Id, Project = name, Kind = kind, OriginalPath = path, ParkedPath = target, Bytes = bytes });
    }

    // ─────────────────────────── sessions ───────────────────────────

    private async Task SendSessionsAsync(string localPath, string serverPath, string name, IProgress<TransferUpdate> progress, CancellationToken ct)
    {
        var sessions = ClaudeSessions.ForCurrentUser();
        string tmp = TempDir();
        try
        {
            int count = sessions.Stage(localPath, serverPath, PathMap.ClaudeFolderName(serverPath), tmp);
            if (count == 0) return;
            Manifest staged = Manifest.BuildLocal(tmp, new HashSet<string>(), null, ct);
            string home = await HomeAsync(ct);
            string staging = $"{home}/.teleporter/incoming-sessions-{name}";
            await ssh.RunAsync($"rm -rf {Q(staging)}; mkdir -p {Q(staging)}", ct: ct);
            var tar = StartLocal(TarExe, LocalTarArgs(tmp, Array.Empty<string>(), Array.Empty<string>()), false, true);
            var remote = await ssh.StartStreamingAsync($"tar -x -f - -C {Q(staging)} --no-same-owner", ct);
            await PipeAsync(tar, remote, staged.TotalBytes, progress, "Copying sessions to the server", ct);
            Manifest arrived = await RemoteManifestAsync(staging, Array.Empty<string>(), ct);
            var diff = Manifest.Compare(staged, arrived);
            if (diff.Count > 0) throw new InvalidOperationException("Session verification failed; the local sessions were left in place:\n" + string.Join("\n", diff.Take(10)));
            var merge = await ssh.RunAsync($"python3 {Helper} merge {Q(staging + "/.claude")} \"$HOME/.claude\" && rm -rf {Q(staging)}", ct: ct);
            if (!merge.Ok) throw new InvalidOperationException("Could not merge the sessions on the server: " + merge.StdErr.Trim());

            string parkRoot = ParkingRootFor(localPath);
            string park = Path.Combine(parkRoot, $"{name}-sessions-{DateTime.Now:yyyyMMdd-HHmmss}");
            sessions.Park(localPath, park);
            registry.AddParked(new ParkedItem { Where = "local", ServerId = profile.Id, Project = name, Kind = "sessions", OriginalPath = sessions.Root, ParkedPath = park, Bytes = staged.TotalBytes });
            Log.Info($"moved {count} Claude sessions of {name} to the server");
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private async Task BringBackSessionsAsync(string serverPath, string localPath, string name, IProgress<TransferUpdate> progress, CancellationToken ct)
    {
        var sessions = ClaudeSessions.ForCurrentUser();
        string home = await HomeAsync(ct);
        string staging = $"{home}/.teleporter/outgoing-sessions-{name}";
        string target = ExistingOrNewClaudeFolder(sessions, localPath);
        var stage = await ssh.RunAsync($"rm -rf {Q(staging)}; python3 {Helper} sessions-stage {Q(serverPath)} {Q(localPath)} {Q(target)} {Q(staging)}", timeout: TimeSpan.FromMinutes(30), ct: ct);
        if (!stage.Ok) throw new InvalidOperationException("Could not collect the server sessions: " + stage.StdErr.Trim());
        if (stage.StdOut.Contains("\"sessions\": 0")) { await ssh.RunAsync($"rm -rf {Q(staging)}", ct: ct); return; }

        Manifest remote = await RemoteManifestAsync(staging, Array.Empty<string>(), ct);
        string tmp = TempDir();
        try
        {
            var src = await ssh.StartStreamingAsync($"cd {Q(staging)} && python3 {Helper} filelist . | tar --null -T - --format=pax -cf -", ct);
            var tar = StartLocal(TarExe, new[] { "-x", "-f", "-", "-C", tmp }, true, true);
            await PipeAsync(src, tar, remote.TotalBytes, progress, "Copying sessions to this PC", ct);
            Manifest arrived = Manifest.BuildLocal(tmp, new HashSet<string>(), null, ct);
            var diff = Manifest.Compare(remote, arrived);
            if (diff.Count > 0) throw new InvalidOperationException("Session verification failed; the server sessions were left in place:\n" + string.Join("\n", diff.Take(10)));
            sessions.MergeFromStaging(Path.Combine(tmp, ".claude"));
        }
        finally
        {
            TryDelete(tmp);
        }
        string park = $"{home}/.teleporter/parked/{name}-sessions-{DateTime.Now:yyyyMMdd-HHmmss}";
        var parked = await ssh.RunAsync($"python3 {Helper} sessions-park {Q(serverPath)} {Q(park)} && rm -rf {Q(staging)}", ct: ct);
        if (parked.Ok)
            registry.AddParked(new ParkedItem { Where = "server", ServerId = profile.Id, Project = name, Kind = "sessions", OriginalPath = home + "/.claude", ParkedPath = park, Bytes = remote.TotalBytes });
    }

    /// <summary>The local folder Claude already uses for this path, or the VS Code-style name for a new one.</summary>
    private static string ExistingOrNewClaudeFolder(ClaudeSessions sessions, string localPath)
    {
        string? existing = sessions.FindProjectFolders(localPath).FirstOrDefault();
        return existing is not null ? Path.GetFileName(existing) : PathMap.ClaudeFolderName(localPath);
    }

    // ─────────────────────────── bring back ───────────────────────────

    public async Task<(PreCheck Check, Manifest Remote)> PreCheckBringBackAsync(BringBackOptions o, IProgress<TransferUpdate> progress, CancellationToken ct)
    {
        string serverPath = await ServerProjectPathAsync(o.Name, ct);
        progress.Report(new TransferUpdate("Hashing files on the server"));
        await EnsureHelperAsync(ct);
        Manifest remote = await RemoteManifestAsync(serverPath, SkipNames(o.SkipDependencyFolders), ct);
        var pc = new PreCheck { Bytes = remote.TotalBytes, Files = remote.Files.Count };
        var (problems, warnings) = NameChecks.Check(remote.Files.Keys.Concat(remote.EmptyDirs), Path.GetFullPath(o.Destination));
        pc.Problems.AddRange(problems.Take(30));
        pc.Warnings.AddRange(warnings.Take(10));
        foreach (string u in remote.Unreadable.Take(20)) pc.Problems.Add($"Cannot read on the server: {u}");
        if (remote.Links.Count > 0)
            pc.Warnings.Add($"{remote.Links.Count} symlink(s) on the server will not be copied: {string.Join(", ", remote.Links.Take(5))}{(remote.Links.Count > 5 ? ", …" : "")}");
        if (Directory.Exists(o.Destination) && StubInfo.Read(o.Destination) is null && Directory.EnumerateFileSystemEntries(o.Destination).Any())
            pc.Problems.Add($"{o.Destination} already exists and is not a Teleporter stub. Choose another destination.");
        return (pc, remote);
    }

    public async Task<List<string>> BringBackAsync(BringBackOptions o, Manifest remote, IProgress<TransferUpdate> progress, CancellationToken ct)
    {
        string serverPath = await ServerProjectPathAsync(o.Name, ct);
        string dest = Path.GetFullPath(o.Destination);
        string parent = Path.GetDirectoryName(dest) ?? throw new InvalidOperationException("Invalid destination");
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, $".{Path.GetFileName(dest)}.teleporter-incoming");
        TryDelete(staging);
        Directory.CreateDirectory(staging);

        progress.Report(new TransferUpdate("Stopping the project's agent service"));
        await ssh.RunAsync($"systemctl --user disable --now {Q("teleporter-claude@" + o.Name + ".service")} >/dev/null 2>&1; true", ct: ct);

        try
        {
            string skip = string.Join(' ', SkipNames(o.SkipDependencyFolders).Select(Q));
            var src = await ssh.StartStreamingAsync($"cd {Q(serverPath)} && python3 {Helper} filelist . {skip} | tar --null -T - --format=pax -cf -", ct);
            var tar = StartLocal(TarExe, new[] { "-x", "-f", "-", "-C", staging }, true, true);
            await PipeAsync(src, tar, remote.TotalBytes, progress, "Copying to this PC", ct);

            progress.Report(new TransferUpdate("Verifying every file on this PC"));
            Manifest arrived = Manifest.BuildLocal(staging, new HashSet<string>(), new Progress<(long done, long total)>(p =>
                progress.Report(new TransferUpdate("Verifying every file on this PC", p.total > 0 ? p.done / (double)p.total : null))), ct);
            var diff = Manifest.Compare(remote, arrived);
            if (diff.Count > 0) throw new InvalidOperationException("Verification failed, nothing was moved:\n" + string.Join("\n", diff.Take(20)));

            if (Directory.Exists(dest)) StubInfo.Remove(dest);
            Directory.Move(staging, dest);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }

        var warnings = new List<string>();
        await AfterCommitAsync(warnings, "Recording permissions and git settings", () =>
        {
            ProjectRecord rec = registry.Project(profile.Id, o.Name);
            rec.LocalPath = dest;
            rec.BroughtBackAt = DateTimeOffset.Now;
            // Remember exec bits and other non-default modes for the next trip to Linux.
            rec.Modes = remote.Files.Values.Where(f => f.Mode is not null and not "644" and not "664" and not "600" && !f.Path.StartsWith(".git/", StringComparison.Ordinal))
                .ToDictionary(f => f.Path, f => f.Mode!);
            if (rec.IsGit || Directory.Exists(Path.Combine(dest, ".git")))
            {
                rec.IsGit = true;
                if (LocalGit(dest, "config --local --get core.autocrlf") == "input")
                {
                    if (rec.AutoCrlfLocalBefore is null) LocalGit(dest, "config --local --unset core.autocrlf");
                    else LocalGit(dest, $"config --local core.autocrlf {rec.AutoCrlfLocalBefore}");
                }
            }
            registry.Save();
            return Task.CompletedTask;
        });

        if (o.MoveSessions)
        {
            progress.Report(new TransferUpdate("Moving Claude Code sessions and memory"));
            await AfterCommitAsync(warnings, "Sessions (left on the server)", () => BringBackSessionsAsync(serverPath, dest, o.Name, progress, ct));
        }

        if (o.SetAsideServerCopy)
        {
            progress.Report(new TransferUpdate("Setting the server copy aside"));
            await AfterCommitAsync(warnings, "Setting the server copy aside (it is still in place)", async () =>
            {
                string home = await HomeAsync(ct);
                string park = $"{home}/.teleporter/parked/{o.Name}-project-{DateTime.Now:yyyyMMdd-HHmmss}";
                var mv = await ssh.RunAsync($"mkdir -p \"$HOME/.teleporter/parked\" && mv -T {Q(serverPath)} {Q(park)}", ct: ct);
                if (!mv.Ok) throw new InvalidOperationException(mv.StdErr.Trim());
                registry.AddParked(new ParkedItem { Where = "server", ServerId = profile.Id, Project = o.Name, Kind = "project", OriginalPath = serverPath, ParkedPath = park, Bytes = remote.TotalBytes });
            });
        }
        progress.Report(new TransferUpdate("Done", 1));
        Log.Info($"brought back {profile.Id}:{serverPath} -> {dest} ({remote.Files.Count} files, sessions={o.MoveSessions}, warnings={warnings.Count})");
        return warnings;
    }

    // ─────────────────────────── set-aside copies ───────────────────────────

    public async Task DeleteParkedAsync(ParkedItem item)
    {
        if (item.Where != "server")
        {
            if (Directory.Exists(item.ParkedPath))
            {
                foreach (var f in new DirectoryInfo(item.ParkedPath).EnumerateFiles("*", SearchOption.AllDirectories))
                    if (f.IsReadOnly) f.IsReadOnly = false; // git objects are read-only
                Directory.Delete(item.ParkedPath, recursive: true);
            }
        }
        else
        {
            string home = await HomeAsync(CancellationToken.None);
            if (!item.ParkedPath.StartsWith(home + "/.teleporter/parked/", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to delete a server path outside ~/.teleporter/parked.");
            var r = await ssh.RunAsync($"rm -rf {Q(item.ParkedPath)}");
            if (!r.Ok) throw new InvalidOperationException(r.StdErr.Trim());
        }
        registry.RemoveParked(item.Id);
        Log.Info($"deleted set-aside {item.Where} copy {item.ParkedPath}");
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                if (f.IsReadOnly) f.IsReadOnly = false;
            Directory.Delete(dir, true);
        }
        catch { }
    }

    private static void TryDeleteFile(string file)
    {
        try { File.Delete(file); } catch { }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class ModesJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
