using ICSharpCode.ILSpy.Options;
using ICSharpCode.ILSpyX.Settings;
using Microsoft.UI.Xaml.Controls;

namespace Roma.Host.Options;

public sealed partial class DisplaySettingsPanel : UserControl, IOptionPage
{
    DisplaySettingsViewModel _vm = new();
    bool _loading;

    public DisplaySettingsPanel()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    public string Title => _vm.Title;

    public void Load(SettingsSnapshot snapshot)
    {
        _loading = true;
        _vm.Load(snapshot);
        _themeCombo.SelectedIndex = _vm.SessionSettings?.Theme switch {
            "Light" => 1,
            "Dark"  => 2,
            _       => 0,
        };
        _loading = false;
    }

    public void LoadDefaults() => _vm.LoadDefaults();

    void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _vm.SessionSettings is null) return;
        _vm.SessionSettings.Theme = _themeCombo.SelectedIndex switch {
            1 => "Light",
            2 => "Dark",
            _ => string.Empty,
        };
    }

    void OnUseTabsChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // Binding handles the value; handler exists to satisfy XAML event wiring.
    }
}
