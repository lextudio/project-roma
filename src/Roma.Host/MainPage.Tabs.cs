using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

using ICSharpCode.ILSpy.ViewModels;
using ICSharpCode.ILSpyX.TreeView;

using Microsoft.UI.Xaml;

namespace Roma.Host;

public sealed partial class MainPage
{
    private AvalonDock.Layout.LayoutDocumentPane? _documentPane;
    private readonly Dictionary<TabPageModel, DocTabContent> _contentByModel = new();
    private TabPageModel? _activeModel;
    private bool _syncingActiveTab;
    private bool _tabsWired;

    internal static void RegisterTabContent(TabPageModel model, DocTabContent content)
    {
        var page = _current;
        if (page is null) return;
        page._contentByModel[model] = content;
        // Adopt the active editor theme immediately. New content is constructed with the Light
        // default, so without this it would show a light editor surface under a dark theme (ILSpy
        // avoids this by binding the editor Background to a DynamicResource that follows the theme).
        content.CodeEditor.Theme = page._currentEditorTheme;
        content.Decompiler?.SetEditorTheme(page._currentEditorTheme);
        // DataTemplate expansion is deferred to Loaded; if this model is already active,
        // wire the editor fields now that the content view is available.
        if (ReferenceEquals(page._activeModel, model))
            page.ActivateTab(model);
    }

    private void ActivateTab(TabPageModel? model)
    {
        if (model is null)
            return;

        _activeModel = model;
        _decompilerTabPage = model;

        if (!_contentByModel.TryGetValue(model, out var content))
            return;

        _codeEditor = content.CodeEditor;
        _foldingManager = content.FoldingManager;
        _nodeContent = content.NodeContent;
        _nodeHost = content.NodeHost;
        _decompilerTextView = content.Decompiler;
        ApplyDisplaySettings();

        // Replay a deferred About request now that the decompiler view exists (see _pendingShowAbout).
        if (_pendingShowAbout && _decompilerTextView is not null)
            DispatcherQueue.TryEnqueue(ShowAboutPage);

        // Replay a deferred selection now that the view is wired (see _pendingRestoreNode): a selection
        // (startup restore / early click) can arrive before the DecompilerTextView exists.
        if (_pendingRestoreNode is { } pending && _decompilerTextView is not null)
        {
            _pendingRestoreNode = null;
            DispatcherQueue.TryEnqueue(() => OnTreeNodeSelected(pending));
        }
    }

    private void WireDocumentTabs()
    {
        var dock = ICSharpCode.ILSpy.App.ExportProvider
            .GetExportedValue<ICSharpCode.ILSpy.Docking.DockWorkspace>();

        if (!dock.TabPages.Any())
            dock.AddTabPage();

        if (!_tabsWired)
        {
            _tabsWired = true;
            DockManager.LayoutItemTemplate = (Microsoft.UI.Xaml.DataTemplate)Resources["TabPageTemplate"];
            DockManager.DocumentsSource = dock.TabPages;

            // Tool panes (Assembly Browser / Analyze / Search) via the ILSpy PaneModel system:
            // LayoutUpdateStrategy positions each pane (BeforeInsertAnchorable, by ContentId),
            // AnchorableContentTemplate renders the model's Content, and AnchorablesSource binds
            // DockWorkspace.ToolPanes (already seeded into the export provider in the ctor).
            DockManager.LayoutUpdateStrategy = dock;
            DockManager.AnchorableContentTemplate = (Microsoft.UI.Xaml.DataTemplate)Resources["AnchorablePaneTemplate"];
            DockManager.AnchorablesSource = dock.ToolPanes;

            DockManager.ActiveContentChanged += (_, _) =>
            {
                if (_syncingActiveTab)
                    return;
                if (DockManager.ActiveContent is not TabPageModel model)
                    return;
                _syncingActiveTab = true;
                ActivateTab(model);
                dock.ActiveTabPage = model;
                _syncingActiveTab = false;
            };
        }

        var initial = dock.ActiveTabPage ?? dock.TabPages.FirstOrDefault();
        if (initial is not null)
        {
            ActivateTab(initial);
            DockManager.ActiveContent = initial;
        }
    }

    internal void OpenNodeInNewTab(SharpTreeNode node)
    {
        var dock = ICSharpCode.ILSpy.App.ExportProvider
            .GetExportedValue<ICSharpCode.ILSpy.Docking.DockWorkspace>();
        var model = dock.AddTabPage();
        ActivateTab(model);
        DockManager.ActiveContent = model;
        OnTreeNodeSelected(node);
    }
}
