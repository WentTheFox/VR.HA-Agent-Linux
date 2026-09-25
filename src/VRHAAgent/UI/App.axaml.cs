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
