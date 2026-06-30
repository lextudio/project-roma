using ICSharpCode.ILSpy.Options;
using ICSharpCode.ILSpyX.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Roma.Host.Options;

public sealed partial class MiscSettingsPanel : UserControl, IOptionPage
{
    MiscSettingsViewModel _vm = new();
    RomaHostSettings? _hostSettings;
    bool _loading;

    public MiscSettingsPanel()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    public string Title => _vm.Title;

    public void Load(SettingsSnapshot snapshot)
    {
        _loading = true;
        _vm.Load(snapshot);
        _hostSettings = snapshot.GetSettings<RomaHostSettings>();
        _hideMacOSAppMenu.Visibility = OperatingSystem.IsMacOS()
            ? Visibility.Visible
            : Visibility.Collapsed;
        _hideMacOSAppMenu.IsChecked = _hostSettings.HideMacOSAppMenu;
        _recentFontsEnabled.IsChecked = _hostSettings.RecentFontsEnabled;

        var options = TerminalOptionsForCurrentOS();
        _terminalCombo.ItemsSource = options;
        var pref = string.IsNullOrEmpty(_hostSettings.PreferredTerminalApp) ? DefaultOption : _hostSettings.PreferredTerminalApp;
        _terminalCombo.SelectedItem = System.Array.IndexOf(options, pref) >= 0 ? pref : DefaultOption;
        _customTerminalPath.Text = _hostSettings.CustomTerminalPath ?? string.Empty;
        _customTerminalPath.Visibility = (_terminalCombo.SelectedItem as string) == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        _loading = false;
    }

    const string DefaultOption = "(Default)";

    static string[] TerminalOptionsForCurrentOS()
    {
        if (OperatingSystem.IsMacOS())
            return [DefaultOption, "System Default", "Terminal.app", "iTerm2", "Custom"];
        if (OperatingSystem.IsWindows())
            return [DefaultOption, "Command Prompt", "PowerShell", "PowerShell Core", "Windows Terminal", "Custom"];
        return [DefaultOption, "System Default", "GNOME Terminal", "Konsole", "Xfce Terminal", "XTerm", "Custom"];
    }

    private void OnTerminalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _hostSettings is null)
            return;
        var selected = _terminalCombo.SelectedItem as string;
        _hostSettings.PreferredTerminalApp = selected == DefaultOption ? string.Empty : selected ?? string.Empty;
        _customTerminalPath.Visibility = selected == "Custom" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCustomTerminalPathChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _hostSettings is null)
            return;
        _hostSettings.CustomTerminalPath = _customTerminalPath.Text ?? string.Empty;
    }

    private void OnRecentFontsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _hostSettings is null)
            return;
        _hostSettings.RecentFontsEnabled = _recentFontsEnabled.IsChecked == true;
    }

    public void LoadDefaults() => _vm.LoadDefaults();

    private void OnHideMacOSAppMenuChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _hostSettings is null)
            return;
        _hostSettings.HideMacOSAppMenu = _hideMacOSAppMenu.IsChecked == true;
    }

    private async void OnShellIntegrationClick(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title = "Shell Integration",
            Content = _vm.ShellIntegrationDescription,
            PrimaryButtonText = _vm.AddRemoveShellIntegrationText,
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            _vm.ApplyShellIntegration();
            _shellIntegrationBtn.Content = _vm.AddRemoveShellIntegrationText;
        }
    }
}
