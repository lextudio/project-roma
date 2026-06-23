// Roma host glue for the linked upstream ILSpy model.
//
// Session 26: the real TabPageModel, AssemblyTreeModel, DockWorkspace, NavigationHistory,
// and NavigationState are now linked from ext/ilspy. This file no longer stubs them —
// it provides only the Roma App surface (export provider + the App.Current.Dispatcher /
// CommandLineArguments / StartupExceptions members the linked startup paths compile
// against but never run) and the DecompilerTextViewState carrier (DecompilerTextView
// itself remains the Roma bridge in RomaTextViewStubs.cs).

using System.Collections.Generic;

using TomsToolbox.Composition;

namespace ICSharpCode.ILSpy
{
    // Roma's stand-in for the WPF App. Roma replaces MEF with direct construction; the
    // export provider supplies the handful of services the linked statics resolve.
    internal sealed class App
    {
        public static TomsToolbox.Composition.IExportProvider ExportProvider { get; internal set; } = new NullExportProvider();

        // WPF App.Current.Dispatcher: upstream tree nodes marshal lazy-load work onto the
        // UI dispatcher. Current returns a singleton carrying the current dispatcher.
        public static App Current { get; } = new();

        public System.Windows.Threading.Dispatcher Dispatcher { get; } = System.Windows.Threading.Dispatcher.CurrentDispatcher;

        // Mirrors App.xaml.cs. Referenced only by AssemblyTreeModel's startup paths
        // (Initialize/OpenAssemblies), which Roma never invokes — present for compilation.
        public static ICSharpCode.ILSpy.AppEnv.CommandLineArguments CommandLineArguments { get; set; }
            = ICSharpCode.ILSpy.AppEnv.CommandLineArguments.Create(System.Array.Empty<string>());

        public static IList<ExceptionData> StartupExceptions { get; } = new List<ExceptionData>();

        public static void UnhandledException(System.Exception exception) { }

        internal record ExceptionData(System.Exception Exception)
        {
            public string? PluginName { get; init; }
        }

        internal sealed class NullExportProvider : TomsToolbox.Composition.IExportProvider
        {
            public event System.EventHandler<System.EventArgs>? ExportsChanged { add { } remove { } }
            public T GetExportedValue<T>(string? contractName = null) where T : class => default!;
            public T? GetExportedValueOrDefault<T>(string? contractName = null) where T : class => default;
            public bool TryGetExportedValue<T>(string? contractName, out T? value) where T : class { value = default; return false; }
            public IEnumerable<T> GetExportedValues<T>(string? contractName = null) where T : class
            {
                if (typeof(T) == typeof(ICSharpCode.ILSpy.TreeNodes.IResourceNodeFactory))
                {
                    return
                    [
                        (new ICSharpCode.ILSpy.TreeNodes.ResourcesFileTreeNodeFactory() as T)!,
                    ];
                }

                return [];
            }
            public IEnumerable<object> GetExportedValues(System.Type contractType, string? contractName = null) => [];
            public IEnumerable<TomsToolbox.Composition.IExport<object>> GetExports(System.Type contractType, string? contractName = null) => [];
            public IEnumerable<TomsToolbox.Composition.IExport<T>> GetExports<T>(string? contractName = null) where T : class => [];
            public IEnumerable<TomsToolbox.Composition.IExport<T, TMetadataView>> GetExports<T, TMetadataView>(string? contractName = null) where T : class where TMetadataView : class => [];
        }

        // Real export provider for the linked model. Supplies LanguageService, SettingsService,
        // and lazily constructs the [Shared] DockWorkspace + AssemblyTreeModel singletons, plus
        // a fresh [NonShared] TabPageModel per request — matching upstream MEF lifetimes.
        //
        // Cycle break: LanguageService needs a DockWorkspace, DockWorkspace needs this provider,
        // and this provider needs the LanguageService. So LanguageService is late-bound: callers
        // create the provider, read DockWorkspace, build the LanguageService, then assign it back.
        internal sealed class RomaExportProvider : TomsToolbox.Composition.IExportProvider
        {
            private readonly ICSharpCode.ILSpy.SettingsService _settingsService;
            private ICSharpCode.ILSpy.LanguageService? _languageService;
            private ICSharpCode.ILSpy.Docking.DockWorkspace? _dockWorkspace;
            private ICSharpCode.ILSpy.AssemblyTree.AssemblyTreeModel? _assemblyTreeModel;
            private ICSharpCode.ILSpy.MainWindow? _mainWindow;
            private ICSharpCode.ILSpy.ViewModels.ToolPaneModel[] _toolPanes = [];

            public RomaExportProvider(ICSharpCode.ILSpy.SettingsService settingsService)
            {
                _settingsService = settingsService;
            }

            // The tool panes exposed to DockWorkspace.ToolPanes (contract "ToolPane"). Seeded by the
            // host after the panes are built; must be set before DockWorkspace.ToolPanes is first read
            // (it caches). Mirrors ILSpy's [ExportToolPane] MEF discovery.
            public void SetToolPanes(System.Collections.Generic.IEnumerable<ICSharpCode.ILSpy.ViewModels.ToolPaneModel> toolPanes)
                => _toolPanes = System.Linq.Enumerable.ToArray(toolPanes);

            // Assigned after the LanguageService is built (it depends on DockWorkspace).
            public ICSharpCode.ILSpy.LanguageService? LanguageService
            {
                get => _languageService;
                set => _languageService = value;
            }

            public ICSharpCode.ILSpy.Docking.DockWorkspace DockWorkspace
                => _dockWorkspace ??= new ICSharpCode.ILSpy.Docking.DockWorkspace(_settingsService, this);

            private ICSharpCode.ILSpy.AssemblyTree.AssemblyTreeModel AssemblyTreeModel
                => _assemblyTreeModel ??= new ICSharpCode.ILSpy.AssemblyTree.AssemblyTreeModel(_settingsService, _languageService!, this);

            public event System.EventHandler<System.EventArgs>? ExportsChanged { add { } remove { } }

            public T GetExportedValue<T>(string? contractName = null) where T : class
                => (GetExportedValueOrDefault<T>(contractName))!;

            public T? GetExportedValueOrDefault<T>(string? contractName = null) where T : class
            {
                if (typeof(T) == typeof(ICSharpCode.ILSpy.LanguageService)) return _languageService as T;
                if (typeof(T) == typeof(ICSharpCode.ILSpy.SettingsService)) return _settingsService as T;
                // Analyzer infrastructure (AnalyzerTreeNode.AssemblyList) resolves the list from here.
                if (typeof(T) == typeof(ICSharpCode.ILSpyX.AssemblyList)) return Roma.Host.Analyzers.RomaAnalyzerContext.AssemblyList as T;
                if (typeof(T) == typeof(ICSharpCode.ILSpy.Docking.DockWorkspace)) return DockWorkspace as T;
                if (typeof(T) == typeof(ICSharpCode.ILSpy.AssemblyTree.AssemblyTreeModel)) return AssemblyTreeModel as T;
                if (typeof(T) == typeof(ICSharpCode.ILSpy.ViewModels.TabPageModel)) return new ICSharpCode.ILSpy.ViewModels.TabPageModel(this) as T;
                if (typeof(T) == typeof(ICSharpCode.ILSpy.MainWindow)) return (_mainWindow ??= new ICSharpCode.ILSpy.MainWindow()) as T;
                return default;
            }

            public bool TryGetExportedValue<T>(string? contractName, out T? value) where T : class
            {
                value = GetExportedValueOrDefault<T>(contractName);
                return value is not null;
            }

            public IEnumerable<T> GetExportedValues<T>(string? contractName = null) where T : class
            {
                if (typeof(T) == typeof(ICSharpCode.ILSpy.TreeNodes.IResourceNodeFactory))
                {
                    return
                    [
                        (new ICSharpCode.ILSpy.TreeNodes.ResourcesFileTreeNodeFactory() as T)!,
                    ];
                }

                // DockWorkspace.ToolPanes => GetExportedValues<ToolPaneModel>("ToolPane").
                if (typeof(T) == typeof(ICSharpCode.ILSpy.ViewModels.ToolPaneModel) && contractName == "ToolPane")
                    return System.Linq.Enumerable.Cast<T>(_toolPanes);

                return [];
            }
            public IEnumerable<object> GetExportedValues(System.Type contractType, string? contractName = null) => [];
            public IEnumerable<TomsToolbox.Composition.IExport<object>> GetExports(System.Type contractType, string? contractName = null) => [];
            public IEnumerable<TomsToolbox.Composition.IExport<T>> GetExports<T>(string? contractName = null) where T : class => [];

            // AnalyzerTreeNode.Analyzers => GetExports<IAnalyzer, IAnalyzerMetadata>("Analyzer").
            // The analyzers are discovered once by RomaAnalyzerContext and exposed through this single
            // provider, so the ILSpy analyzer tree (which reads ICSharpCode.ILSpy.App.ExportProvider)
            // actually finds them.
            public IEnumerable<TomsToolbox.Composition.IExport<T, TMetadataView>> GetExports<T, TMetadataView>(string? contractName = null) where T : class where TMetadataView : class
            {
                if (typeof(T) == typeof(ICSharpCode.ILSpyX.Analyzers.IAnalyzer)
                    && typeof(TMetadataView) == typeof(ICSharpCode.ILSpyX.Analyzers.IAnalyzerMetadata))
                    return (IEnumerable<TomsToolbox.Composition.IExport<T, TMetadataView>>)Roma.Host.Analyzers.RomaAnalyzerContext.Analyzers;
                return [];
            }
        }
    }
}
// DecompilerTextViewState is provided by the linked real DecompilerTextView.cs (Session 27).
