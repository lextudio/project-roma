using ICSharpCode.ILSpy.Options;
using ICSharpCode.ILSpyX.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Roma.Host.Options;

public sealed partial class MiscSettingsPanel : UserControl, IOptionPage
{
    MiscSettingsViewModel _vm = new();

    public MiscSettingsPanel()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    public string Title => _vm.Title;

    public void Load(SettingsSnapshot snapshot) => _vm.Load(snapshot);

    public void LoadDefaults() => _vm.LoadDefaults();

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
