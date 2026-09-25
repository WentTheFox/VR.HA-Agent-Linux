using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;
using ManifestResult = VRHAAgent.Agent.ManifestResult;

namespace VRHAAgent.UI;

/// <summary>Port of the Windows app's SettingsPage; same settings, order and messages where they apply.</summary>
public partial class SettingsPage : UserControl
{
    private bool _isInitializing;
    private bool _ignoreAutoStartToggleThisTime;

    private static AgentConfig Config => App.Agent.Config;

    public SettingsPage()
    {
        InitializeComponent();

        _isInitializing = true;

        PortBox.Value = Config.Port;
        LaunchMinimizedToggle.IsChecked = Config.LaunchMinimized;
        EnableTrayToggle.IsChecked = Config.EnableTray;
        ExitWithSteamVRToggle.IsChecked = Config.ExitWithSteamVR;
        AlwaysOnTopToggle.IsChecked = Config.AlwaysOnTop;
        EnableNotifyPluginToggle.IsChecked = Config.EnableAdvancedNotifications;
        RuntimeComboBox.SelectedItem = RuntimeComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string?)i.Tag == Config.Runtime) ?? RuntimeComboBox.Items[0];
        StartCommandBox.Text = Config.StartRuntimeCommand ?? "";
        HardwareAccelerationToggle.IsChecked = Config.HardwareAcceleration;
        RestartButton.IsVisible = Config.HardwareAcceleration != App.HardwareAccelerationActive;
        VersionText.Text = Paths.Version;

        _ = InitStartWithLoginToggle();

        if (Config.AutoLaunchWithSteamVR)
        {
            AutoStartToggle.IsChecked = true;
            if (File.Exists(Paths.ManifestFile))
            {
                AutoStartInfoBar.Title = "Manifest file path: " + Paths.ManifestFile;
                AutoStartInfoBar.Severity = FAInfoBarSeverity.Success;
            }
            else
            {
                AutoStartInfoBar.Title = "Manifest will be registered the next time SteamVR runs";
                AutoStartInfoBar.Severity = FAInfoBarSeverity.Warning;
            }

            AutoStartInfoBar.IsOpen = true;
            SaveManifestButton.Content = "Change";
        }
        else
        {
            AutoStartToggle.IsChecked = false;
            AutoStartInfoBar.Title = "Manifest file path is not set, auto start is disabled";
            AutoStartInfoBar.Severity = FAInfoBarSeverity.Warning;
            AutoStartInfoBar.IsOpen = true;
            ExportManifestCard.IsEnabled = false;
        }

        _isInitializing = false;
    }

    private void PortBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SavePortButton_OnClick(SavePortButton, new RoutedEventArgs());
        else SavePortButton.Content = "Save";
    }

    private async void SavePortButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (double.IsNaN(PortBox.Value)) return;
        var oldPort = Config.Port;
        Config.Port = (int)PortBox.Value;
        App.Current.SaveConfig();
        SavePortButton.Content = await App.Agent.SetPortAsync(Config.Port, oldPort) ? "Saved!" : "Port in use";
    }

    private void LaunchMinimized_OnToggled(object? sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        Config.LaunchMinimized = LaunchMinimizedToggle.IsChecked == true;
        App.Current.SaveConfig();
    }

    private void EnableTray_OnToggled(object? sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        Config.EnableTray = EnableTrayToggle.IsChecked == true;
        App.Current.SaveConfig();
        App.Current.SetTrayIconVisibility(Config.EnableTray);
    }

    private void AlwaysOnTop_OnToggled(object? sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        Config.AlwaysOnTop = AlwaysOnTopToggle.IsChecked == true;
        App.Current.SaveConfig();
        if (App.Current.MainWindow is { } window) window.Topmost = Config.AlwaysOnTop;
    }

    private async Task InitStartWithLoginToggle()
    {
        var enabled = await StartWithLogin.IsEnabledAsync();
        _isInitializing = true;
        StartWithLoginToggle.IsChecked = enabled == true;
        _isInitializing = false;
        if (enabled == null)
        {
            StartWithLoginToggle.IsEnabled = false;
            StartWithLoginCard.Description = "Install the app with install.sh to start it automatically when you log in.";
        }
    }

    private async void StartWithLogin_OnToggled(object? sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        var wanted = StartWithLoginToggle.IsChecked == true;
        if (!await StartWithLogin.SetEnabledAsync(wanted))
        {
            // Revert, like the Windows app does when the startup task can't be changed.
            _isInitializing = true;
            StartWithLoginToggle.IsChecked = !wanted;
            _isInitializing = false;
        }
    }

    private void ExitWithSteamVR_OnToggled(object? sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        Config.ExitWithSteamVR = ExitWithSteamVRToggle.IsChecked == true;
        App.Current.SaveConfig();
    }

    private void EnableNotifyPlugin_OnToggled(object? sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        Config.EnableAdvancedNotifications = EnableNotifyPluginToggle.IsChecked == true;
        App.Current.SaveConfig();
    }

    private void Runtime_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || RuntimeComboBox.SelectedItem is not ComboBoxItem { Tag: string runtime }) return;
        Config.Runtime = runtime;
        App.Current.SaveConfig();
    }

    private void HardwareAcceleration_OnToggled(object? sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        Config.HardwareAcceleration = HardwareAccelerationToggle.IsChecked == true;
        App.Current.SaveConfig();
        RestartButton.IsVisible = Config.HardwareAcceleration != App.HardwareAccelerationActive;
    }

    private void Restart_OnClick(object? sender, RoutedEventArgs e) => App.Current.Restart();

    private void SaveStartCommand_OnClick(object? sender, RoutedEventArgs e)
    {
        var command = StartCommandBox.Text?.Trim();
        Config.StartRuntimeCommand = string.IsNullOrEmpty(command) ? null : command;
        App.Current.SaveConfig();
        SaveStartCommandButton.Content = "Saved!";
    }

    #region Auto start (SteamVR manifest)

    private async Task<ManifestResult> SaveManifest()
    {
        Config.AutoLaunchWithSteamVR = true;
        App.Current.SaveConfig();
        var result = await App.Agent.RegisterManifestAsync();
        if (result == ManifestResult.Success)
        {
            AutoStartInfoBar.Title = "Manifest file path: " + Paths.ManifestFile;
            AutoStartInfoBar.Severity = FAInfoBarSeverity.Success;
            SaveManifestButton.Content = "Change";
        }

        return result;
    }

    private async Task SaveManifestAndUpdateUI()
    {
        var result = await SaveManifest();
        _ignoreAutoStartToggleThisTime = AutoStartToggle.IsChecked != true;
        AutoStartToggle.IsChecked = true;
        AutoStartExpander.IsExpanded = true;
        AutoStartInfoBar.IsOpen = true;
        AutoStartInfoBarActionButton.IsVisible = result != ManifestResult.Success;
        ExportManifestCard.IsEnabled = true;

        switch (result)
        {
            case ManifestResult.SteamVRNotRunning:
                AutoStartInfoBar.Title = "Failed to set up auto start, SteamVR is not running. " +
                                         "The app will register the manifest when SteamVR runs.";
                AutoStartInfoBar.Severity = FAInfoBarSeverity.Error;
                break;
            case ManifestResult.FailedToRegister:
                AutoStartInfoBar.Title =
                    "Failed to register the manifest, please try again or try to edit SteamVR's settings manually. The app will try to register the manifest again when it starts next time.";
                AutoStartInfoBar.Severity = FAInfoBarSeverity.Error;
                break;
            case ManifestResult.FailedToSave:
                AutoStartInfoBar.Title = "Failed to save the manifest file, please try again";
                AutoStartInfoBar.Severity = FAInfoBarSeverity.Error;
                break;
        }
    }

    private async Task DeleteManifest()
    {
        Config.AutoLaunchWithSteamVR = false;
        App.Current.SaveConfig();
        await App.Agent.UnregisterManifestAsync();

        AutoStartInfoBar.Title = "Manifest file path is not set, auto start is disabled";
        AutoStartInfoBar.Severity = FAInfoBarSeverity.Warning;
        SaveManifestButton.Content = "Save Manifest";
        AutoStartInfoBarActionButton.IsVisible = false;
    }

    private void SetSavingState()
    {
        AutoStartInfoBar.Title = "Setting up auto start...";
        AutoStartInfoBar.Severity = FAInfoBarSeverity.Informational;
        AutoStartInfoBar.IsOpen = true;
        SaveManifestButton.Content = "Saving...";
    }

    private async void SaveManifestButton_OnClick(object? sender, RoutedEventArgs e)
    {
        SetSavingState();
        await SaveManifestAndUpdateUI();
    }

    private async void AutoStart_OnToggled(object? sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (_ignoreAutoStartToggleThisTime)
        {
            _ignoreAutoStartToggleThisTime = false;
            return;
        }

        var on = AutoStartToggle.IsChecked == true;
        AutoStartExpander.IsExpanded = on;
        ExportManifestCard.IsEnabled = on;
        if (on)
        {
            SetSavingState();
            await SaveManifestAndUpdateUI();
        }
        else
        {
            await DeleteManifest();
        }
    }

    private async void AutoStartInfoBar_ActionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        SetSavingState();
        await SaveManifestAndUpdateUI();
    }

    #endregion
}
