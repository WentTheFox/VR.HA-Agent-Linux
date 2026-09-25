using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VRHAAgent.UI;

/// <summary>
/// The Windows app embedded this editor in a WebView2. Avalonia has no bundled web view on Linux, so this
/// opens the same page in the default browser.
/// </summary>
public partial class NotificationEditorPage : UserControl
{
    public NotificationEditorPage() => InitializeComponent();

    private async void OpenEditor_OnClick(object? sender, RoutedEventArgs e)
    {
        var url = new Uri($"https://openvroverlaypipeeditor.tiiny.site?port={App.Agent.Config.Port}");
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher != null) await launcher.LaunchUriAsync(url);
    }
}
