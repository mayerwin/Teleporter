using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Teleporter.Connection;

namespace Teleporter.Server;

public sealed record MemoryUse(string Label, long Kb);
public sealed record ProjectInfo(string Name, bool IsGit, DateTimeOffset Modified);
public sealed record ServiceUnit(string Unit, string Active, string Sub);

public sealed class ServerStatus
{
    public string Home { get; set; } = "";
    public long MemTotalKb { get; set; }
    public long MemAvailableKb { get; set; }
    public long SwapTotalKb { get; set; }
    public long SwapFreeKb { get; set; }
    public List<MemoryUse> Groups { get; } = new();
    public List<ProjectInfo> Projects { get; } = new();
    public List<ServiceUnit> Services { get; } = new();
    public int AccountProcesses { get; set; }
    public List<string> LoadedModels { get; } = new();
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// Everything the window asks of the server. Each call is one ssh round trip running a
/// small bash script, so the window's 5 s refresh costs one short-lived ssh.exe and
/// nothing at all while the window is closed.
/// </summary>
internal sealed class ServerService(ServerProfile profile, SshClient ssh)
{
    // A fresh client per call, disposed straight away: a long-lived HttpClient keeps a
    // connection-pool scavenging timer that wakes the process even with the window closed.
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(5) };

    private const string StatusScript = """
        set -u
        echo "home $HOME"
        awk '/^MemTotal:/{t=$2} /^MemAvailable:/{a=$2} /^SwapTotal:/{st=$2} /^SwapFree:/{sf=$2} END{print "mem",t,a,st,sf}' /proc/meminfo
        # memory.current includes the page cache of files the group read (large data
        # files such as model weights), which the kernel drops on demand. Report what cannot
        # be dropped: current - (file - shmem). shmem stays in: integrated-GPU buffers are shmem.
        cg() {
          d="/sys/fs/cgroup/$1"; [ -r "$d/memory.current" ] || return
          awk -v cur="$(cat "$d/memory.current")" -v label="$2" '
            /^file /{f=$2} /^shmem /{s=$2}
            END{ v=cur-(f-s); if (v<0) v=0; printf "cg %d %s\n", v/1024, label }' "$d/memory.stat"
        }
        cg "user.slice/user-$(id -u).slice" "This account"
        for s in $WATCH; do cg "system.slice/$s" "$s"; done
        echo "procs $(ps -u "$(id -u)" --no-headers | wc -l)"
        systemctl --user list-units 'teleporter-*' --all --no-legend --plain 2>/dev/null | awk '{print "svc",$1,$3,$4}'
        root="${ROOT/#\~/$HOME}"
        if [ -d "$root" ]; then
          for d in "$root"/*/; do
            [ -d "$d" ] || continue
            n=$(basename "$d"); g=0; [ -d "$d/.git" ] && g=1
            echo "proj $g $(stat -c %Y "$d") $n"
          done
        fi
        """;

    public async Task<ServerStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var env = new Dictionary<string, string>
        {
            ["ROOT"] = profile.ProjectsRoot,
            ["WATCH"] = string.Join(' ', profile.WatchedServices),
        };
        ProcResult r = await ssh.RunScriptAsync(StatusScript, env, ct: ct);
        if (!r.Ok) throw new InvalidOperationException(FirstLine(r.StdErr) ?? $"ssh exit {r.ExitCode}");

        var s = new ServerStatus();
        foreach (string raw in r.StdOut.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string[] p = line.Split(' ', 2);
            if (p.Length < 2) continue;
            string rest = p[1];
            switch (p[0])
            {
                case "home": s.Home = rest; break;
                case "mem":
                    var m = rest.Split(' ').Select(x => long.TryParse(x, out long v) ? v : 0).ToArray();
                    if (m.Length >= 4) { s.MemTotalKb = m[0]; s.MemAvailableKb = m[1]; s.SwapTotalKb = m[2]; s.SwapFreeKb = m[3]; }
                    break;
                case "cg":
                    var c = rest.Split(' ', 2);
                    if (c.Length == 2 && long.TryParse(c[0], out long kb)) s.Groups.Add(new MemoryUse(c[1], kb));
                    break;
                case "procs": s.AccountProcesses = int.TryParse(rest, out int n) ? n : 0; break;
                case "svc":
                    var v = rest.Split(' ');
                    if (v.Length >= 3) s.Services.Add(new ServiceUnit(v[0], v[1], v[2]));
                    break;
                case "proj":
                    var q = rest.Split(' ', 3);
                    if (q.Length == 3 && long.TryParse(q[1], out long epoch))
                        s.Projects.Add(new ProjectInfo(q[2], q[0] == "1", DateTimeOffset.FromUnixTimeSeconds(epoch)));
                    break;
            }
        }
        s.Projects.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        await FillModelsAsync(s, ct);
        return s;
    }

    private async Task FillModelsAsync(ServerStatus s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.LlamaSwapUrl)) return;
        try
        {
            using var http = NewHttp();
            string json = await http.GetStringAsync(profile.LlamaSwapUrl.TrimEnd('/') + "/running", ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var m in doc.RootElement.GetProperty("running").EnumerateArray())
                s.LoadedModels.Add(m.GetProperty("model").GetString() ?? "?");
        }
        catch (Exception ex)
        {
            Log.Warn($"llama-swap /running failed: {ex.Message}");
        }
    }

    public async Task UnloadModelAsync(string model)
    {
        if (string.IsNullOrWhiteSpace(profile.LlamaSwapUrl)) return;
        using var http = NewHttp();
        using var resp = await http.PostAsync($"{profile.LlamaSwapUrl.TrimEnd('/')}/api/models/unload/{Uri.EscapeDataString(model)}", null);
        resp.EnsureSuccessStatusCode();
        Log.Info($"unloaded model {model}");
    }

    /// <summary>
    /// End every process of the server account: agent services, containers, VS Code and
    /// Antigravity servers, tmux, builds. The account's own systemd manager survives so
    /// services can be started again. The kill runs detached a second later, because it
    /// also ends the ssh session this command arrived on.
    /// </summary>
    public async Task StopEverythingAsync()
    {
        const string script = """
            systemctl --user stop 'teleporter-*' >/dev/null 2>&1
            podman stop -a -t 5 >/dev/null 2>&1
            me=$(id -u)
            setsid -f bash -c '
              sleep 1
              keep=" $$ $(pgrep -u '"$me"' -x systemd | tr "\n" " ") $(pgrep -u '"$me"' -x "(sd-pam)" | tr "\n" " ") "
              list=""
              for p in $(pgrep -u '"$me"'); do case "$keep" in *" $p "*) ;; *) list="$list $p" ;; esac; done
              [ -n "$list" ] && kill -KILL $list 2>/dev/null
            ' >/dev/null 2>&1 </dev/null
            echo stopping
            """;
        ProcResult r = await ssh.RunScriptAsync(script, timeout: TimeSpan.FromSeconds(30));
        Log.Info($"stop everything: exit {r.ExitCode}");
        if (!r.StdOut.Contains("stopping")) throw new InvalidOperationException(FirstLine(r.StdErr) ?? "stop failed");
    }

    public async Task CreateProjectAsync(string name, bool gitInit)
    {
        if (!IsValidName(name)) throw new ArgumentException("Use letters, digits, dot, dash or underscore.");
        const string script = """
            set -e
            root="${ROOT/#\~/$HOME}"
            d="$root/$NAME"
            [ -e "$d" ] && { echo "exists" >&2; exit 3; }
            mkdir -p "$d"
            if [ "$GIT" = 1 ]; then git -C "$d" init -q; fi
            echo created
            """;
        var env = new Dictionary<string, string> { ["ROOT"] = profile.ProjectsRoot, ["NAME"] = name, ["GIT"] = gitInit ? "1" : "0" };
        ProcResult r = await ssh.RunScriptAsync(script, env);
        if (r.ExitCode == 3) throw new InvalidOperationException($"'{name}' already exists on the server.");
        if (!r.Ok) throw new InvalidOperationException(FirstLine(r.StdErr) ?? $"exit {r.ExitCode}");
        Log.Info($"created project {name} (git={gitInit})");
    }

    public string ProjectPath(ServerStatus status, string name)
    {
        string root = profile.ProjectsRoot.StartsWith('~') ? status.Home + profile.ProjectsRoot[1..] : profile.ProjectsRoot;
        return root.TrimEnd('/') + "/" + name;
    }

    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= 100 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_') && name[0] != '.';

    private static string? FirstLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    public static string Gb(long kb) => (kb / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
}
