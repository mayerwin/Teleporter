using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Teleporter.Addons;
using Teleporter.Connection;

namespace Teleporter.Agents;

public sealed class AntigravityStatus
{
    public bool Installed { get; set; }
    public string? Version { get; set; }
    public bool SignedIn { get; set; }
    public string Daemon { get; set; } = "unknown";
}

/// <summary>
/// Long-running agents on the server, owned by systemd (user services of the server account) so
/// they survive the local PC being off:
///  - Claude Code: one <c>teleporter-claude@&lt;project&gt;</c> service per project running
///    <c>claude remote-control</c> (each new session gets its own git worktree in a git repo),
///    steerable from Claude Desktop, claude.ai/code or the phone.
///  - Antigravity: the official <c>agy remote-control</c> daemon, steerable from the Antigravity
///    dashboard. Add-ons can contribute environment (e.g. a per-process proxy).
/// When the profile names a Toolbx container, agents run inside it.
/// </summary>
internal sealed class AgentService(ServerProfile profile, SshClient ssh, AddonSet addons)
{
    private static string Q(string s) => SshClient.ShellQuote(s);

    private string Runner => string.IsNullOrWhiteSpace(profile.Container) ? "" : $"toolbox run -c {profile.Container} ";

    private const string ClaudeUnit = """
        [Unit]
        Description=Teleporter: Claude Code remote control for project %i
        StartLimitIntervalSec=600
        StartLimitBurst=5

        [Service]
        EnvironmentFile=%h/.config/teleporter/claude-%i.env
        ExecStart=/bin/bash -lc 'cd "$PROJECT_DIR" && exec $RUNNER "$HOME/.local/bin/claude" remote-control --name "$AGENT_NAME" --spawn "$SPAWN"'
        # remote-control exits after ~10 minutes without network; come back on our own.
        Restart=on-failure
        RestartSec=30

        [Install]
        WantedBy=default.target
        """;

    public async Task StartClaudeAsync(string project, string projectDir, bool isGit)
    {
        string env = $"PROJECT_DIR={projectDir}\nAGENT_NAME={project}\nSPAWN={(isGit ? "worktree" : "same-dir")}\nRUNNER={Runner.Trim()}\n";
        string script = $"""
            set -e
            mkdir -p "$HOME/.config/systemd/user" "$HOME/.config/teleporter"
            cat > "$HOME/.config/systemd/user/teleporter-claude@.service" <<'UNIT'
            {ClaudeUnit}
            UNIT
            cat > "$HOME/.config/teleporter/claude-{project}.env" <<'ENV'
            {env}ENV
            systemctl --user daemon-reload
            systemctl --user enable --now 'teleporter-claude@{project}.service'
            """;
        var r = await ssh.RunScriptAsync(script);
        if (!r.Ok) throw new InvalidOperationException(r.StdErr.Trim());
        Log.Info($"started Claude agent service for {project}");
    }

    public async Task StopClaudeAsync(string project)
    {
        var r = await ssh.RunAsync($"systemctl --user disable --now {Q($"teleporter-claude@{project}.service")}");
        if (!r.Ok) throw new InvalidOperationException(r.StdErr.Trim());
        Log.Info($"stopped Claude agent service for {project}");
    }

    public async Task<bool> ClaudeSignedInAsync()
    {
        // `claude auth status` prints JSON: { "loggedIn": true|false, "authMethod": ... } (exit 1 when signed out).
        var r = await ssh.RunAsync("\"$HOME/.local/bin/claude\" auth status 2>/dev/null; true", timeout: TimeSpan.FromSeconds(30));
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.StdOut);
            return doc.RootElement.TryGetProperty("loggedIn", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Environment for every agy invocation. File-based credentials: a headless server has no
    /// desktop keyring, and the systemd daemon cannot reach the CLI's keyring login otherwise
    /// (antigravity-cli#854).
    /// </summary>
    public Dictionary<string, string> AntigravityEnv()
    {
        var env = new Dictionary<string, string> { ["GEMINI_FORCE_FILE_STORAGE"] = "true" };
        foreach (var kv in addons.EnvFor("antigravity")) env[kv.Key] = kv.Value;
        return env;
    }

    public string EnvPrefix(Dictionary<string, string> env) =>
        env.Count == 0 ? "" : "env " + string.Join(' ', env.Select(kv => $"{kv.Key}={Q(kv.Value)}")) + " ";

    public async Task<AntigravityStatus> AntigravityStatusAsync()
    {
        string prefix = EnvPrefix(AntigravityEnv());
        string script = $"""
            agy="$HOME/.local/bin/agy"
            if [ -x "$agy" ]; then echo "installed 1"; echo "version $("$agy" --version 2>/dev/null | head -1)"; else echo "installed 0"; fi
            [ -s "$HOME/.gemini/antigravity-cli/antigravity-oauth-token" ] && echo "signedin 1" || echo "signedin 0"
            [ -x "$agy" ] && echo "daemon $({prefix}"$agy" remote-control status 2>&1 | tr '\n' ' ' | cut -c1-200)"
            """;
        var r = await ssh.RunScriptAsync(script, timeout: TimeSpan.FromSeconds(40));
        var s = new AntigravityStatus();
        foreach (string line in r.StdOut.Split('\n'))
        {
            string[] p = line.Trim().Split(' ', 2);
            if (p.Length < 2) continue;
            switch (p[0])
            {
                case "installed": s.Installed = p[1] == "1"; break;
                case "version": s.Version = p[1].Trim(); break;
                case "signedin": s.SignedIn = p[1] == "1"; break;
                case "daemon": s.Daemon = p[1].Trim(); break;
            }
        }
        return s;
    }

    public async Task InstallAntigravityAsync()
    {
        await addons.PrepareAsync(ssh, "antigravity");
        string prefix = EnvPrefix(AntigravityEnv());
        var r = await ssh.RunScriptAsync(
            $"if [ -x \"$HOME/.local/bin/agy\" ]; then {prefix}\"$HOME/.local/bin/agy\" update; else curl -fsSL https://antigravity.google/cli/install.sh | {prefix}bash; fi",
            timeout: TimeSpan.FromMinutes(10));
        if (!r.Ok) throw new InvalidOperationException(r.StdErr.Trim());
        await WriteAntigravityEnvFilesAsync();
    }

    public string SignInCommand() => $"{EnvPrefix(AntigravityEnv())}\"$HOME/.local/bin/agy\"; echo; read -r -p 'Press Enter to close this window' _";

    public async Task StartAntigravityDaemonAsync()
    {
        await addons.PrepareAsync(ssh, "antigravity");
        await WriteAntigravityEnvFilesAsync();
        string prefix = EnvPrefix(AntigravityEnv());
        var r = await ssh.RunScriptAsync($"""
            {prefix}"$HOME/.local/bin/agy" remote-control start --name {Q(profile.DisplayName)}
            systemctl --user daemon-reload
            systemctl --user restart antigravity-cli-daemon.service 2>/dev/null || true
            """, timeout: TimeSpan.FromMinutes(2));
        if (!r.Ok) throw new InvalidOperationException((r.StdErr + r.StdOut).Trim());
        Log.Info("started Antigravity remote-control daemon");
    }

    public async Task StopAntigravityDaemonAsync()
    {
        string prefix = EnvPrefix(AntigravityEnv());
        var r = await ssh.RunAsync($"{prefix}\"$HOME/.local/bin/agy\" remote-control stop");
        if (!r.Ok) throw new InvalidOperationException((r.StdErr + r.StdOut).Trim());
        Log.Info("stopped Antigravity remote-control daemon");
    }

    /// <summary>
    /// Give the same environment to the Antigravity process that is not started through Teleporter:
    /// the systemd daemon (a drop-in). The Antigravity IDE needs nothing here: over Remote-SSH its
    /// agent runs on the local PC.
    /// </summary>
    private async Task WriteAntigravityEnvFilesAsync()
    {
        var env = AntigravityEnv();
        string dropin = "[Service]\n" + string.Join("\n", env.Select(kv => $"Environment=\"{kv.Key}={kv.Value}\"")) + "\n";
        var r = await ssh.RunScriptAsync($"""
            set -e
            mkdir -p "$HOME/.config/systemd/user/antigravity-cli-daemon.service.d"
            cat > "$HOME/.config/systemd/user/antigravity-cli-daemon.service.d/teleporter.conf" <<'DROPIN'
            {dropin}DROPIN
            systemctl --user daemon-reload
            """);
        if (!r.Ok) throw new InvalidOperationException(r.StdErr.Trim());
    }
}
