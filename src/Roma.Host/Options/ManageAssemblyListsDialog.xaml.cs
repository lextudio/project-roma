using System;
using System.Linq;

using ICSharpCode.ILSpyX;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Roma.Host.Options;

// WinUI port of ILSpy's ManageAssemblyListsDialog (ext/ilspy/ILSpy/Views/ManageAssemblyListsDialog).
// Manages assembly-list *definitions* via AssemblyListManager (New/Clone/Rename/Delete/Reset and the
// preconfigured presets). Activation (Open) is via double-click / Enter, as in ILSpy — there is no
// Open button. New/Clone/Rename prompt for the name with a flyout on the button (ILSpy uses a separate
// CreateListDialog; WinUI allows only one ContentDialog open at a time). The actual tree swap on
// activation is done by the host via the onOpen callback.
public sealed partial class ManageAssemblyListsDialog : ContentDialog
{
    readonly AssemblyListManager _manager;
    readonly Action<string> _onOpen;

    // (display name, preset key). The .NET presets resolve assemblies from the GAC and are effectively
    // Windows-only (they yield empty lists elsewhere — GetAssemblyInGac returns null off-Windows).
    // "Uno Platform" is Roma's cross-platform preset: a curated set of core Uno assemblies (see
    // RomaAssemblyListPresets), not the whole runtime.
    static (string Display, string Key)[] Presets() => new[]
    {
        (".NET 4 (WPF)", AssemblyListManager.DotNet4List),
        (".NET 3.5", AssemblyListManager.DotNet35List),
        ("ASP.NET (MVC3)", AssemblyListManager.ASPDotNetMVC3List),
        (RomaAssemblyListPresets.UnoPlatform, RomaAssemblyListPresets.UnoPlatform),
    };

    public ManageAssemblyListsDialog(AssemblyListManager manager, Action<string> onOpen)
    {
        _manager = manager;
        _onOpen = onOpen;
        InitializeComponent();

        _listView.ItemsSource = _manager.AssemblyLists;

        foreach (var preset in Presets())
        {
            var item = new MenuFlyoutItem { Text = preset.Display };
            item.Click += (_, _) => CreatePreset(preset.Key);
            _presetFlyout.Items.Add(item);
        }
    }

    // Number of preconfigured presets wired into the flyout (used by integration probes).
    internal int PresetCount => _presetFlyout.Items.Count;

    string? Selected => _listView.SelectedItem as string;

    void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool has = Selected != null;
        _cloneButton.IsEnabled = has;
        _renameButton.IsEnabled = has;
        _deleteButton.IsEnabled = has;
    }

    void OnListDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        => OpenSelected();

    // Enter activates the selected list (like ILSpy's Return trigger); Delete removes it.
    void OnListKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            OpenSelected();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Delete)
        {
            OnDeleteClick(sender, e);
            e.Handled = true;
        }
    }

    void OpenSelected()
    {
        if (Selected is { } name)
        {
            _onOpen(name);
            Hide();
        }
    }

    void OnNewClick(object sender, RoutedEventArgs e)
        => PromptName(_newButton, "Name for the new list:", "", name =>
        {
            _manager.AddListIfNotExists(_manager.CreateList(name));
            Select(name);
        });

    void OnCloneClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } source)
            return;
        PromptName(_cloneButton, "Name for the cloned list:", source, name =>
        {
            _manager.CloneList(source, name);
            Select(name);
        });
    }

    void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } source)
            return;
        PromptName(_renameButton, "New name for the list:", source, name =>
        {
            if (name == source)
                return;
            _manager.RenameList(source, name);
            Select(name);
        });
    }

    void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } name)
            _manager.DeleteList(name);
    }

    void OnResetClick(object sender, RoutedEventArgs e)
    {
        _manager.ClearAll();
        _manager.CreateDefaultAssemblyLists();
    }

    void CreatePreset(string key)
    {
        try
        {
            var list = RomaAssemblyListPresets.IsUnoPlatform(key)
                ? RomaAssemblyListPresets.CreateUnoPlatformList(_manager)
                : _manager.CreateDefaultList(key);
            _manager.AddListIfNotExists(list);
            Select(key);
        }
        catch
        {
            // Preset creation is best-effort (e.g. GAC assemblies absent off-Windows).
        }
    }

    // Prompts for a list name in a flyout anchored to the clicked button, validating non-empty and
    // uniqueness inline before invoking onAccept. Replaces ILSpy's modal CreateListDialog.
    void PromptName(FrameworkElement anchor, string header, string initial, Action<string> onAccept)
    {
        var box = new TextBox { Text = initial, MinWidth = 220 };
        var error = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Crimson),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel
        {
            Spacing = 6,
            MinWidth = 240,
            Children = { new TextBlock { Text = header }, box, error, ok },
        };
        var flyout = new Flyout { Content = panel };

        void TryAccept()
        {
            var name = box.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(name))
            {
                error.Text = "Enter a name.";
                error.Visibility = Visibility.Visible;
                return;
            }
            if (!string.Equals(name, initial, StringComparison.Ordinal) && _manager.AssemblyLists.Contains(name))
            {
                error.Text = $"A list named '{name}' already exists.";
                error.Visibility = Visibility.Visible;
                return;
            }
            flyout.Hide();
            onAccept(name);
        }

        ok.Click += (_, _) => TryAccept();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                TryAccept();
                e.Handled = true;
            }
        };

        flyout.ShowAt(anchor);
        box.Focus(FocusState.Programmatic);
        box.SelectAll();
    }

    void Select(string name)
    {
        _listView.SelectedItem = _manager.AssemblyLists.Contains(name) ? name : null;
    }
}
