using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.ILSpy.Options;
using ICSharpCode.ILSpyX.Settings;
using Microsoft.UI.Xaml.Controls;

namespace Roma.Host.Options;

public sealed partial class DisplaySettingsPanel : UserControl, IOptionPage
{
    DisplaySettingsViewModel _vm = new();
    RomaHostSettings? _hostSettings;
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
        _hostSettings = snapshot.GetSettings<RomaHostSettings>();
        _themeCombo.SelectedIndex = _vm.SessionSettings?.Theme switch {
            "Light" => 1,
            "Dark"  => 2,
            _       => 0,
        };
        BuildFontList();
        _loading = false;
    }

    // Populate the font combo with recently-used fonts floated to the top (if enabled),
    // then the rest of the system fonts. Mirrors ProjectRover's display panel.
    void BuildFontList()
    {
        var all = _vm.FontFamilies ?? Array.Empty<string>();
        var allSet = new HashSet<string>(all, StringComparer.OrdinalIgnoreCase);

        var recents = (_hostSettings?.RecentFontsEnabled ?? true)
            ? RecentFontsCache.Load().Where(allSet.Contains).ToList()
            : new List<string>();
        var recentSet = new HashSet<string>(recents, StringComparer.OrdinalIgnoreCase);

        var ordered = recents.Concat(all.Where(f => !recentSet.Contains(f))).ToArray();
        _fontCombo.ItemsSource = ordered;
    }

    void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _hostSettings is null)
            return;
        // SelectedItem two-way binding already wrote Settings.SelectedFontName; record the
        // pick as recently-used (surfaced at the top next time the dialog opens).
        if (_hostSettings.RecentFontsEnabled && _fontCombo.SelectedItem is string font && !string.IsNullOrEmpty(font))
            RecentFontsCache.Update(font);
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
