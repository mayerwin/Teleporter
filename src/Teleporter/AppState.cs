using System.IO;
using System.Threading.Tasks;
using Teleporter.Addons;
using Teleporter.Agents;
using Teleporter.Connection;
using Teleporter.Core;
using Teleporter.Server;
using Teleporter.Transfer;

namespace Teleporter;

/// <summary>
/// The app's long-lived state: settings, registry and add-ons, plus the services for the active
/// server. Rebuilt when settings change. The connection provider is only asked for credentials on
/// the first ssh need, and released on exit.
/// </summary>
internal sealed class AppState
{
    public static AppState Current { get; } = new();

    public AppSettings Settings { get; private set; } = SettingsStore.Load();
    public Registry Registry { get; } = new(Path.Combine(AppPaths.Root, "registry.json"));
    public AddonSet Addons { get; private set; } = AddonSet.Load(AppPaths.AddonsDir);
    public IConnectionProvider? Provider { get; private set; }
    public SshClient? Ssh { get; private set; }
    public ServerService? Server { get; private set; }
    public TransferService? Transfer { get; private set; }
    public AgentService? Agents { get; private set; }

    private AppState() => Rebuild();

    public void Save(AppSettings settings)
    {
        SettingsStore.Save(settings);
        _ = Provider?.ReleaseAsync();
        Settings = settings;
        Addons = AddonSet.Load(AppPaths.AddonsDir);
        Rebuild();
    }

    private void Rebuild()
    {
        ServerProfile? p = Settings.ActiveServer;
        if (p is null || (p.Provider != "external" && string.IsNullOrWhiteSpace(p.Host)))
        {
            Provider = null; Ssh = null; Server = null; Transfer = null; Agents = null;
            return;
        }
        Provider = ConnectionFactory.For(p, Settings);
        Ssh = new SshClient(Provider);
        Server = new ServerService(p, Ssh);
        Transfer = new TransferService(p, Settings, Ssh, Registry);
        Agents = new AgentService(p, Ssh, Addons);
    }

    /// <summary>Make sure the VS Code ssh config reflects the current credentials (agent socket may change).</summary>
    public async Task RefreshSshConfigAsync()
    {
        ServerProfile? p = Settings.ActiveServer;
        if (p is null || Provider is null) return;
        SshTarget t = await Provider.PrepareAsync(default);
        VsCode.WriteSshConfig(p, t);
    }

    public Task ShutdownAsync() => Provider?.ReleaseAsync() ?? Task.CompletedTask;
}
