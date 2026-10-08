using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Teleporter.Core;
using Teleporter.Server;

namespace Teleporter.Views;

public partial class MainWindow : Window
{
    // Runs only while this window exists; the window is destroyed on close.
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _refreshing;
    private ServerStatus? _last;

    private static readonly IBrush[] SegmentBrushes =
    {
        new SolidColorBrush(Color.Parse("#6366F1")),
        new SolidColorBrush(Color.Parse("#0EA5E9")),
        new SolidColorBrush(Color.Parse("#F59E0B")),
        new SolidColorBrush(Color.Parse("#10B981")),
        new SolidColorBrush(Color.Parse("#EC4899")),
    };
    private static readonly IBrush OtherBrush = new SolidColorBrush(Color.Parse("#9CA3AF"));

    public MainWindow()
    {
        InitializeComponent();
        LoadSettingsIntoForm();
        _refresh.Tick += async (_, _) => await RefreshAsync();
        Opened += async (_, _) =>
        {
            _refresh.Start();
            RenderParked();
            await RefreshAsync();
            await RefreshAgentsAsync();
        };
        Closed += (_, _) => _refresh.Stop();
        // Sign-ins and installs often finish in another window (terminal, browser): re-read the
        // agent accounts when the user comes back, and when the Server tab is shown.
        Activated += async (_, _) => await RefreshAgentsThrottledAsync();
        Tabs.SelectionChanged += async (_, _) => { if (Tabs.SelectedIndex == 1) await RefreshAgentsThrottledAsync(); };
    }

    private DateTime _agentsReadAt = DateTime.MinValue;

    private async Task RefreshAgentsThrottledAsync()
    {
        if (DateTime.Now - _agentsReadAt < TimeSpan.FromSeconds(10)) return;
        await RefreshAgentsAsync();
    }

    private AppState State => AppState.Current;
    private string? SelectedProject => (ProjectList.SelectedItem as ListBoxItem)?.Tag as string;

    // ─────────────────────────── refresh ───────────────────────────

    private async Task RefreshAsync()
    {
        ServerProfile? p = State.Settings.ActiveServer;
        if (State.Server is null || p is null)
        {
            ServerLine.Text = "No server configured yet. Open Settings.";
            return;
        }
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            ServerStatus s = await State.Server.GetStatusAsync();
            _last = s;
            ServerLine.Text = $"{p.DisplayName}: {p.User}@{p.Host}, projects in {p.ProjectsRoot}";
            RenderProjects(s);
            RenderMemory(s);
            RenderModels(s);
            RenderServices(s);
            UpdateAgentButton();
        }
        catch (Exception ex)
        {
            StatusLine.Text = "Server unreachable: " + ex.Message;
            Log.Warn($"status refresh failed: {ex.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshAgentsAsync()
    {
        if (State.Agents is null) return;
        _agentsReadAt = DateTime.Now;
        try
        {
            bool claude = await State.Agents.ClaudeSignedInAsync();
            ClaudeState.Text = claude ? "Signed in on the server." : "Not signed in on the server.";
            var agy = await State.Agents.AntigravityStatusAsync();
            AgyState.Text = !agy.Installed ? "Not installed." :
                $"{agy.Version} · {(agy.SignedIn ? "signed in" : "not signed in")} · daemon: {agy.Daemon}";
            var addons = await State.Addons.StatusAsync(State.Ssh!);
            AddonList.Items.Clear();
            AddonsEmpty.IsVisible = State.Addons.Items.Count == 0;
            foreach (var a in State.Addons.Items)
            {
                var st = addons.FirstOrDefault(x => x.Addon == a.Name);
                string detail = st is null ? a.Description
                    : st.Error is not null ? "error: " + st.Error
                    : string.Join(" · ", st.Values.Select(v => $"{v.Key}: {v.Value}"));
                AddonList.Items.Add(new TextBlock { Text = $"{a.Name}: {detail}", TextWrapping = TextWrapping.Wrap });
            }
        }
        catch (Exception ex)
        {
            AgyState.Text = "Could not read: " + ex.Message;
        }
    }

    private void RenderProjects(ServerStatus s)
    {
        string? selected = SelectedProject;
        ProjectList.Items.Clear();
        foreach (ProjectInfo proj in s.Projects)
        {
            var svc = s.Services.FirstOrDefault(x => x.Unit == $"teleporter-claude@{proj.Name}.service");
            string agent = svc is null ? "" : svc.Active == "active" ? "agent running" : $"agent {svc.Active}";
            var item = new ListBoxItem
            {
                Tag = proj.Name,
                [AutomationProperties.NameProperty] = proj.Name,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = proj.Name, FontWeight = FontWeight.SemiBold, MinWidth = 200 },
                        new TextBlock { Text = proj.IsGit ? "git" : "folder", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, Width = 50 },
                        new TextBlock { Text = "modified " + proj.Modified.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = agent, Foreground = SegmentBrushes[3], VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            ProjectList.Items.Add(item);
            if (proj.Name == selected) ProjectList.SelectedItem = item;
        }
    }

    private void RenderMemory(ServerStatus s)
    {
        long used = s.MemTotalKb - s.MemAvailableKb;
        MemText.Text = $"{ServerService.Gb(used)} used of {ServerService.Gb(s.MemTotalKb)}  ·  {ServerService.Gb(s.MemAvailableKb)} available"
                       + (s.SwapTotalKb > 0 ? $"  ·  swap {ServerService.Gb(s.SwapTotalKb - s.SwapFreeKb)} of {ServerService.Gb(s.SwapTotalKb)}" : "  ·  no swap");
        double width = MemBar.Bounds.Width;
        MemSegments.Children.Clear();
        GroupList.Items.Clear();
        long accounted = 0;
        int i = 0;
        foreach (MemoryUse g in s.Groups)
        {
            IBrush brush = SegmentBrushes[i++ % SegmentBrushes.Length];
            accounted += g.Kb;
            AddSegment(width, g.Kb, s.MemTotalKb, brush);
            GroupList.Items.Add(LegendRow(brush, g.Label, g.Kb));
        }
        long other = Math.Max(0, used - accounted);
        AddSegment(width, other, s.MemTotalKb, OtherBrush);
        GroupList.Items.Add(LegendRow(OtherBrush, "Everything else (system, caches)", other));
    }

    private void AddSegment(double width, long kb, long total, IBrush brush)
    {
        if (total <= 0 || width <= 0) return;
        MemSegments.Children.Add(new Border { Width = width * kb / total, Background = brush });
    }

    private static Control LegendRow(IBrush brush, string label, long kb) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children =
        {
            new Border { Width = 10, Height = 10, CornerRadius = new Avalonia.CornerRadius(2), Background = brush, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = label, Width = 300 },
            new TextBlock { Text = ServerService.Gb(kb), Classes = { "muted" } },
        },
    };

    private void RenderModels(ServerStatus s)
    {
        ModelList.Items.Clear();
        ModelsEmpty.IsVisible = s.LoadedModels.Count == 0;
        foreach (string model in s.LoadedModels)
        {
            var unload = new Button { Content = "Unload", Padding = new Avalonia.Thickness(10, 2) };
            unload.Click += async (_, _) =>
            {
                try { await State.Server!.UnloadModelAsync(model); await RefreshAsync(); }
                catch (Exception ex) { StatusLine.Text = "Unload failed: " + ex.Message; }
            };
            ModelList.Items.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { new TextBlock { Text = model, Width = 300, VerticalAlignment = VerticalAlignment.Center }, unload },
            });
        }
    }

    private void RenderServices(ServerStatus s)
    {
        ProcText.Text = $"{s.AccountProcesses} processes running as the server account.";
        ServiceList.Items.Clear();
        foreach (ServiceUnit svc in s.Services)
            ServiceList.Items.Add(new TextBlock { Text = $"{svc.Unit}: {svc.Active} ({svc.Sub})" });
        if (s.Services.Count == 0)
            ServiceList.Items.Add(new TextBlock { Text = "No agent services running.", Classes = { "muted" } });
    }

    private void RenderParked()
    {
        int days = State.Settings.ParkDays;
        var items = State.Registry.Data.Parked.OrderBy(p => p.ParkedAt).ToList();
        ParkedList.Items.Clear();
        ParkedEmpty.IsVisible = items.Count == 0;
        foreach (ParkedItem item in items)
        {
            bool expired = item.ExpiresAt(days) <= DateTimeOffset.Now;
            var delete = new Button { Content = expired ? "Delete…" : "Delete now…", Padding = new Avalonia.Thickness(10, 2) };
            delete.Click += async (_, _) => await DeleteParkedAsync(item);
            string where = item.Where == "server" ? "server" : "this PC";
            ParkedList.Items.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Margin = new Avalonia.Thickness(0, 2),
                Children =
                {
                    new TextBlock { Text = $"{item.Project} ({item.Kind}, {where})", Width = 260, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = $"set aside {item.ParkedAt:yyyy-MM-dd}, {item.Bytes / 1048576.0:N0} MB · " +
                               (expired ? "ready to delete" : $"kept until {item.ExpiresAt(days):yyyy-MM-dd}"),
                        Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, Width = 380,
                    },
                    delete,
                },
            });
        }
        int ready = items.Count(i => i.ExpiresAt(days) <= DateTimeOffset.Now);
        ExpiredBanner.IsVisible = ready > 0;
        ExpiredText.Text = $"{ready} set-aside cop{(ready == 1 ? "y is" : "ies are")} older than {days} days and can be deleted (bottom of this page). Nothing is deleted without your confirmation.";
    }

    private async Task DeleteParkedAsync(ParkedItem item)
    {
        if (State.Transfer is null) return;
        string where = item.Where == "server" ? "the server" : "this PC";
        bool ok = await Dialogs.ConfirmAsync(this, "Delete set-aside copy",
            $"Permanently delete the set-aside {item.Kind} copy of '{item.Project}' on {where}?\n\n{item.ParkedPath}\n\nThe live copy is not affected.", "Delete", danger: true);
        if (!ok) return;
        try
        {
            await State.Transfer.DeleteParkedAsync(item);
            StatusLine.Text = $"Deleted {item.ParkedPath}.";
        }
        catch (Exception ex)
        {
            StatusLine.Text = "Delete failed: " + ex.Message;
        }
        RenderParked();
    }

    // ─────────────────────────── projects ───────────────────────────

    private void OnProjectSelected(object? sender, SelectionChangedEventArgs e) => UpdateAgentButton();

    private void UpdateAgentButton()
    {
        string? name = SelectedProject;
        bool running = name is not null && _last?.Services.Any(s => s.Unit == $"teleporter-claude@{name}.service" && s.Active == "active") == true;
        AgentButton.Content = running ? "Stop Claude agent" : "Start Claude agent";
    }

    private async void OnCreateClicked(object? sender, RoutedEventArgs e)
    {
        if (State.Server is null) { StatusLine.Text = "Configure a server first."; return; }
        string name = (NewName.Text ?? "").Trim();
        CreateButton.IsEnabled = false;
        try
        {
            await State.Server.CreateProjectAsync(name, NewGit.IsChecked == true);
            NewName.Text = "";
            StatusLine.Text = $"Created {name}.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusLine.Text = ex.Message;
        }
        finally
        {
            CreateButton.IsEnabled = true;
        }
    }

    private async void OnSendClicked(object? sender, RoutedEventArgs e)
    {
        if (State.Transfer is null) { StatusLine.Text = "Configure a server first."; return; }
        var dlg = new TransferDialog(State.Transfer, State.Settings, send: true);
        await dlg.ShowDialog(this);
        if (dlg.Changed) { RenderParked(); await RefreshAsync(); }
    }

    private async void OnBringBackClicked(object? sender, RoutedEventArgs e)
    {
        if (State.Transfer is null || State.Settings.ActiveServer is not { } p) return;
        if (SelectedProject is not { } name) { StatusLine.Text = "Select a project first."; return; }
        string dest = State.Registry.FindProject(p.Id, name)?.LocalPath
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects", name);
        var dlg = new TransferDialog(State.Transfer, State.Settings, send: false, serverProject: name, defaultDestination: dest);
        await dlg.ShowDialog(this);
        if (dlg.Changed) { RenderParked(); await RefreshAsync(); }
    }

    private async void OnOpenInCodeClicked(object? sender, RoutedEventArgs e)
    {
        if (SelectedProject is not { } name) { StatusLine.Text = "Select a project first."; return; }
        await OpenInCodeAsync(name);
    }

    /// <summary>Also used by the stub's "Open on server" launcher (<c>--open server/project</c>).</summary>
    public async Task OpenInCodeAsync(string name)
    {
        if (State.Transfer is null || State.Settings.ActiveServer is not { } p) return;
        if (!VsCode.IsConfigured(State.Settings.SshPath))
        {
            StatusLine.Text = "Set up VS Code first (Settings tab).";
            Tabs.SelectedIndex = 2;
            return;
        }
        try
        {
            await State.RefreshSshConfigAsync();
            VsCode.OpenFolder(p, await State.Transfer.ServerProjectPathAsync(name));
        }
        catch (Exception ex)
        {
            StatusLine.Text = "Could not open VS Code: " + ex.Message;
        }
    }

    private async void OnOpenInAgyClicked(object? sender, RoutedEventArgs e)
    {
        if (SelectedProject is not { } name) { StatusLine.Text = "Select a project first."; return; }
        if (State.Transfer is null || State.Settings.ActiveServer is not { } p) return;
        if (!AntigravityIde.IsConfigured())
        {
            StatusLine.Text = "Set up the Antigravity IDE first (Settings tab).";
            Tabs.SelectedIndex = 2;
            return;
        }
        try
        {
            await State.RefreshSshConfigAsync();
            AntigravityIde.OpenFolder(p, await State.Transfer.ServerProjectPathAsync(name), State.Settings.AntigravityIdeCommand);
        }
        catch (Exception ex)
        {
            StatusLine.Text = "Could not open the Antigravity IDE: " + ex.Message;
        }
    }

    private async void OnSetupAgyIdeClicked(object? sender, RoutedEventArgs e)
    {
        bool ok = await Dialogs.ConfirmAsync(this, "Set up the Antigravity IDE",
            "Teleporter will set the Antigravity IDE's two Remote-SSH settings (a backup of its settings.json is kept), " +
            "and add one Include line at the top of ~/.ssh/config so that ssh finds Teleporter's servers: the IDE's " +
            "Remote-SSH reads only that file. The Include uses a path Windows' own ssh ignores, so nothing else changes.", "Set up");
        if (!ok) return;
        try
        {
            AntigravityIde.Configure(State.Settings.SshPath);
            await State.RefreshSshConfigAsync();
            AgyIdeState.Text = "Configured.";
        }
        catch (Exception ex)
        {
            AgyIdeState.Text = "Failed: " + ex.Message;
        }
    }

    private async void OnTerminalClicked(object? sender, RoutedEventArgs e)
    {
        if (State.Transfer is null || State.Ssh is null || State.Settings.ActiveServer is not { } p) return;
        if (SelectedProject is not { } name) { StatusLine.Text = "Select a project first."; return; }
        try
        {
            string dir = await State.Transfer.ServerProjectPathAsync(name);
            await Terminal.OpenAsync(State.Ssh, name, Terminal.ShellIn(dir, p.Container));
        }
        catch (Exception ex)
        {
            StatusLine.Text = "Could not open a terminal: " + ex.Message;
        }
    }

    private async void OnAgentClicked(object? sender, RoutedEventArgs e)
    {
        if (State.Agents is null || State.Transfer is null || _last is null) return;
        if (SelectedProject is not { } name) { StatusLine.Text = "Select a project first."; return; }
        AgentButton.IsEnabled = false;
        try
        {
            bool running = _last.Services.Any(s => s.Unit == $"teleporter-claude@{name}.service" && s.Active == "active");
            if (running)
            {
                await State.Agents.StopClaudeAsync(name);
                StatusLine.Text = $"Claude agent for {name} stopped.";
            }
            else
            {
                if (!await State.Agents.ClaudeSignedInAsync())
                {
                    StatusLine.Text = "Sign Claude Code in on the server first (Server tab).";
                    return;
                }
                bool isGit = _last.Projects.FirstOrDefault(x => x.Name == name)?.IsGit == true;
                await State.Agents.StartClaudeAsync(name, await State.Transfer.ServerProjectPathAsync(name), isGit);
                StatusLine.Text = $"Claude agent for {name} started: find it in Claude Desktop, claude.ai/code or the Claude app.";
            }
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusLine.Text = "Agent: " + ex.Message;
        }
        finally
        {
            AgentButton.IsEnabled = true;
        }
    }

    // ─────────────────────────── server ───────────────────────────

    private async void OnStopAllClicked(object? sender, RoutedEventArgs e)
    {
        if (State.Server is null) return;
        bool ok = await Dialogs.ConfirmAsync(this, "Stop everything",
            "End every process of the server account (agents, VS Code and Antigravity servers, terminals, builds, containers)? Unsaved work in those processes is lost. Other accounts and system services are not touched.",
            "Stop everything", danger: true);
        if (!ok) return;
        StopAllButton.IsEnabled = false;
        try
        {
            await State.Server.StopEverythingAsync();
            StatusLine.Text = "Stopping all server-account processes…";
            await Task.Delay(3000);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusLine.Text = "Stop failed: " + ex.Message;
        }
        finally
        {
            StopAllButton.IsEnabled = true;
        }
    }

    private async void OnAgentsRefreshClicked(object? sender, RoutedEventArgs e) => await RefreshAgentsAsync();

    private async void OnClaudeSignInClicked(object? sender, RoutedEventArgs e)
    {
        if (State.Ssh is null) return;
        var ssh = State.Ssh;
        var dlg = new SignInDialog("Sign Claude Code in on the server",
            "Your browser opens Claude's sign-in page. Approve with your claude.ai account (Remote Control needs a subscription login), then paste the code it shows below.",
            () => SignInDialog.StartPtyAsync(ssh, "\"$HOME/.local/bin/claude\" auth login --claudeai"),
            "/oauth/authorize");
        await dlg.ShowDialog(this);
        await RefreshAgentsAsync();
    }

    private async void OnAgySignInClicked(object? sender, RoutedEventArgs e)
    {
        if (State.Ssh is null || State.Agents is null) return;
        await Terminal.OpenAsync(State.Ssh, "Antigravity sign-in", State.Agents.SignInCommand());
    }

    private async void OnAgyInstallClicked(object? sender, RoutedEventArgs e) =>
        await AgentActionAsync("Antigravity installed or updated.", a => a.InstallAntigravityAsync());

    private async void OnAgyStartClicked(object? sender, RoutedEventArgs e) =>
        await AgentActionAsync("Antigravity daemon started. Find it in the Antigravity dashboard.", a => a.StartAntigravityDaemonAsync());

    private async void OnAgyStopClicked(object? sender, RoutedEventArgs e) =>
        await AgentActionAsync("Antigravity daemon stopped.", a => a.StopAntigravityDaemonAsync());

    private async Task AgentActionAsync(string success, Func<Agents.AgentService, Task> action)
    {
        if (State.Agents is null) return;
        StatusLine.Text = "Working…";
        try
        {
            await action(State.Agents);
            StatusLine.Text = success;
        }
        catch (Exception ex)
        {
            StatusLine.Text = ex.Message;
        }
        await RefreshAgentsAsync();
    }

    // ─────────────────────────── settings ───────────────────────────

    private void LoadSettingsIntoForm()
    {
        AppSettings s = State.Settings;
        ServerProfile p = s.ActiveServer ?? new ServerProfile();
        SName.Text = p.DisplayName;
        SProvider.SelectedIndex = p.Provider == "external" ? 1 : 0;
        SProviderCmd.Text = p.ProviderCommand;
        SHost.Text = p.Host;
        SUser.Text = p.User;
        SPort.Value = p.Port;
        SRoot.Text = p.ProjectsRoot;
        SContainer.Text = p.Container;
        SLlama.Text = p.LlamaSwapUrl;
        SWatch.Text = string.Join(' ', p.WatchedServices);
        SSshPath.Text = s.SshPath;
        SParking.Text = s.ParkingFolder;
        SParkDays.Value = s.ParkDays;
        SSkip.Text = string.Join(' ', s.SkipDirNames);
        STheme.SelectedIndex = s.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        SAutostart.IsChecked = Autostart.IsEnabled;
        SDataDir.Text = AppPaths.Root + (AppPaths.IsPortable ? "" : " (fallback: the exe's folder is read-only)");
        VsCodeState.Text = VsCode.IsConfigured(s.SshPath) ? "Configured." : "Not configured yet.";
        SAgyLauncher.Text = s.AntigravityIdeCommand;
        AgyIdeState.Text = !AntigravityIde.Installed ? "Not installed on this PC." : AntigravityIde.IsConfigured() ? "Configured." : "Not configured yet.";
        AgyIdeButton.IsEnabled = OpenAgyButton.IsVisible = AntigravityIde.Installed;
        UpdateProviderFields();
    }

    private void OnProviderChanged(object? sender, SelectionChangedEventArgs e) => UpdateProviderFields();

    private void UpdateProviderFields()
    {
        if (SProviderCmd is null) return;
        SProviderCmd.IsEnabled = SProvider.SelectedIndex == 1;
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            AppSettings s = State.Settings;
            ServerProfile p = s.ActiveServer ?? new ServerProfile();
            p.DisplayName = string.IsNullOrWhiteSpace(SName.Text) ? "Server" : SName.Text.Trim();
            if (!s.Servers.Contains(p))
                p.Id = new string(p.DisplayName.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray()) is { Length: > 0 } id ? id : "server";
            p.Provider = SProvider.SelectedIndex == 1 ? "external" : "openssh";
            p.ProviderCommand = string.IsNullOrWhiteSpace(SProviderCmd.Text) ? null : SProviderCmd.Text.Trim();
            p.Host = (SHost.Text ?? "").Trim();
            p.User = string.IsNullOrWhiteSpace(SUser.Text) ? "dev" : SUser.Text.Trim();
            p.Port = (int)(SPort.Value ?? 22);
            p.ProjectsRoot = string.IsNullOrWhiteSpace(SRoot.Text) ? "~/projects" : SRoot.Text.Trim();
            p.Container = string.IsNullOrWhiteSpace(SContainer.Text) ? null : SContainer.Text.Trim();
            p.LlamaSwapUrl = string.IsNullOrWhiteSpace(SLlama.Text) ? null : SLlama.Text.Trim();
            p.WatchedServices = (SWatch.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!s.Servers.Contains(p)) s.Servers.Add(p);
            s.ActiveServerId = p.Id;
            s.SshPath = (SSshPath.Text ?? "ssh").Trim();
            s.ParkingFolder = (SParking.Text ?? "").Trim();
            s.ParkDays = (int)(SParkDays.Value ?? 30);
            s.SkipDirNames = (SSkip.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            s.AntigravityIdeCommand = (SAgyLauncher.Text ?? "").Trim();
            s.Theme = STheme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
            State.Save(s);

            if (SAutostart.IsChecked == true && !Autostart.IsEnabled) Autostart.Enable();
            else if (SAutostart.IsChecked != true && Autostart.IsEnabled) Autostart.Disable();

            SaveState.Text = "Saved.";
            RenderParked();
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            SaveState.Text = "Not saved: " + ex.Message;
        }
    }

    private async void OnSetupVsCodeClicked(object? sender, RoutedEventArgs e)
    {
        string ssh = State.Settings.SshPath;
        ServerProfile? p = State.Settings.ActiveServer;
        if (p is null) { VsCodeState.Text = "Save a server first."; return; }
        bool ok = await Dialogs.ConfirmAsync(this, "Set up VS Code",
            "Teleporter will set three VS Code user settings (a backup of settings.json is kept next to it):\n\n" +
            $"remote.SSH.configFile = Teleporter's ssh config\nremote.SSH.path = {ssh}\nremote.SSH.remotePlatform: this server = linux\n\n" +
            "Remote-SSH then sees only Teleporter's servers, through Git's ssh. It needs the Remote - SSH extension.", "Set up");
        if (!ok) return;
        try
        {
            string backup = VsCode.Configure(ssh, VsCode.HostAlias(p));
            await State.RefreshSshConfigAsync();
            VsCodeState.Text = "Configured. Backup: " + backup;
        }
        catch (Exception ex)
        {
            VsCodeState.Text = "Failed: " + ex.Message;
        }
    }
}
