using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Teleporter.Views;

namespace Teleporter;

/// <summary>
/// Tray-first shell. The main window is created when shown and DESTROYED when closed
/// (never just hidden): a hidden Avalonia window can keep its render loop alive, and the
/// whole point is zero cost while nobody is looking.
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        RequestedThemeVariant = AppState.Current.Settings.Theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SingleInstance.ActivationRequested += () => Dispatcher.UIThread.Post(ShowWindow);
            SingleInstance.StartListening();
            if (!Program.StartInTray) ShowWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) =>
            {
                _window = null;
                // Give the memory of the window (render surfaces, fonts, layout) back to Windows.
                // Tray-only, the app needs a few MB; without this it would sit on ~200 MB.
                Dispatcher.UIThread.Post(Idle.Trim, DispatcherPriority.Background);
            };
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();

        if (OpenRequest.Take() is { } request)
        {
            var settings = AppState.Current.Settings;
            if (request.Server is { } id && id != settings.ActiveServerId && settings.Servers.Exists(s => s.Id == id))
            {
                settings.ActiveServerId = id;
                AppState.Current.Save(settings);
            }
            _ = _window.OpenInCodeAsync(request.Project);
        }
    }

    private void OnTrayClicked(object? sender, EventArgs e) => ShowWindow();
    private void OnOpenClicked(object? sender, EventArgs e) => ShowWindow();

    private void OnDataFolderClicked(object? sender, EventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });

    private async void OnExitClicked(object? sender, EventArgs e)
    {
        // Release first: an external provider may hold a key in a private agent.
        await AppState.Current.ShutdownAsync();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
