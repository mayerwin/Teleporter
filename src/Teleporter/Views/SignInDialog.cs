using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Teleporter.Connection;

namespace Teleporter.Views;

/// <summary>
/// Signs an agent CLI in on the server without a console window. The CLI runs over ssh on a
/// remote pseudo-terminal (<c>ssh -tt</c>); the dialog picks the sign-in link out of its output,
/// opens it in the browser, and types the code the user pastes back into the CLI. A plain console
/// window was not good enough: Claude Code's prompt did not accept a paste in it.
/// </summary>
internal sealed partial class SignInDialog : Window
{
    [GeneratedRegex(@"https://[^\s\x07\x1b""'<>]+")]
    private static partial Regex Url();

    // CSI sequences, and OSC sequences (terminal hyperlinks) ending in BEL or ESC \.
    [GeneratedRegex(@"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)")]
    private static partial Regex Escapes();

    private readonly Func<Task<Process>> _start;
    private readonly Regex _urlFilter;
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _open = new() { Content = "Open the sign-in page", IsEnabled = false, Classes = { "accent" } };
    private readonly Button _copy = new() { Content = "Copy link", IsEnabled = false };
    private readonly TextBox _code = new() { Width = 420, PlaceholderText = "Paste the code from the browser here", IsEnabled = false };
    private readonly Button _submit = new() { Content = "Submit code", IsEnabled = false, Classes = { "accent" } };
    private readonly Button _close = new() { Content = "Cancel" };
    private readonly StringBuilder _output = new();
    private Process? _proc;
    private string? _url;

    public bool Succeeded { get; private set; }

    public SignInDialog(string title, string intro, Func<Task<Process>> start, string urlMustContain)
    {
        _start = start;
        _urlFilter = new Regex(Regex.Escape(urlMustContain));
        Title = title;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _open.Click += (_, _) => { if (_url is not null) Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true }); };
        _copy.Click += async (_, _) => { if (_url is not null && Clipboard is { } cb) await cb.SetTextAsync(_url); };
        _submit.Click += async (_, _) => await SubmitAsync();
        _code.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) await SubmitAsync(); };
        _close.Click += (_, _) => Close();
        Closed += (_, _) => { try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { } };
        Opened += async (_, _) => await StartAsync();

        _state.Text = "Starting the sign-in on the server…";
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _open, _copy } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _code, _submit } },
                _state,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _close } },
            },
        };
    }

    private async Task StartAsync()
    {
        try
        {
            _proc = await _start();
            _ = Pump(_proc.StandardOutput);
            _ = Pump(_proc.StandardError);
            await _proc.WaitForExitAsync();
            Dispatcher.UIThread.Post(() =>
            {
                Succeeded = _proc.ExitCode == 0;
                _state.Text = Succeeded ? "Signed in." : $"The sign-in ended without success (exit {_proc.ExitCode}). {LastLine()}";
                _close.Content = "Close";
                _code.IsEnabled = _submit.IsEnabled = false;
            });
        }
        catch (Exception ex)
        {
            _state.Text = "Could not start: " + ex.Message;
        }
    }

    private async Task Pump(System.IO.StreamReader reader)
    {
        char[] buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            string chunk = new(buffer, 0, n);
            Dispatcher.UIThread.Post(() => OnOutput(chunk));
        }
    }

    private void OnOutput(string chunk)
    {
        _output.Append(chunk);
        if (_url is not null) return;
        string raw = _output.ToString();
        foreach (Match m in Url().Matches(raw))
        {
            if (!_urlFilter.IsMatch(m.Value)) continue;
            _url = m.Value;
            _open.IsEnabled = _copy.IsEnabled = _code.IsEnabled = _submit.IsEnabled = true;
            _state.Text = "Open the sign-in page, approve, then paste the code you are given.";
            Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
            _code.Focus();
            return;
        }
    }

    private async Task SubmitAsync()
    {
        string code = (_code.Text ?? "").Trim();
        if (code.Length == 0 || _proc is null || _proc.HasExited) return;
        _submit.IsEnabled = false;
        _state.Text = "Checking the code…";
        // Typed into the remote pseudo-terminal as if from a keyboard; CR is the Enter key.
        await _proc.StandardInput.WriteAsync(code + "\r");
        await _proc.StandardInput.FlushAsync();
        _code.Text = "";
    }

    private string LastLine()
    {
        string clean = Escapes().Replace(_output.ToString(), "");
        var lines = clean.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
            if (lines[i].Trim().Length > 0 && !lines[i].Contains("http")) return lines[i].Trim();
        return "";
    }

    /// <summary>Start a remote command on a pseudo-terminal with raw stdin/stdout.</summary>
    public static async Task<Process> StartPtyAsync(SshClient ssh, string remoteCommand)
    {
        var (sshPath, args) = await ssh.InteractiveArgsAsync(remoteCommand);
        args.Remove("-t");
        args.Insert(0, "-tt"); // force a remote pty even though our stdin is a pipe
        var psi = new ProcessStartInfo(sshPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new InvalidOperationException("ssh did not start");
    }
}
