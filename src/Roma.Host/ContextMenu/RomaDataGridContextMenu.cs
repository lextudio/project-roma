// Roma host: context-menu support for metadata DataGrid cells.
//
// ILSpy (WPF) wires this via ContextMenuProvider.Add(DataGrid), which hooks ContextMenuOpening
// and uses MEF-discovered IContextMenuEntry instances. On Uno, ContextMenuProvider.Add(DataGrid)
// invokes ContextMenuProvider.AttachDataGridContextMenu, which MainPage registers to this class.
//
// Two entries mirror what ILSpy shows in the MetadataView:
//   GoToTokenCommand  — navigate to the token in the current cell (Token columns only)
//   CopyCommand       — copy the current cell value to the clipboard

using System.Windows;
using System.Windows.Controls;

using ICSharpCode.ILSpy;
using ICSharpCode.ILSpy.Commands;  // GoToTokenCommand, CopyCommand (in GoToTokenCommand.cs)

using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Roma.Host.ContextMenu;

internal sealed class RomaDataGridContextMenu
{
    private readonly RomaTreeContextMenu _menu = new(
    [
        new GoToTokenCommand(),
        new CopyCommand(),
    ]);

    /// <summary>
    /// Hooks <paramref name="grid"/>'s RightTapped event so that right-clicking a cell
    /// shows a WinUI MenuFlyout built from the registered IContextMenuEntry instances.
    /// </summary>
    public void Attach(DataGrid grid)
    {
        grid.RightTapped += OnRightTapped;
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var grid = (DataGrid)sender;
        var originalSource = e.OriginalSource as DependencyObject;
        var context = TextViewContext.ForDataGrid(grid, originalSource);
        var flyout = _menu.Build(context);
        if (flyout is null)
            return;

        var element = (Microsoft.UI.Xaml.FrameworkElement)sender;
        flyout.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
    }
}
