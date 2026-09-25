using Avalonia.Controls;
using FluentAvalonia.UI.Controls;

namespace VRHAAgent.UI;

public partial class MainWindow : Window
{
    private readonly HomePage _home = new();
    private readonly NotificationEditorPage _notificationEditor = new();
    private SettingsPage? _settings;

    public MainWindow()
    {
        InitializeComponent();
        Topmost = App.Agent.Config.AlwaysOnTop;

        NavigationViewControl.SelectedItem = NavigationViewControl.MenuItems.OfType<FANavigationViewItem>().First();
        NavigationViewControl.Content = _home;

        PropertyChanged += (_, e) =>
        {
            // Minimize to tray instead of the taskbar when the tray icon is enabled.
            if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized && App.Agent.Config.EnableTray)
                Hide();
        };
    }

    /// <summary>Closing the window exits the app, as on Windows.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel) App.Current.Exit();
    }

    public void NavigateToSettings() => NavigationViewControl.SelectedItem = NavigationViewControl.SettingsItem;

    private void NavigationViewControl_OnSelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        if (e.IsSettingsSelected)
        {
            // Rebuilt on every visit so it reflects the current state.
            _settings = new SettingsPage();
            NavigationViewControl.Content = _settings;
            return;
        }

        NavigationViewControl.Content = (e.SelectedItem as FANavigationViewItem)?.Tag switch
        {
            "NotificationEditor" => _notificationEditor,
            _ => _home,
        };
    }
}
