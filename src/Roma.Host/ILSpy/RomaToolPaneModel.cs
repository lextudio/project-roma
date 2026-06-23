using ICSharpCode.ILSpy.ViewModels;

using Microsoft.UI.Xaml;

namespace Roma.Host;

// Roma's concrete tool-pane view-model. ILSpy's ToolPaneModel/PaneModel describe a pane's state
// (ContentId/Title/IsVisible/IsActive/IsSelected/IsCloseable) but carry no view — upstream resolves
// the UI through a type-keyed DataTemplate. Roma instead hosts a ready-built WinUI element per pane,
// exposed here as Content and rendered by the AnchorablesSource content template
// (ContentControl Content="{Binding Content}").
internal sealed class RomaToolPaneModel : ToolPaneModel
{
    private UIElement _content;

    public RomaToolPaneModel(string contentId, string title, UIElement content, bool initiallyVisible, bool closeable)
    {
        ContentId = contentId;
        Title = title;
        _content = content;
        IsCloseable = closeable;
        IsVisible = initiallyVisible;
    }

    /// <summary>The WinUI element shown when this pane is the selected tab in its anchorable pane.
    /// Settable + observable so the host can swap it (e.g. the assembly tree rebuilt on theme change);
    /// the AnchorablePaneTemplate binds {Binding Content} and updates when this raises PropertyChanged.</summary>
    public UIElement Content
    {
        get => _content;
        set => SetProperty(ref _content, value);
    }
}
