using System;
using System.Collections.Generic;
using ICSharpCode.ILSpy.Options;
using ICSharpCode.ILSpyX.Settings;
using Microsoft.UI.Xaml.Controls;

using SettingsService = ICSharpCode.ILSpy.Util.SettingsService;

namespace Roma.Host.Options;

public sealed partial class OptionsDialog : ContentDialog
{
    readonly SettingsSnapshot _snapshot;
    readonly List<IOptionPage> _panels;
    public Action? OnSaved { get; set; }

    public OptionsDialog(SettingsService settingsService)
    {
        InitializeComponent();

        _snapshot = settingsService.CreateSnapshot();

        _panels =
        [
            new DecompilerSettingsPanel(),
            new DisplaySettingsPanel(),
            new MiscSettingsPanel(),
        ];

        foreach (var panel in _panels)
            panel.Load(_snapshot);

        _panelList.ItemsSource = new[] { "Decompiler", "Display", "Misc" };
        _panelList.SelectedIndex = 0;
    }

    void OnPanelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int idx = _panelList.SelectedIndex;
        if (idx >= 0 && idx < _panels.Count)
            _contentArea.Content = _panels[idx];
    }

    void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _snapshot.Save();
        OnSaved?.Invoke();
    }
}
