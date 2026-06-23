using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpy;
using ICSharpCode.ILSpy.TreeNodes;
using SettingsService = ICSharpCode.ILSpy.SettingsService;
using ICSharpCode.ILSpy.ViewModels;
using ICSharpCode.ILSpyX;
using ICSharpCode.ILSpyX.FileLoaders;
using ICSharpCode.ILSpyX.Settings;
using ICSharpCode.ILSpyX.TreeView;
using System.Xml.Linq;

namespace Roma.Host;

// Session 14: reuse the cross-platform upstream ILSpyX SharpTreeNode model as the
// tree's backing type, so the Roma host can bind it to a WinUI/Uno TreeView while
// keeping API parity with the real ILSpy tree nodes. These minimal node subclasses
// stand in for the WPF-coupled ICSharpCode.ILSpy.TreeNodes.* until those can be
// hosted; they expose the same SharpTreeNode surface (Text, Children, LazyLoading,
// LoadChildren) the upstream tree control consumes.
//
// Session 15: RomaAssemblyContext wraps the assembly + CSharpDecompiler so the
// host can call DecompileType(fullTypeName) on selection without rebuilding the
// decompiler on every click.
//
// Session 21: expose the ILSpy services (LanguageService, SettingsService, AssemblyList,
// AssemblyListManager) so MainPage can wire toolbar controls to real bindings.

public sealed class RomaAssemblyContext
{
    private readonly CSharpDecompiler _decompiler;

    internal RomaAssemblyContext(
        SharpTreeNode root,
        CSharpDecompiler decompiler,
        LanguageService languageService,
        SettingsService settingsService,
        AssemblyList assemblyList,
        AssemblyListManager assemblyListManager)
    {
        Root = root;
        _decompiler = decompiler;
        LanguageService = languageService;
        SettingsService = settingsService;
        AssemblyList = assemblyList;
        AssemblyListManager = assemblyListManager;
    }

    public SharpTreeNode Root { get; private set; }

    // The first assembly node under the (AssemblyListTreeNode) root — i.e. the Roma assembly
    // that the bundled CSharpDecompiler/TypeSystem in this context was built for. Used by the
    // legacy single-assembly helpers (FindTypeNode, metadata diagnostics).
    public ICSharpCode.ILSpy.TreeNodes.AssemblyTreeNode? PrimaryAssemblyNode =>
        Root.Children.OfType<ICSharpCode.ILSpy.TreeNodes.AssemblyTreeNode>().FirstOrDefault();

    public LanguageService LanguageService { get; }
    public SettingsService SettingsService { get; }
    public AssemblyList AssemblyList { get; private set; }
    public AssemblyListManager AssemblyListManager { get; }

    // Swaps the active assembly list (and its backing AssemblyListTreeNode) when the user switches
    // lists via the combo or the Manage Assembly Lists dialog. The host rebuilds the browser tree
    // against the new Root and re-inits the analyzer with the new list; the bundled legacy decompiler
    // is left as-is (the live decompile path uses each node's own language/DecompilerTextView).
    internal void SwitchList(SharpTreeNode root, AssemblyList assemblyList)
    {
        Root = root;
        AssemblyList = assemblyList;
    }
    // Bundled decompiler/typesystem for the Roma assembly, used by FindTypeNode to resolve a type
    // into the live ILSpy tree.
    public CSharpDecompiler Decompiler => _decompiler;
}

public static class RomaAssemblyTree
{

    // Builds the REAL ILSpy AssemblyTreeNode tree (assembly → namespace/type/metadata
    // subtrees) so Roma.Host can wire up node selection to View() calls that return
    // live DataGrids for metadata tables and DecompilerTextView for code nodes.
    public static (SharpTreeNode Root, RomaAssemblyContext Context) BuildILSpyTree()
    {
        // Build the real ILSpy SettingsService first. Its base ctor runs LoadSettings(), which
        // points ILSpySettings.SettingsFilePathProvider at Roma's ILSpySettingsFilePathProvider
        // (%AppData%\LeXtudio\Roma\Roma.ILSpy.xml) and loads the on-disk settings. Reusing this
        // service's file-backed AssemblyListManager — instead of the former throwaway in-memory
        // provider — gives ILSpy-faithful persistence: AssemblyList.SaveAsXml writes only the
        // assemblies the user explicitly opened (IsAutoLoaded == false) and reloads them on the
        // next launch; reference assemblies that were auto-loaded are never persisted.
        var settingsService = new SettingsService();
        // Cycle break: LanguageService needs a DockWorkspace, DockWorkspace needs the export
        // provider, the provider needs the LanguageService. Create the provider first, read the
        // (lazily built) DockWorkspace, build the LanguageService, then assign it back. Done
        // before any ILSpyTreeNode subclass is constructed (ILSpyTreeNode.LanguageService is a
        // static property initialized on first access via App.ExportProvider).
        var exportProvider = new ICSharpCode.ILSpy.App.RomaExportProvider(settingsService);
        var dockWorkspace = exportProvider.DockWorkspace;
        var languageService = new LanguageService(
            new Language[] { new RomaCSharpLanguage(), new RomaILLanguage() },
            settingsService,
            dockWorkspace);
        exportProvider.LanguageService = languageService;
        ICSharpCode.ILSpy.App.ExportProvider = exportProvider;

        var assemblyListManager = settingsService.AssemblyListManager;
        assemblyListManager.UseDebugSymbols = true;

        // Drop the legacy auto-seeded "Roma" demo list (removed in session 34's follow-up). One-time
        // migration: existing installs carry it until deleted here.
        if (assemblyListManager.AssemblyLists.Contains("Roma"))
            assemblyListManager.DeleteList("Roma");

        // First run (no lists persisted): seed the defaults. On Windows, add ILSpy's GAC-based default
        // lists (.NET 4 (WPF) / .NET 3.5 / ASP.NET (MVC3)) — CreateDefaultAssemblyLists only adds the
        // ones whose assemblies actually resolve from the GAC, and must run while the set is still
        // empty (it bails if any list exists). Always add Roma's cross-platform "Uno Platform" preset.
        if (assemblyListManager.AssemblyLists.Count == 0)
        {
            if (OperatingSystem.IsWindows())
                assemblyListManager.CreateDefaultAssemblyLists();
            assemblyListManager.AddListIfNotExists(RomaAssemblyListPresets.CreateUnoPlatformList(assemblyListManager));
        }

        // Resolve the active list: the persisted choice if it still exists, otherwise "Uno Platform"
        // (Roma's cross-platform default), otherwise whatever list remains. Honoring the persisted
        // name makes the user's toolbar/dialog selection survive restart.
        var activeName = settingsService.SessionSettings.ActiveAssemblyList;
        if (string.IsNullOrEmpty(activeName) || !assemblyListManager.AssemblyLists.Contains(activeName))
        {
            activeName = assemblyListManager.AssemblyLists.Contains(RomaAssemblyListPresets.UnoPlatform)
                ? RomaAssemblyListPresets.UnoPlatform
                : assemblyListManager.AssemblyLists.FirstOrDefault() ?? RomaAssemblyListPresets.UnoPlatform;
        }
        settingsService.SessionSettings.ActiveAssemblyList = activeName;

        var assemblyList = assemblyListManager.LoadList(activeName);
        assemblyList.UseDebugSymbols = true;

        // Bundled decompiler/typesystem for the legacy DecompileType path, built straight from the Roma
        // assembly path (independent of the active list; the live path decompiles via each node's
        // language/DecompilerTextView instead).
        var assemblyPath = typeof(RomaAssemblyTree).Assembly.Location;
        // ThrowOnAssemblyResolveErrors defaults to true, which aborts type-system init the moment a
        // reference can't be found — and when Roma runs as a GUI-launched app (no shell PATH), the
        // resolver's DotNetCorePathFinder can't locate `dotnet`, so the .NET shared runtime
        // (System.Runtime etc.) is unreachable. ILSpy's own CLI/PowerShell entry points set this to
        // false so missing references degrade gracefully instead of crashing; do the same here.
        CSharpDecompiler decompiler = new CSharpDecompiler(assemblyPath,
            new ICSharpCode.Decompiler.DecompilerSettings { ThrowOnAssemblyResolveErrors = false });

        // ILSpy design: the (invisible) tree root is an AssemblyListTreeNode bound to the
        // AssemblyList. Rather than build a standalone root here, hand the resolved list to the real
        // AssemblyTreeModel so IT owns the AssemblyListTreeNode — then Roma renders model.Root. This
        // unifies the displayed tree with the model's SelectNode/FindNodeByPath/history, so navigation
        // and session restore can be driven entirely by upstream ILSpy code (no Roma-local mirrors).
        var assemblyTreeModel = exportProvider.GetExportedValue<ICSharpCode.ILSpy.AssemblyTree.AssemblyTreeModel>();
        assemblyTreeModel.ShowAssemblyListForHost(assemblyList);
        var root = assemblyTreeModel.Root!;

        return (root, new RomaAssemblyContext(root, decompiler, languageService, settingsService, assemblyList, assemblyListManager));
    }
}
