using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace VRHAAgent.UI;

public partial class HomePage : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public HomePage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Refresh();
        _ = RefreshAutoStartInfoBar();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    private void Refresh()
    {
        var agent = App.Agent;
        var runtime = agent.ConnectedRuntime;
        RuntimeStatusText.Text = runtime == null ? "Disconnected" : $"Connected ({runtime})";

        var display = agent.HeadsetDisplayState;
        HeadsetDisplayText.Text = display.State switch
        {
            HeadsetDisplayState.Ok => $"OK ({display.Connector})",
            HeadsetDisplayState.FallbackEdid => "Broken, power-cycle the headset",
            HeadsetDisplayState.NotDetected => "Not detected",
            _ => "Headset off",
        };

        WebSocketStatusText.Text = agent.IsServerListening
            ? agent.ClientCount switch
            {
                0 => "Started",
                1 => "Started (1 client)",
                var n => $"Started ({n} clients)",
            }
            : "Stopped";

        // Advanced notifications are built in on Linux, so "running" means SteamVR is there to show them.
        NotifyPluginCard.IsVisible = agent.Config.EnableAdvancedNotifications;
        NotifyPluginStatusText.Text = runtime switch
        {
            "SteamVR" => "Running",
            "Monado" => "Unavailable on Monado",
            _ => "Stopped",
        };
    }

    private async Task RefreshAutoStartInfoBar()
    {
        var startWithLogin = await StartWithLogin.IsEnabledAsync() == true;
        AutoStartInfoBar.IsOpen = !startWithLogin && !App.Agent.Config.AutoLaunchWithSteamVR;
    }

    private void OpenSettings_OnClick(object? sender, RoutedEventArgs e) => App.Current.MainWindow?.NavigateToSettings();
}
