using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text;
using System.Threading.Tasks;
using AvalonDock.Layout;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using ICSharpCode.ILSpyX.TreeView;
using ICSharpCode.ILSpy.ViewModels;
using ICSharpCode.ILSpy.TreeNodes;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.Util;
using ICSharpCode.ILSpy;
using ICSharpCode.ILSpyX;
using LeXtudio.DevFlow.Agent.Core;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using UnoEdit.Skia.Desktop.Controls;
using Roma.Host.Analyzers;

namespace Roma.Host;

public enum RomaTheme { System, Light, Dark }

public sealed partial class MainPage : Page
{
    public DecompilerIntegrationStatus Status { get; }
    public LayoutRoot DockLayout { get; }
    public string BootstrapStatus { get; } = $"Roma  ·  ILSpy {CompatibilityInfo.UpstreamTarget} on Uno Platform";
    public string FooterStatus => $"Assembly: {Status.LoadedAssemblyName}  ·  Language: {Status.SessionLanguage}  ·  Languages: {Status.AvailableLanguageCount}";
    public string CandidateFooter => $"Theme: {Status.SessionTheme}  ·  VM: {Status.MainWindowViewModelCreated}";

    // Session 15: shared decompiler context; editor updated on tree selection.
    private readonly RomaAssemblyContext _assemblyContext;


    // Session 20: decompiled code shown in the UnoEdit TextEditor with C# syntax
    // highlighting and brace-based folding, matching ILSpy WPF's DecompilerTextView.
    private TextEditor? _codeEditor;
    private FoldingManager? _foldingManager;
    private readonly BraceFoldingStrategy _foldingStrategy = new();

    // Session 27: the real ILSpy DecompilerTextView is now linked and hosted as the code
    // surface. Type/member selection routes through its DecompileAsync (real decompiler path).
    private ICSharpCode.ILSpy.TextView.DecompilerTextView? _decompilerTextView;
    private TabPageModel? _decompilerTabPage;
    private Task? _lastDecompileTask;
    private string? _lastDecompileNode;
    private string? _lastDecompileError;

    // Session 19: live node-content display. When View() returns a DataGrid (metadata
    // tables) it's shown here; the editor is hidden while a DataGrid is visible.
    private ContentPresenter? _nodeContent;
    private FrameworkElement? _nodeHost;

    // Static reference for DevFlow invoke actions.
    private static MainPage? _current;

    // Navigation history (Back/Forward) is owned by AssemblyTreeModel.NavigationHistory; the toolbar
    // drives it via NavigateHistory and reads CanNavigateBack/CanNavigateForward (see UpdateNavButtons).

    // Last selected tree node, for re-decompile after settings change.
    private SharpTreeNode? _lastSelectedNode;

    [DevFlowAction("select-metadata-node", Description = "Select the first child of the Metadata tree node and trigger View()")]
    public static void SelectMetadataNode()
    {
        var page = _current;
        if (page is null) return;
        page.DispatcherQueue.TryEnqueue(() => page.SelectMetadataNodeImpl());
        System.Threading.Thread.Sleep(500); // let the UI thread process it before the action returns
    }

    [DevFlowAction("roma.decompiler-state", Description = "Reports the live Roma decompiler text view state.")]
    public static string GetDecompilerState()
    {
        var page = _current;
        if (page is null)
            return "MainPage is not available.";

        string result = string.Empty;
        page.DispatcherQueue.TryEnqueue(() =>
        {
            var task = page._lastDecompileTask;
            result =
                $"lastNode={page._lastDecompileNode ?? string.Empty}\n" +
                $"lastTaskStatus={task?.Status.ToString() ?? "none"}\n" +
                $"lastError={page._lastDecompileError ?? string.Empty}\n" +
                $"documentTitle={page._activeModel?.Title ?? string.Empty}\n" +
                $"selectedNodeType={page._lastSelectedNode?.GetType().FullName ?? string.Empty}\n" +
                $"selectedNodeText={page._lastSelectedNode?.Text?.ToString() ?? string.Empty}\n" +
                $"selectedNodeFullTypeName={(page._lastSelectedNode as TypeTreeNode)?.TypeDefinition.FullName ?? string.Empty}\n" +
                (page._decompilerTextView?.GetUnoDebugSnapshot() ?? "DecompilerTextView is not available.");
        });
        System.Threading.Thread.Sleep(500);
        return result;
    }

    [DevFlowAction("roma.metadata-tree-state", Description = "Reports the live Roma metadata subtree shape and icon mapping.")]
    public static string GetMetadataTreeState()
    {
        var page = _current;
        if (page is null)
            return "MainPage is not available.";

        string result = string.Empty;
        using var completed = new ManualResetEventSlim();
        page.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var root = page._assemblyContext.Root;
                root.EnsureLazyChildren();

                var sb = new StringBuilder();
                sb.AppendLine($"root={DescribeTreeNode(root)}");

                foreach (SharpTreeNode child in root.Children)
                {
                    var text = child.Text?.ToString() ?? string.Empty;
                    if (text.Contains("Metadata", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("Resources", StringComparison.OrdinalIgnoreCase))
                    {
                        AppendTreeNode(sb, child, 0, 3);
                    }
                }

                result = sb.ToString();
            }
            catch (Exception ex)
            {
                result = ex.ToString();
            }
            finally
            {
                completed.Set();
            }
        });
        completed.Wait(5000);
        return result;
    }

    private static void AppendTreeNode(StringBuilder sb, SharpTreeNode node, int depth, int maxDepth)
    {
        node.EnsureLazyChildren();
        sb.Append(' ', depth * 2);
        sb.AppendLine(DescribeTreeNode(node));

        if (depth >= maxDepth)
            return;

        foreach (SharpTreeNode child in node.Children)
            AppendTreeNode(sb, child, depth + 1, maxDepth);
    }

    private static string DescribeTreeNode(SharpTreeNode node)
    {
        var (baseIcon, overlayIcon) = RomaTreeIconProvider.GetIcons(node);
        var (closedIcon, _) = RomaTreeIconProvider.GetIcons(node, isExpanded: false);
        var (openIcon, _) = RomaTreeIconProvider.GetIcons(node, isExpanded: true);
        var baseUri = IconUri(baseIcon);
        var overlayUri = IconUri(overlayIcon);
        var closedUri = IconUri(closedIcon);
        var openUri = IconUri(openIcon);
        var ilspy = node as ILSpyTreeNode;
        return
            $"text=\"{node.Text}\" " +
            $"type={node.GetType().FullName} " +
            $"children={node.Children.Count} lazy={node.LazyLoading} " +
            $"icon={baseUri ?? "<null>"} closed={closedUri ?? "<null>"} open={openUri ?? "<null>"} overlay={overlayUri ?? "<null>"} " +
            $"publicApi={ilspy?.IsPublicAPI.ToString() ?? "<n/a>"} autoLoaded={ilspy?.IsAutoLoaded.ToString() ?? "<n/a>"}";
    }

    private static string IconUri(Microsoft.UI.Xaml.Media.ImageSource? icon) =>
        (icon as SvgImageSource)?.UriSource?.ToString() ?? "<null>";

    [DevFlowAction("roma.dock-autohide-state", Description = "Reports DockingManager auto-hide strip bounds and resolved VS2013 brushes.")]
    public static string GetDockAutoHideState()
    {
        var page = _current;
        if (page is null)
            return "MainPage is not available.";

        string result = string.Empty;
        page.DispatcherQueue.TryEnqueue(() =>
        {
            var sb = new StringBuilder();
            sb.AppendLine($"dock.background={BrushToString(page.DockManager.Background)}");
            sb.AppendLine($"dock.actualTheme={page.DockManager.ActualTheme}");
            foreach (var key in new[]
            {
                "UnoDock_VS2013_Background",
                "UnoDock_VS2013_TabBarBackground",
                "UnoDock_VS2013_ContentBackground",
                "UnoDock_VS2013_ResizerBackground",
                "UnoDock_VS2013_TabBarBorderBrush",
                "UnoDock_VS2013_TabText",
            })
            {
                sb.AppendLine($"{key}={BrushToString(FindResourceBrush(page.DockManager, key))}");
            }

            foreach (var side in FindVisualChildren<AvalonDock.Controls.LayoutAnchorSideControl>(page.DockManager))
            {
                var transform = side.TransformToVisual(page);
                var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                var tabBarBrush = FindResourceBrush(side, "UnoDock_VS2013_TabBarBackground");
                sb.AppendLine(
                    $"side[{side.GetHashCode():x}] bounds={point.X:0},{point.Y:0},{side.ActualWidth:0}x{side.ActualHeight:0} " +
                    $"background={BrushToString(side.Background)} " +
                    $"tabBar={BrushToString(tabBarBrush)}");
            }

            result = sb.ToString();
        });
        System.Threading.Thread.Sleep(500);
        return result;
    }

    private static Microsoft.UI.Xaml.Media.Brush? FindResourceBrush(FrameworkElement start, string key)
    {
        if (start.Resources.TryGetValue(key, out var local) && local is Microsoft.UI.Xaml.Media.Brush localBrush)
            return localBrush;

        DependencyObject? current = start;
        while (current != null)
        {
            if (current is FrameworkElement fe &&
                fe.Resources.TryGetValue(key, out var scoped) &&
                scoped is Microsoft.UI.Xaml.Media.Brush scopedBrush)
                return scopedBrush;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static string BrushToString(Microsoft.UI.Xaml.Media.Brush? brush)
    {
        return brush switch
        {
            Microsoft.UI.Xaml.Media.SolidColorBrush solid => solid.Color.ToString(),
            null => "<null>",
            _ => brush.GetType().Name,
        };
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    [DevFlowAction("roma.select-ilspy-app", Description = "Selects ICSharpCode.ILSpy.App and triggers the decompiler view.")]
    public static void SelectIlSpyAppNode()
    {
        var page = _current;
        if (page is null) return;
        page.DispatcherQueue.TryEnqueue(() =>
        {
            var target = page.FindTypeNode("ICSharpCode.ILSpy.App")
                ?? (page._assemblyContext.PrimaryAssemblyNode is { } asm
                    ? page.FindNodeByPath(asm, "ICSharpCode.ILSpy", "App")
                    : null);
            if (target is not null)
                page.OnTreeNodeSelected(target);
        });
        System.Threading.Thread.Sleep(1000);
    }

    [DevFlowAction("roma.select-resources-file", Description = "Selects the first .resources node and reports the rendered view.")]
    public static string SelectResourcesFileNode()
    {
        var page = _current;
        if (page is null)
            return "MainPage is not available.";

        string result = string.Empty;
        using var completed = new ManualResetEventSlim();
        page.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                page._assemblyContext.Root.EnsureLazyChildren();
                var target = page.FindNode(page._assemblyContext.Root, n =>
                    n.Text?.ToString()?.EndsWith(".resources", StringComparison.OrdinalIgnoreCase) == true);
                if (target is null)
                {
                    result = ".resources node not found.";
                    return;
                }

                page.OnTreeNodeSelected(target);
                var panel = page._nodeContent?.Content as Microsoft.UI.Xaml.Controls.Grid;
                var saveButton = panel?.Children.OfType<Microsoft.UI.Xaml.Controls.Button>().FirstOrDefault();
                var dataGrid = panel?.Children.OfType<System.Windows.Controls.DataGrid>().FirstOrDefault();
                result =
                    $"selected={target.Text}\n" +
                    $"nodeType={target.GetType().FullName}\n" +
                    $"documentTitle={page._activeModel?.Title}\n" +
                    $"contentType={page._nodeContent?.Content?.GetType().FullName ?? "<null>"}\n" +
                    $"hasSaveButton={saveButton is not null}\n" +
                    $"hasGrid={dataGrid is not null}\n" +
                    $"gridRows={dataGrid?.Items.Count ?? 0}";
            }
            finally
            {
                completed.Set();
            }
        });
        completed.Wait(5000);
        return result;
    }

    [DevFlowAction("roma.select-resource", Description = "Selects a resource node by suffix and reports the rendered surface.")]
    public static string SelectResourceNode(string suffix)
    {
        var page = _current;
        if (page is null)
            return "MainPage is not available.";

        string result = string.Empty;
        using var completed = new ManualResetEventSlim();
        page.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                page._assemblyContext.Root.EnsureLazyChildren();
                var target = page.FindNode(page._assemblyContext.Root, n =>
                    n.Text?.ToString()?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true);
                if (target is null)
                {
                    result = $"Resource ending with '{suffix}' not found.";
                    return;
                }

                page.OnTreeNodeSelected(target);
                result =
                    $"selected={target.Text}\n" +
                    $"nodeType={target.GetType().FullName}\n" +
                    $"documentTitle={page._activeModel?.Title}\n" +
                    $"codeEditorVisible={page._codeEditor?.Visibility}\n" +
                    $"nodeHostVisible={page._nodeHost?.Visibility}\n" +
                    $"decompilerVisible={page._decompilerTextView?.Visibility}\n" +
                    $"textLength={page._codeEditor?.Text?.Length ?? 0}";
            }
            finally
            {
                completed.Set();
            }
        });
        completed.Wait(5000);
        return result;
    }

    private SharpTreeNode? FindNode(SharpTreeNode root, Func<SharpTreeNode, bool> predicate)
    {
        if (predicate(root))
            return root;

        root.EnsureLazyChildren();
        foreach (SharpTreeNode child in root.Children)
        {
            var match = FindNode(child, predicate);
            if (match is not null)
                return match;
        }

        return null;
    }

    private TypeTreeNode? FindTypeNode(string fullTypeName)
    {
        if (_assemblyContext.PrimaryAssemblyNode is not { } assemblyNode)
            return null;

        try
        {
            var type = _assemblyContext.Decompiler.TypeSystem.FindType(new FullTypeName(fullTypeName)).GetDefinition();
            return assemblyNode.FindTypeNode(type);
        }
        catch
        {
            return null;
        }
    }

    private TypeTreeNode? FindTypeNode(SharpTreeNode root, string fullTypeName)
    {
        root.EnsureLazyChildren();
        foreach (SharpTreeNode child in root.Children)
        {
            if (child is TypeTreeNode typeNode &&
                string.Equals(typeNode.TypeDefinition.FullName, fullTypeName, StringComparison.Ordinal))
                return typeNode;

            var match = FindTypeNode(child, fullTypeName);
            if (match is not null)
                return match;
        }

        return null;
    }

    private SharpTreeNode? FindNodeByPath(SharpTreeNode root, params string[] path)
    {
        SharpTreeNode current = root;
        foreach (var part in path)
        {
            current.EnsureLazyChildren();
            SharpTreeNode? next = null;
            foreach (SharpTreeNode child in current.Children)
            {
                if (string.Equals(child.Text?.ToString(), part, StringComparison.Ordinal))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
                return null;
            current = next;
        }

        return current;
    }

    // Set when a selection arrives before the document tab's DecompilerTextView has been realized
    // (MainPage.Loaded races the DocTabContent's own Loaded — the same race _pendingShowAbout handles).
    // OnTreeNodeSelected stashes the node here and ActivateTab replays it once the view is wired.
    private SharpTreeNode? _pendingRestoreNode;

    private void SelectMetadataNodeImpl()
    {
        // Under the AssemblyListTreeNode root, the assembly node holds Metadata as a child;
        // Metadata's children are the table nodes.
        if (_assemblyContext.PrimaryAssemblyNode is not { } root)
            return;
        root.EnsureLazyChildren();
        SharpTreeNode? metadataNode = null;
        foreach (SharpTreeNode child in root.Children)
        {
            if (child.Text?.ToString() == "Metadata") { metadataNode = child; break; }
        }
        if (metadataNode is null) return;
        metadataNode.EnsureLazyChildren();
        if (metadataNode.Children.Count == 0) return;
        OnTreeNodeSelected(metadataNode.Children[0]);
    }

    public MainPage()
    {
        _current = this;
        Status = CompatibilityInfo.GetDecompilerIntegrationStatus();
        var (_, context) = RomaAssemblyTree.BuildILSpyTree();
        _assemblyContext = context;
        RomaAnalyzerContext.Initialize(_assemblyContext.LanguageService, _assemblyContext.AssemblyList);
        _analyzerViewModel = new RomaAnalyzerViewModel();
        // The real [Shared] AssemblyTreeModel is built by the export provider that
        // BuildILSpyTree installed as App.ExportProvider — reuse that single instance.
        _assemblyTreeModel = ICSharpCode.ILSpy.App.ExportProvider
            .GetExportedValue<ICSharpCode.ILSpy.AssemblyTree.AssemblyTreeModel>();
        // The model owns selection/navigation/history; the WinUI surface reacts to it. When its
        // selection changes (tree click, reference jump, Back/Forward, startup restore), render the
        // newly-selected node and refresh the nav buttons. SelectedItem itself is only observable via
        // SelectedItems here (SelectedItem's PropertyChanged is gated behind CROSS_PLATFORM upstream).
        _assemblyTreeModel.PropertyChanged += OnAssemblyTreeModelPropertyChanged;
        // Assembly-tree context menu. Unlike the former hand-ordered tuple list, headers, icons,
        // ordering, category separators and submenu nesting are all derived from each entry's
        // [ExportContextMenuEntry] metadata — exactly as ILSpy's ContextMenuProvider does — so this
        // is just the set of entry instances (constructed with their dependencies). Adding an
        // upstream command here is a one-liner once its source is in the Roma build.
        var dockWorkspace = ICSharpCode.ILSpy.App.ExportProvider
            .GetExportedValue<ICSharpCode.ILSpy.Docking.DockWorkspace>();
        var languageService = ICSharpCode.ILSpy.App.ExportProvider
            .GetExportedValue<ICSharpCode.ILSpy.LanguageService>();
        IContextMenuEntry[] entries =
        [
            new Roma.Host.Analyzers.RomaAnalyzeContextMenuCommand(_analyzerViewModel),
            new ICSharpCode.ILSpy.CopyFullyQualifiedNameContextMenuEntry(),
            new ICSharpCode.ILSpy.TreeNodes.RemoveAssembly(),
            new ICSharpCode.ILSpy.TreeNodes.ReloadAssembly(_assemblyTreeModel),
            new ICSharpCode.ILSpy.TreeNodes.LoadDependencies(_assemblyTreeModel),
            new ICSharpCode.ILSpy.TreeNodes.AddToMainList(_assemblyTreeModel),
            new Roma.Host.ContextMenu.RomaDecompileInNewTabEntry(node => OpenNodeInNewTab(node)),
            new Roma.Host.ContextMenu.RomaScopeSearchToAssemblyEntry(assemblyName =>
            {
                _searchPane?.ScopeToAssembly(assemblyName);
                dockWorkspace.ShowToolPane("searchPane");
            }),
            new ICSharpCode.ILSpy.TextView.SaveCodeContextMenuEntry(languageService, dockWorkspace),
            new ICSharpCode.ILSpy.GeneratePdbContextMenuEntry(languageService, dockWorkspace),
            new ICSharpCode.ILSpy.TextView.CreateDiagramContextMenuEntry(dockWorkspace),
            new ICSharpCode.ILSpy.SelectPdbContextMenuEntry(_assemblyTreeModel),
            new ICSharpCode.ILSpy.TreeNodes.OpenContainingFolder(),
            new ICSharpCode.ILSpy.TreeNodes.OpenCmdHere(),
            new ICSharpCode.ILSpy.SearchMsdnContextMenuEntry(),
        ];
        _treeContextMenu = new Roma.Host.ContextMenu.RomaTreeContextMenu(
            entries,
            afterExecute: entry =>
            {
                if (entry is Roma.Host.Analyzers.RomaAnalyzeContextMenuCommand)
                    dockWorkspace.ShowToolPane("analyzerPane");
            });
        // Select the icon theme before the assembly tree is built so its glyphs use the correct
        // (light/dark) SVG variant from the start (matches the persisted theme).
        ICSharpCode.ILSpy.Images.IsDark = IsDarkTheme(_assemblyContext.SettingsService.SessionSettings.Theme);
        // ThemeManager.IsDarkTheme is set by ThemeManager.Theme (applied in InitializeThemingAndLocalization).
        // Build the tool-pane view-models (and seed the export provider) before the layout/tab wiring
        // reads DockWorkspace.ToolPanes, which caches on first access.
        CreateToolPanes();
        DockLayout = CreateDockLayout();
        InitializeComponent();
        // Restore the persisted dock layout (tool-pane visibility/sizes) over the freshly built
        // default — before any document tabs are wired, so document panes are re-acquired clean.
        RestoreDockLayout();
        // Wire the multi-tab surface before the DockingManager realizes its pane controls (on
        // Loaded), so LayoutItemTemplate is in place when LayoutDocumentPaneControl is created and
        // its SelectedContentTemplate picks it up. Layout is already bound (x:Bind) at this point.
        WireDocumentTabs();
        InitializeThemingAndLocalization();
        InitializeToolbar();
        // Reference jumps (clicking a type/member link in the decompiled text) are handled by the real
        // AssemblyTreeModel, which subscribes to NavigateToReferenceEventArgs in its ctor and routes
        // through JumpToReferenceAsync → SelectNode → our SelectedItems observer. No Roma handler needed.
        Microsoft.UI.Xaml.RoutedEventHandler? onLoaded = null;
        onLoaded = (_, _) =>
        {
            Loaded -= onLoaded;
            // At startup, XamlRoot can be unavailable during InitializeComponent/constructor time.
            // Reapply the persisted theme once loaded so the window root, main menu, and toolbar
            // all resolve ThemeResource brushes from the selected light/dark dictionary.
            ReapplyPersistedTheme();
            // Wire the multi-tab document surface once the DockingManager's layout is realized.
            WireDocumentTabs();
            // ILSpy parity: with no assemblies loaded (first run after the user cleared the list),
            // show the About page in the document area instead of a blank tab. Otherwise restore the
            // last-viewed tree node (SessionSettings.ActiveTreeViewPath) so startup returns to the type
            // or member the user was looking at, exactly like ILSpy's NavigateOnLaunch.
            if (!_assemblyContext.AssemblyList.GetAssemblies().Any())
            {
                ShowAboutPage();
            }
            else
            {
                // Restore the last-viewed node via the real model: it re-opens the auto-loaded assembly
                // (ActiveAutoLoadedAssembly) and navigates to ActiveTreeViewPath, exactly like ILSpy's
                // NavigateOnLaunch. The resulting SelectNode raises SelectedItems → our observer renders.
                _ = _assemblyTreeModel?.RestoreSessionForHostAsync();
            }
            // Auto update check (no-ops unless enabled and stale); shows the top banner if newer.
            _ = RunStartupUpdateCheckAsync();
            // Set the initial Back/Forward enabled state (both empty → disabled + dimmed icons)
            // now that the named toolbar buttons exist.
            UpdateNavButtons();
        };
        Loaded += onLoaded;
    }

    private void InitializeToolbar()
    {
        // Session 21: bind assembly list combo to the real AssemblyListManager.
        _assemblyListCombo.ItemsSource = _assemblyContext.AssemblyListManager.AssemblyLists;
        // Select the active list without triggering a reload (it is already the loaded list — the
        // ReloadActiveAssemblyList guard short-circuits when the name matches the current list).
        var activeName = _assemblyContext.AssemblyList.ListName;
        _assemblyListCombo.SelectedItem =
            _assemblyContext.AssemblyListManager.AssemblyLists.Contains(activeName) ? activeName : null;

        // Session 21: bind language combo to the real LanguageService.
        _languageCombo.ItemsSource = _assemblyContext.LanguageService.AllLanguages;
        _languageCombo.SelectedItem = _assemblyContext.LanguageService.Language;

        // Sync language version visibility for the initial language.
        UpdateLanguageVersionCombo(_assemblyContext.LanguageService.Language);

        // Sync API visibility toggles from the persisted settings.
        SyncApiVisToggles(_assemblyContext.SettingsService.SessionSettings.LanguageSettings.ShowApiLevel);
    }

    // ── Menu: File ──────────────────────────────────────────────
    private void OnMenuOpen(object sender, RoutedEventArgs e) => _ = OpenAssemblyAsync();
    private void OnMenuOpenFromGac(object sender, RoutedEventArgs e) => Dbg("Menu: File → Open from GAC (not yet implemented)");
    private void OnMenuManageAssemblyLists(object sender, RoutedEventArgs e) => ShowManageAssemblyListsDialog();
    private void OnMenuReloadAll(object sender, RoutedEventArgs e) => ReloadAll();
    private void OnMenuSaveCode(object sender, RoutedEventArgs e) => Dbg("Menu: File → Save Code (not yet implemented)");
    private void OnMenuRemoveAssembliesWithErrors(object sender, RoutedEventArgs e)
    {
        var toRemove = _assemblyContext.AssemblyList.GetAssemblies()
            .Where(a => a.HasLoadError)
            .ToList();
        foreach (var asm in toRemove)
            _assemblyContext.AssemblyList.Unload(asm);
        Dbg($"Menu: File → Removed {toRemove.Count} assemblies with load errors");
    }
    private void OnMenuClearAssemblyList(object sender, RoutedEventArgs e)
    {
        _assemblyContext.AssemblyList.Clear();
        Dbg("Menu: File → Clear Assembly List");
    }
    private void OnMenuExit(object sender, RoutedEventArgs e) => Microsoft.UI.Xaml.Application.Current.Exit();

    // ── Menu: View — API visibility ─────────────────────────────
    private void OnMenuApiVisAll(object sender, RoutedEventArgs e) => SetApiVis(ApiVisibility.All);
    private void OnMenuApiVisPublicAndInternal(object sender, RoutedEventArgs e) => SetApiVis(ApiVisibility.PublicAndInternal);
    private void OnMenuApiVisPublicOnly(object sender, RoutedEventArgs e) => SetApiVis(ApiVisibility.PublicOnly);

    // Theme + UI Language menus are populated/applied in MainPage.Theming.cs.

    // ── Menu: View — Options ────────────────────────────────────
    private async void OnMenuOptions(object sender, RoutedEventArgs e)
    {
        var dialog = new Options.OptionsDialog(_assemblyContext.SettingsService)
        {
            XamlRoot = XamlRoot,
            OnSaved = RedecompileCurrentNode,
        };
        await dialog.ShowAsync();
    }

    // ── Menu: Window ────────────────────────────────────────────
    private void OnMenuShowAssembliesPane(object sender, RoutedEventArgs e) => Dbg("Menu: Window → Assemblies (not yet implemented)");
    private void OnMenuShowSearchPane(object sender, RoutedEventArgs e) => ShowSearchPane();
    private void OnMenuShowAnalyzerPane(object sender, RoutedEventArgs e) => DockWorkspace.ShowToolPane("analyzerPane");
    private void OnMenuShowDebugStepsPane(object sender, RoutedEventArgs e) => Dbg("Menu: Window → Debug Steps (not yet implemented)");
    private void OnMenuCloseAllDocuments(object sender, RoutedEventArgs e) => Dbg("Menu: Window → Close All Documents (not yet implemented)");
    private void OnMenuResetLayout(object sender, RoutedEventArgs e) => Dbg("Menu: Window → Reset Layout (not yet implemented)");
    private void OnMenuShowCodeTab(object sender, RoutedEventArgs e) => Dbg("Menu: Window → Code tab (not yet implemented)");

    // ── Menu: Help ──────────────────────────────────────────────
    private void OnMenuCheckForUpdates(object sender, RoutedEventArgs e) => OnCheckForUpdates();
    private void OnMenuAbout(object sender, RoutedEventArgs e) => ShowAboutPage();

    // ── Toolbar: file / search / navigation ────────────────────
    private void OnOpenAssembly(object sender, RoutedEventArgs e) => _ = OpenAssemblyAsync();
    private void OnReloadAll(object sender, RoutedEventArgs e) => ReloadAll();
    private void OnSearch(object sender, RoutedEventArgs e) => ShowSearchPane();

    // The shared DockWorkspace (ILSpy PaneModel system) resolved from the export provider.
    private ICSharpCode.ILSpy.Docking.DockWorkspace DockWorkspace
        => ICSharpCode.ILSpy.App.ExportProvider.GetExportedValue<ICSharpCode.ILSpy.Docking.DockWorkspace>();

    private void ShowSearchPane()
    {
        DockWorkspace.ShowToolPane("searchPane");
        _searchPane?.FocusSearchBox();
    }

    private void OnSortAssemblyList(object sender, RoutedEventArgs e)
    {
        var list = _assemblyContext.AssemblyList;
        var assemblies = list.GetAssemblies().OrderBy(a => a.ShortName, StringComparer.OrdinalIgnoreCase).ToList();
        list.Clear();
        foreach (var asm in assemblies)
        {
            list.UseDebugSymbols = true;
            list.OpenAssembly(asm.FileName);
        }
        Dbg("Assembly list sorted by name");
    }

    private void OnCollapseAll(object sender, RoutedEventArgs e)
    {
        CollapseNode(_assemblyContext.Root);
        Dbg("All tree nodes collapsed");
    }

    private static void CollapseNode(SharpTreeNode node)
    {
        node.IsExpanded = false;
        foreach (var child in node.Children)
            CollapseNode(child);
    }

    // Back/Forward drive the model's NavigationHistory. NavigateHistory restores the target
    // NavigationState (re-selecting its tree nodes), which raises SelectedItems → our observer
    // re-renders. UpdateNavButtons then tracks CanNavigateBack/Forward.
    private void OnNavigateBack(object sender, RoutedEventArgs e)
    {
        if (_assemblyTreeModel?.CanNavigateBack != true) return;
        _assemblyTreeModel.NavigateHistory(forward: false);
        UpdateNavButtons();
    }

    private void OnNavigateForward(object sender, RoutedEventArgs e)
    {
        if (_assemblyTreeModel?.CanNavigateForward != true) return;
        _assemblyTreeModel.NavigateHistory(forward: true);
        UpdateNavButtons();
    }

    private void UpdateNavButtons()
    {
        bool canBack = _assemblyTreeModel?.CanNavigateBack ?? false;
        bool canForward = _assemblyTreeModel?.CanNavigateForward ?? false;

        // Set IsEnabled and swap the icon to a grayscale variant when disabled. A disabled Button does
        // not grey an <Image> child — the colored SVG (blue circle) only fades with opacity, which on
        // a light theme still reads as "active". Swapping the Source to the grey SVG makes the disabled
        // state unmistakable.
        if (_navBackBtn is not null) _navBackBtn.IsEnabled = canBack;
        if (_navForwardBtn is not null) _navForwardBtn.IsEnabled = canForward;
        SetNavIcon(_navBackIcon, "back", canBack);
        SetNavIcon(_navForwardIcon, "forward", canForward);
    }

    private static void SetNavIcon(Microsoft.UI.Xaml.Controls.Image? icon, string baseName, bool enabled)
    {
        if (icon is null) return;
        // Enabled → the shipped colored SVG. Disabled → a grayscale variant synthesized on the fly
        // (no -disabled.svg asset): recolor the action-blue fill to grey, mirroring Images.EnsureDark-
        // Variant (Uno's SvgImageSource can't load data:/in-memory streams, so the recolored SVG is
        // written to LocalFolder and loaded via ms-appdata). Falls back to the colored icon on failure.
        var colorUri = ILSpyIconHelper.GetUri($"{baseName}.svg");
        var uri = enabled ? colorUri : (EnsureDisabledNavVariant(baseName) ?? colorUri);
        icon.Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(uri);
    }

    private const string DisabledNavIconFolder = "roma-nav-disabled-icons";

    // Recolors the action-blue (#00539C) fill of a nav SVG to grey (#A6A6A6) and caches it under
    // LocalFolder, returning an ms-appdata URI. Returns null on failure (caller falls back to color).
    private static Uri? EnsureDisabledNavVariant(string baseName)
    {
        try
        {
            var localRoot = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            var dir = System.IO.Path.Combine(localRoot, DisabledNavIconFolder);
            var outPath = System.IO.Path.Combine(dir, $"{baseName}.svg");

            if (!System.IO.File.Exists(outPath))
            {
                var srcPath = System.IO.Path.Combine(AppContext.BaseDirectory, "ILSpyIcons", $"{baseName}.svg");
                if (!System.IO.File.Exists(srcPath))
                    return null;
                System.IO.Directory.CreateDirectory(dir);
                var grey = System.IO.File.ReadAllText(srcPath)
                    .Replace("#00539C", "#A6A6A6", StringComparison.OrdinalIgnoreCase);
                System.IO.File.WriteAllText(outPath, grey);
            }

            return new Uri($"ms-appdata:///local/{DisabledNavIconFolder}/{baseName}.svg");
        }
        catch
        {
            return null;
        }
    }

    // ── Toolbar: assembly list ──────────────────────────────────
    private void OnAssemblyListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_assemblyListCombo.SelectedItem is string listName)
            ReloadActiveAssemblyList(listName);
    }

    private void OnManageAssemblyLists(object sender, RoutedEventArgs e) => ShowManageAssemblyListsDialog();

    // ── Toolbar: API visibility ─────────────────────────────────
    private void OnApiVisPublicOnly(object sender, RoutedEventArgs e) => SetApiVis(ApiVisibility.PublicOnly);
    private void OnApiVisPublicAndInternal(object sender, RoutedEventArgs e) => SetApiVis(ApiVisibility.PublicAndInternal);
    private void OnApiVisAll(object sender, RoutedEventArgs e) => SetApiVis(ApiVisibility.All);

    private void SetApiVis(ApiVisibility vis)
    {
        // Sync toolbar toggles (mutually exclusive).
        _apiVisPublicOnly.IsChecked = vis == ApiVisibility.PublicOnly;
        _apiVisPublicAndInternal.IsChecked = vis == ApiVisibility.PublicAndInternal;
        _apiVisAll.IsChecked = vis == ApiVisibility.All;

        // Sync menu items.
        _menuShowPublicOnly.IsChecked = vis == ApiVisibility.PublicOnly;
        _menuShowInternal.IsChecked = vis == ApiVisibility.PublicAndInternal;
        _menuShowAllMembers.IsChecked = vis == ApiVisibility.All;

        // Session 21: write through to the real LanguageSettings.
        _assemblyContext.SettingsService.SessionSettings.LanguageSettings.ShowApiLevel = vis;

        Dbg($"ApiVis → {vis}");

        // Re-decompile if a code node is currently shown.
        RedecompileCurrentNode();
    }

    private void SyncApiVisToggles(ApiVisibility vis)
    {
        _apiVisPublicOnly.IsChecked = vis == ApiVisibility.PublicOnly;
        _apiVisPublicAndInternal.IsChecked = vis == ApiVisibility.PublicAndInternal;
        _apiVisAll.IsChecked = vis == ApiVisibility.All;
        _menuShowPublicOnly.IsChecked = vis == ApiVisibility.PublicOnly;
        _menuShowInternal.IsChecked = vis == ApiVisibility.PublicAndInternal;
        _menuShowAllMembers.IsChecked = vis == ApiVisibility.All;
    }

    // ── Toolbar: language ───────────────────────────────────────
    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_languageCombo.SelectedItem is not Language lang) return;

        // Session 21: write through to the real LanguageService.
        _assemblyContext.LanguageService.Language = lang;

        UpdateLanguageVersionCombo(lang);
        Dbg($"Toolbar: Language → {lang.Name}");
        RedecompileCurrentNode();
    }

    private void UpdateLanguageVersionCombo(Language? lang)
    {
        if (lang is null || !lang.HasLanguageVersions)
        {
            _languageVersionCombo.Visibility = Visibility.Collapsed;
            return;
        }

        _languageVersionCombo.ItemsSource = lang.LanguageVersions;
        _languageVersionCombo.SelectedItem = _assemblyContext.LanguageService.LanguageVersion
            ?? lang.LanguageVersions.FirstOrDefault();
        _languageVersionCombo.Visibility = Visibility.Visible;
    }

    private void OnLanguageVersionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_languageVersionCombo.SelectedItem is LanguageVersion ver)
        {
            _assemblyContext.LanguageService.LanguageVersion = ver;
            Dbg($"Toolbar: LanguageVersion → {ver.DisplayName}");
            RedecompileCurrentNode();
        }
    }

    // ── Open Assembly (Microsoft.Win32.OpenFileDialog shim → WinUI FileOpenPicker) ──
    private async Task OpenAssemblyAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = ".NET assemblies|*.dll;*.exe;*.winmd;*.wasm|Packages|*.nupkg|Debug symbols|*.pdb|All files|*.*",
            Multiselect = true,
        };

        bool? result;
        try
        {
            result = await dlg.ShowDialogAsync();
        }
        catch (Exception ex)
        {
            Dbg($"OpenAssemblyAsync: ShowDialogAsync threw {ex.GetType().Name}: {ex.Message}");
            return;
        }
        if (result != true)
            return;

        foreach (var path in dlg.FileNames)
        {
            Dbg($"Open: {path}");
            _assemblyContext.AssemblyList.UseDebugSymbols = true;
            var loaded = _assemblyContext.AssemblyList.OpenAssembly(path);
            Dbg($"Opened: {loaded.ShortName}");
        }
    }

    // ── Reload ──────────────────────────────────────────────────
    private void ReloadAll()
    {
        foreach (var asm in _assemblyContext.AssemblyList.GetAssemblies())
        {
            // Re-open the assembly from its original file path to pick up any disk changes.
            var path = asm.FileName;
            if (!string.IsNullOrEmpty(path))
            {
                _assemblyContext.AssemblyList.UseDebugSymbols = true;
                _assemblyContext.AssemblyList.OpenAssembly(path);
                Dbg($"Reload: {asm.ShortName}");
            }
        }
    }

    // ── Re-decompile current node after settings change ─────────
    private void RedecompileCurrentNode()
    {
        ApplyDisplaySettings();
        if (_lastSelectedNode is not null)
            OnTreeNodeSelected(_lastSelectedNode, recordHistory: false);
    }

    private void ApplyDisplaySettings()
    {
        if (_codeEditor is null) return;
        var ds = _assemblyContext.SettingsService.DisplaySettings;
        _codeEditor.ShowLineNumbers = ds.ShowLineNumbers;
        _codeEditor.WordWrap = ds.EnableWordWrap;
        _codeEditor.EditorFontSize = ds.SelectedFontSize > 0 ? ds.SelectedFontSize : 12.0;
        if (!string.IsNullOrWhiteSpace(ds.SelectedFontName))
            _codeEditor.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(ds.SelectedFontName);
        if (ds.FoldBraces && _foldingManager is not null)
            _foldingStrategy.UpdateFoldings(_foldingManager, _codeEditor.Document);
        else if (!ds.FoldBraces && _foldingManager is not null)
            _foldingManager.Clear();
    }

    private SearchPane? _searchPane;
    private RomaToolPaneModel? _browserPaneModel;
    private RomaAnalyzerViewModel _analyzerViewModel = null!;
    private Roma.Host.ContextMenu.RomaTreeContextMenu? _treeContextMenu;
    private ICSharpCode.ILSpy.Controls.TreeView.SharpTreeView? _assemblyTree;
    private ICSharpCode.ILSpy.AssemblyTree.AssemblyTreeModel? _assemblyTreeModel;

    [DevFlowAction("activate-tool-pane", Description = "Activate the Assembly Browser tool pane (sets IsActive=true, as a tab click does)")]
    public static void ActivateToolPane()
    {
        var page = _current;
        if (page is null) return;
        page.DispatcherQueue.TryEnqueue(() => page.DockWorkspace.ShowToolPane("assemblyListPane"));
        System.Threading.Thread.Sleep(400);
    }

    // Target-only skeleton for the ILSpy PaneModel system: empty, named anchorable panes that
    // DockWorkspace.BeforeInsertAnchorable routes tool panes into by ContentId (assemblyListPane →
    // left, searchPane → top, analyzerPane → bottom). The anchorables themselves are materialized by
    // DockManager.AnchorablesSource = DockWorkspace.ToolPanes (wired in WireDocumentTabs); they are
    // NOT added here. Empty top/bottom panes survive CollectGarbage because the hidden search/analyze
    // panes reference them as PreviousContainer.
    private LayoutRoot CreateDockLayout()
    {
        var browserPane = new LayoutAnchorablePane
        {
            Name = ICSharpCode.ILSpy.Docking.DockWorkspace.AssemblyListPaneName,
            DockWidth = new GridLength(300),
        };
        var topPane = new LayoutAnchorablePane
        {
            Name = ICSharpCode.ILSpy.Docking.DockWorkspace.ToolPaneTopName,
            DockHeight = new GridLength(225),
        };
        var bottomPane = new LayoutAnchorablePane
        {
            Name = ICSharpCode.ILSpy.Docking.DockWorkspace.ToolPaneBottomName,
            DockHeight = new GridLength(225),
        };

        _documentPane = new LayoutDocumentPane();

        return new LayoutRoot
        {
            RootPanel = new AvalonDock.Layout.LayoutPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Children =
                {
                    browserPane,
                    new AvalonDock.Layout.LayoutPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Vertical,
                        Children =
                        {
                            topPane,
                            new LayoutDocumentPaneGroup(_documentPane),
                            bottomPane,
                        }
                    }
                }
            }
        };
    }

    // Builds the three tool-pane view-models (ILSpy PaneModel system) and seeds the export provider
    // so DockWorkspace.ToolPanes returns them. Must run before WireDocumentTabs reads ToolPanes
    // (which caches). Analyzer + Search start hidden; the Assembly Browser starts visible.
    private void CreateToolPanes()
    {
        _searchPane = new SearchPane(
            _assemblyContext.AssemblyList,
            _assemblyContext.LanguageService,
            _assemblyContext.SettingsService);

        _browserPaneModel = new RomaToolPaneModel("assemblyListPane", "Assembly Browser",
            CreateAssemblyBrowserContent(), initiallyVisible: true, closeable: false);
        var analyzer = new RomaToolPaneModel("analyzerPane", "Analyze",
            new AnalyzerPane(_analyzerViewModel), initiallyVisible: false, closeable: true);
        var search = new RomaToolPaneModel("searchPane", "Search",
            _searchPane, initiallyVisible: false, closeable: true);

        if (ICSharpCode.ILSpy.App.ExportProvider is ICSharpCode.ILSpy.App.RomaExportProvider provider)
            provider.SetToolPanes(new ICSharpCode.ILSpy.ViewModels.ToolPaneModel[] { _browserPaneModel, analyzer, search });
    }

    // Restores the persisted layout (mirrors ILSpy's DockWorkspace.InitializeLayout): deserializes the
    // saved XML over the DockingManager, reconnecting each tool pane's live content by ContentId. The
    // XML stores structure only, so the LayoutSerializationCallback supplies the UIElement subtrees; the
    // deserialized tree is fresh, so the cached field references are re-pointed afterwards. When there is
    // no saved layout (Valid==false), DockLayoutSettings.Deserialize is a no-op and the code-built
    // default stays. Documents are not persisted (decompilation output is regenerated) — they're
    // cancelled in the callback and re-created by WireDocumentTabs.
    // Restores the persisted dock layout (sizes / visibility / which panes are open) over the
    // code-built skeleton. The serializer round-trips the named target panes (LayoutAnchorablePane.Name),
    // so positioning keeps working after restore. Tool-pane anchorables are reconnected to their
    // RomaToolPaneModel by ContentId (the AnchorablesSource binding in WireDocumentTabs then adopts
    // them); documents are regenerated by WireDocumentTabs. Layouts that predate the named-pane
    // skeleton are discarded (one-time reset) so they can't break BeforeInsertAnchorable positioning.
    private void RestoreDockLayout()
    {
        var dockLayout = _assemblyContext.SettingsService.SessionSettings.DockLayout;
        if (dockLayout is null || !dockLayout.Valid)
            return;

        var dock = DockWorkspace;
        var serializer = new AvalonDock.Serializer.Xml.XmlLayoutSerializer(DockManager);
        serializer.LayoutSerializationCallback += (_, e) =>
        {
            var id = e.Model.ContentId;
            var model = id is null ? null : dock.ToolPanes.FirstOrDefault(p => p.ContentId == id);
            if (model is not null)
                e.Content = model;       // reconnect the tool pane to its view-model
            else
                e.Cancel = true;          // documents / unknown panes: regenerated by WireDocumentTabs
        };

        try
        {
            dockLayout.Deserialize(serializer);
        }
        catch
        {
            DockManager.Layout = CreateDockLayout(); // corrupt layout: fall back to the skeleton
            return;
        }

        // Migration guard: discard layouts saved before the named-pane skeleton existed — positioning
        // (BeforeInsertAnchorable) depends on the toolPaneTop/Bottom panes being present.
        var layout = DockManager.Layout;
        var hasNamedPanes = layout?.Descendents().OfType<LayoutAnchorablePane>()
            .Any(p => p.Name == ICSharpCode.ILSpy.Docking.DockWorkspace.ToolPaneBottomName) == true;
        if (!hasNamedPanes)
        {
            DockManager.Layout = CreateDockLayout();
            return;
        }

        _documentPane = layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault() ?? _documentPane;
    }

    // Persists the current dock layout on window close (mirrors ILSpy MainWindow.OnClosing): a settings
    // snapshot's SessionSettings.DockLayout is serialized from the live DockingManager, then saved.
    internal static void PersistDockLayoutOnExit()
    {
        var page = _current;
        if (page is null)
            return;
        try
        {
            var snapshot = page._assemblyContext.SettingsService.CreateSnapshot();
            var sessionSettings = snapshot.GetSettings<ICSharpCode.ILSpy.SessionSettings>();
            sessionSettings.DockLayout.Serialize(new AvalonDock.Serializer.Xml.XmlLayoutSerializer(page.DockManager));
            // Persist the selected tree node (ActiveTreeViewPath + ActiveAutoLoadedAssembly) via the real
            // model: AssemblyTreeModel.ApplySessionSettings fills them from SelectedItem, exactly like
            // ILSpy's MainWindow.OnClosing. Startup then restores to the last-viewed type/member.
            ICSharpCode.ILSpy.Util.MessageBus.Send(page,
                new ICSharpCode.ILSpy.Util.ApplySessionSettingsEventArgs(sessionSettings));
            snapshot.Save();
        }
        catch
        {
            // Layout persistence is best-effort; never block app shutdown.
        }
    }

    private UIElement CreateAssemblyBrowserContent()
    {
        var template = SharpTreeViewAdapter.BuildItemTemplate();
        // Route tree clicks through the model so it records selection + history; the model's
        // SelectedItems change then drives the display via OnAssemblyTreeModelPropertyChanged. Skip when
        // the node is already the model's selection: the model's activeView.ScrollIntoView programmatically
        // re-selects the node in the WinUI tree (to highlight it), whose SelectionChanged would otherwise
        // re-enter SelectNode → RefreshDecompiledView and record a spurious history entry.
        _assemblyTree = SharpTreeViewAdapter.Build(
            _assemblyContext.Root,
            node => { if (!ReferenceEquals(_assemblyTreeModel?.SelectedItem, node)) _assemblyTreeModel?.SelectNode(node); },
            template);

        // DecompileInNewViewCommand distinguishes the main assembly tree from other tree views by
        // comparing TreeView.DataContext to the AssemblyTreeModel; set it so the command treats the
        // selection as ILSpy tree nodes (and is enabled on assembly nodes) rather than the
        // member-lookup fallback path.
        if (_assemblyTreeModel is not null)
        {
            _assemblyTree.DataContext = _assemblyTreeModel;
            // Give the model an activeView over this WinUI tree so SelectNode reveals/highlights the
            // node (reference jumps, Back/Forward, session restore). Re-set on every rebuild (list switch).
            _assemblyTreeModel.SetActiveView(new ICSharpCode.ILSpy.AssemblyTree.AssemblyListPane(_assemblyTree));
        }

        // ILSpy parity: when the last assembly is removed the document view has nothing to show, so
        // reset it to a blank "New Tab" (rather than leaving the stale decompiled text on screen).
        if (_assemblyContext.Root.Children is System.Collections.Specialized.INotifyCollectionChanged incc)
            incc.CollectionChanged += (_, e) =>
            {
                if (_assemblyContext.Root.Children.Count == 0)
                    DispatcherQueue.TryEnqueue(ResetToNewTab);
            };

        _assemblyTree.RightTapped += (sender, args) =>
        {
            var node = _lastSelectedNode;
            if (node is null) return;
            var flyout = BuildTreeContextFlyout(TextViewContext.ForTreeNode(node, _assemblyTree));
            flyout?.ShowAt(_assemblyTree, new FlyoutShowOptions { Position = args.GetPosition(_assemblyTree) });
        };

        return _assemblyTree;
    }

    private MenuFlyout? BuildTreeContextFlyout(TextViewContext ctx)
        => _treeContextMenu?.Build(ctx);

    private static readonly string _dbgLog = "/tmp/roma-debug.log";

    private static void Dbg(string msg)
    {
        try { System.IO.File.AppendAllText(_dbgLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    // Reacts to the model's selection by rendering the node in the WinUI document view. Navigation
    // history is owned by AssemblyTreeModel now (SelectNode/NavigateHistory), so `recordHistory` is
    // retained only for call-site compatibility and is ignored. Called from the model's SelectedItems
    // observer and a few direct callers (probes, theming re-select, new-tab).
    private void OnTreeNodeSelected(SharpTreeNode node, bool recordHistory = true)
    {
        Dbg($"OnTreeNodeSelected: node={node?.GetType().Name} text={node?.Text}");

        _lastSelectedNode = node;

        // The document view's DataTemplate is realized on Loaded, which can lag a selection that
        // arrives during startup (e.g. session restore). Stash and let ActivateTab replay it once the
        // decompiler/node views exist — otherwise ShowDecompiled would early-return on a null view.
        if (node is ILSpyTreeNode && (_decompilerTextView is null || _nodeContent is null))
        {
            _pendingRestoreNode = node;
            return;
        }
        _pendingRestoreNode = null;

        // Real ILSpyTreeNode → call View() and display the content.
        if (node is ILSpyTreeNode ilspyNode && _nodeContent is not null)
        {
            EnsureDecompilerTabPage();
            var tabPage = new TabPageModel(ICSharpCode.ILSpy.App.ExportProvider);
            bool handled;
            try
            {
                handled = ilspyNode.View(tabPage);
                Dbg($"  View()={handled} tabPage.Content={tabPage.Content?.GetType().Name} Title={tabPage.Title}");
            }
            catch (Exception ex)
            {
                Dbg($"  View() THREW: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return;
            }

            if (handled && tabPage.Content is UIElement element)
            {
                var nodeTitle = tabPage.Title ?? ilspyNode.Text?.ToString() ?? string.Empty;
                ShowNode(nodeTitle, element);
                return;
            }

            Dbg($"  View() returned false or no UIElement — decompiling via DecompilerTextView");
            ShowDecompiled(ilspyNode);
        }
    }

    // The model is the single source of truth for selection/navigation/history. When its selection
    // changes (tree click → SelectNode, reference jump → JumpToReference, Back/Forward → NavigateHistory,
    // or startup restore), render the new node and refresh the Back/Forward button state.
    private void OnAssemblyTreeModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ICSharpCode.ILSpy.AssemblyTree.AssemblyTreeModel.SelectedItems))
            return;
        if (_assemblyTreeModel?.SelectedItem is { } node)
            OnTreeNodeSelected(node);
        UpdateNavButtons();
    }

    // Session 27: run the real ILSpy decompiler path into the linked DecompilerTextView.
    private void ShowDecompiled(ILSpyTreeNode node)
    {
        if (_decompilerTextView is null)
            return;

        var nodeTitle = node.Text?.ToString() ?? string.Empty;
        if (_activeModel is not null)
            _activeModel.Title = nodeTitle;

        try
        {
            var tabPage = EnsureDecompilerTabPage();
            var options = tabPage.CreateDecompilationOptions();
            var language = _assemblyContext.LanguageService.Language;
            _lastDecompileNode = node.Text?.ToString() ?? node.GetType().Name;
            _lastDecompileError = null;
            _lastDecompileTask = _decompilerTextView.DecompileAsync(language, new[] { node }, source: null, options);
            _lastDecompileTask.ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    _lastDecompileError = task.Exception?.GetBaseException().ToString();
                    Dbg($"  DecompileAsync FAULTED: {_lastDecompileError}");
                }
                else if (task.IsCanceled)
                {
                    _lastDecompileError = "Canceled";
                    Dbg("  DecompileAsync CANCELED");
                }
                else
                {
                    Dbg("  DecompileAsync completed");
                }
            });
        }
        catch (Exception ex)
        {
            Dbg($"  DecompileAsync THREW: {ex.GetType().Name}: {ex.Message}");
        }

        _decompilerTextView.Visibility = Visibility.Visible;
        if (_codeEditor is not null)
            _codeEditor.Visibility = Visibility.Collapsed;
        if (_nodeHost is not null)
            _nodeHost.Visibility = Visibility.Collapsed;
    }


    private TabPageModel EnsureDecompilerTabPage()
    {
        if (_decompilerTabPage is not null)
            return _decompilerTabPage;

        var dockWorkspace = ICSharpCode.ILSpy.App.ExportProvider.GetExportedValue<ICSharpCode.ILSpy.Docking.DockWorkspace>();
        var model = dockWorkspace.TabPages.FirstOrDefault() ?? dockWorkspace.AddTabPage();
        ActivateTab(model);
        return _decompilerTabPage!;
    }

    // Clears the document view back to an empty "New Tab" — used when the last assembly is removed,
    // matching ILSpy (which leaves an empty decompiler tab rather than stale content).
    private void ResetToNewTab()
    {
        _lastSelectedNode = null;
        if (_activeModel is not null)
            _activeModel.Title = ICSharpCode.ILSpy.Properties.Resources.NewTab;

        if (_decompilerTextView is not null)
        {
            _decompilerTextView.ShowText(new ICSharpCode.ILSpy.TextView.AvalonEditTextOutput());
            _decompilerTextView.Visibility = Visibility.Visible;
        }
        if (_codeEditor is not null)
            _codeEditor.Visibility = Visibility.Collapsed;
        if (_nodeHost is not null)
            _nodeHost.Visibility = Visibility.Collapsed;
    }

    private void ShowNode(string title, UIElement element)
    {
        if (_activeModel is not null)
            _activeModel.Title = title;
        if (_nodeContent is not null)
        {
            // Clear first to sever any active bindings on the old DataGrid before the
            // backing PEReader can be disposed, which would otherwise cause ObjectDisposedException
            // when the binding engine re-evaluates properties like Offset on stale ModuleEntry objects.
            _nodeContent.Content = null;
            _nodeContent.Content = element;
        }
        if (_codeEditor is not null)
            _codeEditor.Visibility = Visibility.Collapsed;
        if (_decompilerTextView is not null)
            _decompilerTextView.Visibility = Visibility.Collapsed;
        if (_nodeHost is not null)
            _nodeHost.Visibility = Visibility.Visible;
    }
}
