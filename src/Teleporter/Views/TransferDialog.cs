using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Teleporter.Core;
using Teleporter.Transfer;

namespace Teleporter.Views;

/// <summary>
/// Send to server / Bring back, in one modal window: options, then a pre-check that hashes
/// everything and lists problems (block) and warnings (need a tick), then the transfer with
/// progress. Nothing moves before the user presses the final button.
/// </summary>
internal sealed class TransferDialog : Window
{
    private readonly bool _send;
    private readonly TransferService _svc;
    private readonly TextBox _path = new() { Width = 420 };
    private readonly TextBox _name = new() { Width = 260 };
    private readonly CheckBox _skipDeps = new() { IsChecked = true };
    private readonly CheckBox _sessions = new() { IsChecked = false, Content = "Also move Claude Code sessions and memory" };
    private readonly CheckBox _setAside;
    private readonly CheckBox _accept = new() { Content = "I have read the warnings above", IsVisible = false };
    private readonly TextBlock _report = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 640 };
    private readonly TextBlock _step = new() { Classes = { "muted" } };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
    private readonly Button _check = new() { Content = "Check" };
    private readonly Button _go = new() { Classes = { "accent" }, IsEnabled = false };
    private readonly Button _close = new() { Content = "Cancel" };
    private CancellationTokenSource? _cts;
    private Manifest? _manifest;
    private bool _done;

    public bool Changed { get; private set; }

    public TransferDialog(TransferService svc, AppSettings settings, bool send, string? serverProject = null, string? defaultDestination = null)
    {
        _svc = svc;
        _send = send;
        Title = send ? "Send a project to the server" : $"Bring {serverProject} back to this PC";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        ShowInTaskbar = false;

        string skipList = string.Join(", ", settings.SkipDirNames);
        _skipDeps.Content = $"Skip {skipList} (they hold OS-specific binaries; rebuild them on the other side)";
        _setAside = new CheckBox
        {
            IsChecked = true,
            Content = send
                ? "Set the local copy aside (a stub with an \"Open on server\" link stays in its place)"
                : "Set the server copy aside (kept in ~/.teleporter/parked until you delete it)",
        };
        if (!send)
        {
            _name.Text = serverProject;
            _name.IsEnabled = false;
            _path.Text = defaultDestination;
        }
        _go.Content = send ? "Send to server" : "Bring back";

        var browse = new Button { Content = send ? "Browse…" : "Choose…" };
        browse.Click += async (_, _) => await BrowseAsync();
        _path.TextChanged += (_, _) =>
        {
            if (_send && string.IsNullOrWhiteSpace(_name.Text) && !string.IsNullOrWhiteSpace(_path.Text))
                _name.Text = Path.GetFileName(_path.Text.TrimEnd('\\', '/'));
            Invalidate();
        };
        _skipDeps.IsCheckedChanged += (_, _) => Invalidate();
        _accept.IsCheckedChanged += (_, _) => _go.IsEnabled = _manifest is not null && _accept.IsChecked == true;
        _check.Click += async (_, _) => await CheckAsync();
        _go.Click += async (_, _) => await RunAsync();
        _close.Click += (_, _) => { if (_cts is not null && !_done) _cts.Cancel(); else Close(); };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 10,
            Children =
            {
                Row(send ? "Local folder" : "Put it in", _path, browse),
                Row("Name on the server", _name),
                _skipDeps, _sessions, _setAside,
                new Border { Height = 1, Background = Brushes.Gray, Opacity = 0.3 },
                _report, _accept, _bar, _step,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { _close, _check, _go } },
            },
        };
    }

    private static Control Row(string label, params Control[] controls)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center });
        foreach (var c in controls) p.Children.Add(c);
        return p;
    }

    private void Invalidate()
    {
        _manifest = null;
        _go.IsEnabled = false;
        _accept.IsVisible = false;
        _accept.IsChecked = false;
    }

    private async Task BrowseAsync()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _send ? "Project folder to send" : "Folder to put the project in (the project folder itself)",
            AllowMultiple = false,
        });
        string? path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        _path.Text = _send ? path : Path.Combine(path, _name.Text ?? "");
    }

    private Progress<TransferUpdate> NewProgress() => new(u =>
    {
        _step.Text = u.Detail is null ? u.Step : $"{u.Step}  ·  {u.Detail}";
        _bar.IsVisible = true;
        _bar.IsIndeterminate = u.Fraction is null;
        if (u.Fraction is { } f) _bar.Value = f;
    });

    private async Task CheckAsync()
    {
        string path = (_path.Text ?? "").Trim();
        if (path.Length == 0) { _report.Text = "Choose a folder first."; return; }
        if (_send && !Directory.Exists(path)) { _report.Text = "That folder does not exist."; return; }
        _check.IsEnabled = false;
        _cts = new CancellationTokenSource();
        var progress = NewProgress();
        try
        {
            PreCheck pc;
            if (_send)
            {
                bool skip = _skipDeps.IsChecked == true;
                _manifest = await Task.Run(() => _svc.ScanLocal(path, skip, progress, _cts.Token));
                pc = _svc.PreCheckSend(Options(), _manifest);
            }
            else
            {
                (pc, _manifest) = await _svc.PreCheckBringBackAsync(BackOptions(), progress, _cts.Token);
            }
            _bar.IsVisible = false;
            _step.Text = "";
            var lines = new System.Collections.Generic.List<string> { $"{pc.Files:N0} files, {pc.Bytes / 1048576.0:N1} MB, all hashed (SHA-256)." };
            if (pc.Problems.Count > 0) lines.Add("\nProblems (fix these first):\n• " + string.Join("\n• ", pc.Problems));
            if (pc.Warnings.Count > 0) lines.Add("\nWarnings:\n• " + string.Join("\n• ", pc.Warnings));
            _report.Text = string.Join("\n", lines);
            if (pc.Problems.Count > 0) { _manifest = null; return; }
            _accept.IsVisible = pc.Warnings.Count > 0;
            _go.IsEnabled = pc.Warnings.Count == 0;
        }
        catch (OperationCanceledException)
        {
            _report.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            _report.Text = "Check failed: " + ex.Message;
            _manifest = null;
        }
        finally
        {
            _check.IsEnabled = true;
            _bar.IsVisible = false;
        }
    }

    private SendOptions Options() => new()
    {
        LocalPath = (_path.Text ?? "").Trim(),
        Name = (_name.Text ?? "").Trim(),
        SkipDependencyFolders = _skipDeps.IsChecked == true,
        MoveSessions = _sessions.IsChecked == true,
        SetAsideLocalCopy = _setAside.IsChecked == true,
    };

    private BringBackOptions BackOptions() => new()
    {
        Name = (_name.Text ?? "").Trim(),
        Destination = (_path.Text ?? "").Trim(),
        SkipDependencyFolders = _skipDeps.IsChecked == true,
        MoveSessions = _sessions.IsChecked == true,
        SetAsideServerCopy = _setAside.IsChecked == true,
    };

    private async Task RunAsync()
    {
        if (_manifest is null) return;
        _go.IsEnabled = _check.IsEnabled = false;
        foreach (var c in new Control[] { _path, _name, _skipDeps, _sessions, _setAside, _accept }) c.IsEnabled = false;
        _cts = new CancellationTokenSource();
        var progress = NewProgress();
        try
        {
            var warnings = _send
                ? await _svc.SendAsync(Options(), _manifest, progress, _cts.Token)
                : await _svc.BringBackAsync(BackOptions(), _manifest, progress, _cts.Token);
            _done = true;
            Changed = true;
            _report.Text = (_send
                ? "Done. Every file was verified on the server before anything was set aside."
                : "Done. Every file was verified on this PC before anything was set aside.")
                + (warnings.Count == 0 ? "" : "\n\nThe project moved, but some follow-up steps did not finish:\n• " + string.Join("\n• ", warnings));
            _close.Content = "Close";
        }
        catch (OperationCanceledException)
        {
            _report.Text = "Cancelled. The source was not changed.";
            _done = true;
            _close.Content = "Close";
        }
        catch (Exception ex)
        {
            _report.Text = "Stopped: " + ex.Message;
            _done = true;
            Changed = true;
            _close.Content = "Close";
            Log.Error($"transfer failed: {ex}");
        }
        finally
        {
            _bar.IsVisible = false;
            Dispatcher.UIThread.Post(() => _close.Focus());
        }
    }
}
