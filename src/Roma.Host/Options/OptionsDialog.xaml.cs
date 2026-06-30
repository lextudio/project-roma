using System;
using ICSharpCode.ILSpy.Options;
using Microsoft.UI.Xaml.Controls;

using SettingsService = ICSharpCode.ILSpy.SettingsService;

namespace Roma.Host.Options;

public sealed partial class OptionsDialog : ContentDialog
{
    readonly OptionsDialogViewModel _vm;
    public Action? OnSaved { get; set; }

    public OptionsDialog(SettingsService settingsService)
    {
        InitializeComponent();

        _vm = new OptionsDialogViewModel(settingsService);

        _panelList.ItemsSource = _vm.OptionPages;
        _panelList.DisplayMemberPath = nameof(OptionsItemViewModel.Title);
        _panelList.SelectedIndex = 0;
    }

    void OnPanelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_panelList.SelectedItem is OptionsItemViewModel item)
        {
            _vm.SelectedPage = item.Content;
            _contentArea.Content = item.Content;
        }
    }

    void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _vm.CommitCommand.Execute(null);
        OnSaved?.Invoke();
    }
}
