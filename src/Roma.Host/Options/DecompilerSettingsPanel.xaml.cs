using ICSharpCode.ILSpy.Options;
using ICSharpCode.ILSpyX.Settings;
using Microsoft.UI.Xaml.Controls;

namespace Roma.Host.Options;

public sealed partial class DecompilerSettingsPanel : UserControl, IOptionPage
{
    DecompilerSettingsViewModel _vm = new();

    public DecompilerSettingsPanel()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    public string Title => _vm.Title;

    public void Load(SettingsSnapshot snapshot) => _vm.Load(snapshot);

    public void LoadDefaults() => _vm.LoadDefaults();
}
