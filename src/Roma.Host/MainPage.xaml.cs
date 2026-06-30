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

    // Static reference used by PersistDockLayoutOnExit and (in DEBUG builds) by DevFlow probe actions.
    private static MainPage? _current;

    // Navigation history (Back/Forward) is owned by AssemblyTreeModel.NavigationHistory; the toolbar
    // drives it via NavigateHistory and reads CanNavigateBack/CanNavigateForward (see UpdateNavButtons).

    // Last selected tree node, for re-decompile after settings change.
    private SharpTreeNode? _lastSelectedNode;

    // Set when a selection arrives before the document tab's DecompilerTextView has been realized
    // (MainPage.Loaded races the DocTabContent's own Loaded — the same race _pendingShowAbout handles).
    // OnTreeNodeSelected stashes the node here and ActivateTab replays it once the view is wired.
    private SharpTreeNode? _pendingRestoreNode;

    public MainPage()
    {
        _current = this;
        Status = CompatibilityInfo.GetDecompilerIntegrationStatus();
        var (_, context) = RomaAssemblyTree.BuildILSpyTree();
        _assemblyContext = context;
        RomaAnalyzerContext.Initialize(_assemblyContext.LanguageService, _assemblyContext.AssemblyList);
        // The real [Shared] AssemblyTreeModel is built by the export provider that
        // BuildILSpyTree installed as App.ExportProvider — reuse that single instance.
        _assemblyTreeModel = ICSharpCode.ILSpy.App.ExportProvider
            .GetExportedValue<ICSharpCode.ILSpy.AssemblyTree.AssemblyTreeModel>();
        _analyzerViewModel = ICSharpCode.ILSpy.App.ExportProvider
            .GetExportedValue<ICSharpCode.ILSpy.Analyzers.AnalyzerTreeViewModel>();
        _searchPaneModel = ICSharpCode.ILSpy.App.ExportProvider
            .GetExportedValue<ICSharpCode.ILSpy.Search.SearchPaneModel>();
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
        _treeContextMenu = new Roma.Host.ContextMenu.RomaTreeContextMenu(
            ICSharpCode.ILSpy.App.ExportProvider.GetExports<IContextMenuEntry, IContextMenuEntryMetadata>(),
            afterExecute: entry =>
            {
                if (entry is ICSharpCode.ILSpy.Analyzers.AnalyzeContextMenuCommand)
                    dockWorkspace.ShowToolPane("analyzerPane");
                if (entry is ICSharpCode.ILSpy.ScopeSearchToAssembly or ICSharpCode.ILSpy.ScopeSearchToNamespace)
                    dockWorkspace.ShowToolPane("searchPane");
            });
        ICSharpCode.ILSpy.ContextMenuProvider.AttachDataGridContextMenu =
            grid => new Roma.Host.ContextMenu.RomaDataGridContextMenu().Attach(grid);
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
        InitializeMacOSNativeMenu();
        ApplyMacOSAppMenuVisibility();
        // Reference jumps (clicking a type/member link in the decompiled text) are handled by the real
        // AssemblyTreeModel, which subscribes to NavigateToReferenceEventArgs in its ctor and routes
        // through JumpToReferenceAsync → SelectNode → our SelectedItems observer. No Roma handler needed.
        Microsoft.UI.Xaml.RoutedEventHandler? onLoaded = null;
        onLoaded = (_, _) =>
        {
            Loaded -= onLoaded;
            // macOS: Uno's Skia host doesn't deliver OS file drops to the XAML layer, so hook the
            // native NSView's NSDraggingDestination directly. The callback runs on the AppKit main
            // thread; marshal onto the dispatcher to mutate the assembly list. (Windows/Linux use the
            // tree's DragOver/Drop handlers wired in CreateAssemblyBrowserContent.)
            MacOSFileDrop.RegisterHandler(paths =>
                DispatcherQueue.TryEnqueue(() => OpenAssembliesFromPaths(paths)));
            if (MacOSFileDrop.Install())
                Dbg("macOS native file-drop handler installed");
            // Re-register the open-documents Apple Event handler now that AppKit/Uno have finished
            // launching — finishLaunching installs the default odoc handler that overwrites our
            // earlier (OnLaunched) registration, so claim it again here to win the race.
            MacOSFileDrop.RegisterOpenDocumentsHandler();
            // At startup, XamlRoot can be unavailable during InitializeComponent/constructor time.
            // Reapply the persisted theme once loaded so the window root, main menu, and toolbar
            // all resolve ThemeResource brushes from the selected light/dark dictionary.
            ReapplyPersistedTheme();
            ApplyMacOSAppMenuVisibility();
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
            OnSaved = () =>
            {
                ApplyMacOSAppMenuVisibility();
                RedecompileCurrentNode();
            },
        };
        await dialog.ShowAsync();
    }

    private void InitializeMacOSNativeMenu()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        if (MacOSNativeMenu.Install(
            command => DispatcherQueue.TryEnqueue(() => ExecuteNativeMenuCommand(command)),
            BuildMacOSNativeMenuState()))
            Dbg("macOS native menu installed");
    }

    private void RefreshMacOSNativeMenu()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        MacOSNativeMenu.Update(BuildMacOSNativeMenuState());
    }

    private MacOSNativeMenu.NativeMenuState BuildMacOSNativeMenuState()
    {
        var apiVisibility = _assemblyContext.SettingsService.SessionSettings.LanguageSettings.ShowApiLevel;
        var currentTheme = _assemblyContext.SettingsService.SessionSettings.Theme;
        var currentCulture = _assemblyContext.SettingsService.SessionSettings.CurrentCulture;
        var currentLanguage = _assemblyContext.LanguageService.Language;
        var currentVersion = _assemblyContext.LanguageService.LanguageVersion;

        var apiItems = new[]
        {
            new MacOSNativeMenu.NativeMenuChoice("Show public types and members only", "view.apiPublicOnly", apiVisibility == ApiVisibility.PublicOnly, "person"),
            new MacOSNativeMenu.NativeMenuChoice("Show internal types and members", "view.apiPublicInternal", apiVisibility == ApiVisibility.PublicAndInternal, "person.2"),
            new MacOSNativeMenu.NativeMenuChoice("Show all types and members", "view.apiAll", apiVisibility == ApiVisibility.All, "person.3"),
        };

        var themeItems = RomaThemes
            .Select(t => new MacOSNativeMenu.NativeMenuChoice(t.Name, $"theme:{t.Name}", t.Name == currentTheme, t.IsDark ? "moon" : "sun.max"))
            .ToList();

        var uiLanguageItems = new List<MacOSNativeMenu.NativeMenuChoice>
        {
            new("System Default", "uiCulture:", string.IsNullOrEmpty(currentCulture), "globe")
        };
        uiLanguageItems.AddRange(AvailableUiCultures()
            .Select(c => new MacOSNativeMenu.NativeMenuChoice(
                $"{c.NativeName} ({c.Name})",
                $"uiCulture:{c.Name}",
                string.Equals(currentCulture, c.Name, StringComparison.OrdinalIgnoreCase),
                "character.book.closed")));

        var languageItems = _assemblyContext.LanguageService.AllLanguages
            .Select(l => new MacOSNativeMenu.NativeMenuChoice(
                l.Name,
                $"language:{l.Name}",
                ReferenceEquals(l, currentLanguage) || string.Equals(l.Name, currentLanguage?.Name, StringComparison.Ordinal),
                "chevron.left.forwardslash.chevron.right"))
            .ToList();

        var versionItems = currentLanguage?.HasLanguageVersions == true
            ? currentLanguage.LanguageVersions
                .Select(v => new MacOSNativeMenu.NativeMenuChoice(
                    v.DisplayName,
                    $"languageVersion:{v.DisplayName}",
                    ReferenceEquals(v, currentVersion) || string.Equals(v.DisplayName, currentVersion?.DisplayName, StringComparison.Ordinal),
                    "number"))
                .ToList()
            : [];

        return new(apiItems, themeItems, uiLanguageItems, languageItems, versionItems);
    }

    private void ApplyMacOSAppMenuVisibility()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var hostSettings = _assemblyContext.SettingsService.GetSettings<RomaHostSettings>();
        _menuBar.Visibility = hostSettings.HideMacOSAppMenu
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ExecuteNativeMenuCommand(string command)
    {
        switch (command)
        {
            case "file.open": _ = OpenAssemblyAsync(); break;
            case "file.openGac": OnMenuOpenFromGac(_menuBar, new RoutedEventArgs()); break;
            case "file.manageAssemblyLists": ShowManageAssemblyListsDialog(); break;
            case "file.reloadAll": ReloadAll(); break;
            case "file.saveCode": OnMenuSaveCode(_menuBar, new RoutedEventArgs()); break;
            case "file.removeErrors": OnMenuRemoveAssembliesWithErrors(_menuBar, new RoutedEventArgs()); break;
            case "file.clearAssemblyList": OnMenuClearAssemblyList(_menuBar, new RoutedEventArgs()); break;
            case "file.exit": Microsoft.UI.Xaml.Application.Current.Exit(); break;
            case "view.apiPublicOnly": SetApiVis(ApiVisibility.PublicOnly); break;
            case "view.apiPublicInternal": SetApiVis(ApiVisibility.PublicAndInternal); break;
            case "view.apiAll": SetApiVis(ApiVisibility.All); break;
            case "view.sortAssemblyList": OnSortAssemblyList(_menuBar, new RoutedEventArgs()); break;
            case "view.collapseAll": OnCollapseAll(_menuBar, new RoutedEventArgs()); break;
            case "view.options": OnMenuOptions(_menuBar, new RoutedEventArgs()); break;
            case "window.assemblies": OnMenuShowAssembliesPane(_menuBar, new RoutedEventArgs()); break;
            case "window.search": ShowSearchPane(); break;
            case "window.analyze": DockWorkspace.ShowToolPane("analyzerPane"); break;
            case "window.debugSteps": OnMenuShowDebugStepsPane(_menuBar, new RoutedEventArgs()); break;
            case "window.closeAllDocuments": OnMenuCloseAllDocuments(_menuBar, new RoutedEventArgs()); break;
            case "window.resetLayout": OnMenuResetLayout(_menuBar, new RoutedEventArgs()); break;
            case "window.code": OnMenuShowCodeTab(_menuBar, new RoutedEventArgs()); break;
            case "help.checkUpdates": OnCheckForUpdates(); break;
            case "help.about": ShowAboutPage(); break;
            default:
                if (command.StartsWith("theme:", StringComparison.Ordinal))
                {
                    ApplyThemeFromNativeMenu(command["theme:".Length..]);
                }
                else if (command.StartsWith("uiCulture:", StringComparison.Ordinal))
                {
                    ApplyCulture(command["uiCulture:".Length..], persist: true);
                    RefreshMacOSNativeMenu();
                }
                else if (command.StartsWith("language:", StringComparison.Ordinal))
                {
                    ApplyDecompilerLanguageFromNativeMenu(command["language:".Length..]);
                }
                else if (command.StartsWith("languageVersion:", StringComparison.Ordinal))
                {
                    ApplyDecompilerLanguageVersionFromNativeMenu(command["languageVersion:".Length..]);
                }
                else
                {
                    Dbg($"macOS native menu: unknown command '{command}'");
                }
                break;
        }
    }

    private void ApplyThemeFromNativeMenu(string name)
    {
        var def = RomaThemes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        if (def.Name is null)
            return;
        ApplyTheme(def.Name, def.IsDark);
    }

    private void ApplyDecompilerLanguageFromNativeMenu(string name)
    {
        var language = _assemblyContext.LanguageService.AllLanguages
            .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal));
        if (language is null)
            return;

        _assemblyContext.LanguageService.Language = language;
        _languageCombo.SelectedItem = language;
        UpdateLanguageVersionCombo(language);
        RefreshMacOSNativeMenu();
        Dbg($"Native Menu: Language → {language.Name}");
        RedecompileCurrentNode();
    }

    private void ApplyDecompilerLanguageVersionFromNativeMenu(string displayName)
    {
        var language = _assemblyContext.LanguageService.Language;
        if (language?.HasLanguageVersions != true)
            return;

        var version = language.LanguageVersions
            .FirstOrDefault(v => string.Equals(v.DisplayName, displayName, StringComparison.Ordinal));
        if (version is null)
            return;

        _assemblyContext.LanguageService.LanguageVersion = version;
        _languageVersionCombo.SelectedItem = version;
        RefreshMacOSNativeMenu();
        Dbg($"Native Menu: LanguageVersion → {version.DisplayName}");
        RedecompileCurrentNode();
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
        RefreshMacOSNativeMenu();

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
        RefreshMacOSNativeMenu();
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
            RefreshMacOSNativeMenu();
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

        OpenAssembliesFromPaths(dlg.FileNames);
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
    private ICSharpCode.ILSpy.Search.SearchPaneModel _searchPaneModel = null!;
    private RomaToolPaneModel? _browserPaneModel;
    private ICSharpCode.ILSpy.Analyzers.AnalyzerTreeViewModel _analyzerViewModel = null!;
    private Roma.Host.ContextMenu.RomaTreeContextMenu? _treeContextMenu;
    private ICSharpCode.ILSpy.Controls.TreeView.SharpTreeView? _assemblyTree;
    private ICSharpCode.ILSpy.AssemblyTree.AssemblyTreeModel? _assemblyTreeModel;

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
            _assemblyContext.SettingsService,
            _searchPaneModel);

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

        // Accept assemblies dragged in from Finder / Explorer. On Windows/Linux these WinUI
        // StorageItems drop events fire normally; on macOS they don't (see MacOSFileDrop, wired in
        // the Loaded handler), so this path is effectively the non-macOS one.
        _assemblyTree.AllowDrop = true;
        _assemblyTree.DragOver += OnAssemblyTreeDragOver;
        _assemblyTree.Drop += OnAssemblyTreeDrop;

        return _assemblyTree;
    }

    // Show the "copy" cursor while files are dragged over the tree; reject non-file payloads
    // (e.g. text) so the OS gives the user the no-drop cursor.
    private void OnAssemblyTreeDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open assembly";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        else
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
    }

    private async void OnAssemblyTreeDrop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        // GetStorageItemsAsync can complete after the event returns, so take a deferral to keep the
        // DataView alive while we read it.
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            OpenAssembliesFromPaths(items.OfType<Windows.Storage.StorageFile>().Select(f => f.Path).ToList());
        }
        catch (Exception ex)
        {
            Dbg($"OnAssemblyTreeDrop: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    // Opens the given paths as assemblies and selects/focuses the first one in the tree (shared by
    // the WinUI drop handler and the macOS native drop / Open-With hook). Must run on the UI thread.
    // Uses the model's OpenFiles (the same path ILSpy's drop/open-file uses), which loads each file
    // and focuses its node — so a freshly opened assembly becomes the tree selection rather than
    // leaving the prior/restored selection in place (notably after a cold "Open With" launch).
    private void OpenAssembliesFromPaths(IReadOnlyList<string> paths)
    {
        var valid = paths.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        if (valid.Length == 0)
            return;
        try
        {
            _assemblyContext.AssemblyList.UseDebugSymbols = true;
            _assemblyTreeModel?.OpenFiles(valid, focusNode: true);
            Dbg($"Opened (drop): {string.Join(", ", valid)}");
        }
        catch (Exception ex)
        {
            Dbg($"OpenAssembliesFromPaths: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private MenuFlyout? BuildTreeContextFlyout(TextViewContext ctx)
        => _treeContextMenu?.Build(ctx);

    private static readonly string _dbgLog = "/tmp/roma-debug.log";

    private static void Dbg(string msg)
    {
        try { System.IO.File.AppendAllText(_dbgLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    // Truncates the debug log so each run starts clean (called at the very start of App.OnLaunched).
    internal static void ResetLog()
    {
        try { System.IO.File.WriteAllText(_dbgLog, $"[{DateTime.Now:HH:mm:ss.fff}] ── Roma started ──\n"); } catch { }
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
