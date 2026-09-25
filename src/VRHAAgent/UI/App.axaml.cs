using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace VRHAAgent.UI;

public partial class App : Application
{
    /// <summary>Set by Program before the app starts.</summary>
    public static Agent Agent { get; set; } = null!;

    public static string ConfigPath { get; set; } = "";

    /// <summary>Whether this process started with GPU rendering (the setting only applies on restart).</summary>
    public static bool HardwareAccelerationActive { get; set; }

    /// <summary>Arguments to pass on when the app restarts itself.</summary>
    public static string[] CommandLineArgs { get; set; } = [];

    public static new App Current => (App)Application.Current!;

    public MainWindow? MainWindow { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SetTrayIconVisibility(Agent.Config.EnableTray);

            MainWindow = new MainWindow();
            if (!Agent.Config.LaunchMinimized)
            {
                MainWindow.Show();
            }
            else if (!Agent.Config.EnableTray)
            {
                MainWindow.Show();
                MainWindow.WindowState = WindowState.Minimized;
            }
            // else: start hidden in the tray, like the Windows app.
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void SaveConfig()
    {
        try
        {
            Agent.Config.Save(ConfigPath);
        }
        catch (Exception e)
        {
            Log.Error($"Could not save config: {e.Message}");
        }
    }

    public void SetTrayIconVisibility(bool visible)
    {
        foreach (var icon in TrayIcon.GetIcons(this) ?? []) icon.IsVisible = visible;
    }

    /// <summary>Shows, restores and raises the main window (tray click, second launch).</summary>
    public void ShowMainWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            MainWindow ??= new MainWindow();
            if (!MainWindow.IsVisible) MainWindow.Show();
            if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
            MainWindow.Activate();
        });
    }

    /// <summary>
    /// Restarts the app: through systemd when running as the service (so it stays managed), otherwise by
    /// launching a new instance that waits for this one to exit.
    /// </summary>
    public void Restart()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("INVOCATION_ID") != null)
            {
                // --no-block: systemd stops this process (SIGTERM, a clean exit) and then starts a new one.
                System.Diagnostics.Process.Start("systemctl",
                    ["--user", "restart", "--no-block", VR.SteamVRManifest.SystemdUnit])?.Dispose();
                return;
            }

            System.Diagnostics.Process.Start(Paths.Executable, [.. CommandLineArgs, "--wait-for-previous"])?.Dispose();
            Exit();
        }
        catch (Exception e)
        {
            Log.Error($"Could not restart: {e.Message}");
        }
    }

    public void Exit()
    {
        Dispatcher.UIThread.Post(() =>
        {
            SetTrayIconVisibility(false);
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        });
    }

    private void TrayIcon_OnClicked(object? sender, EventArgs e) => ShowMainWindow();
    private void ShowWindow_OnClick(object? sender, EventArgs e) => ShowMainWindow();
    private void Exit_OnClick(object? sender, EventArgs e) => Exit();
}
