using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpy;
using ICSharpCode.ILSpy.Docking;
using ICSharpCode.ILSpy.Util;
using ICSharpCode.ILSpyX;
using ICSharpCode.ILSpyX.FileLoaders;
using ICSharpCode.ILSpyX.Settings;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Xml.Linq;

namespace Roma.Host;

public sealed record NamespaceBrowserItem(string Name, int TypeCount);
public sealed record TypeBrowserItem(string FullName);

public sealed record DecompilerIntegrationStatus(
    string UpstreamTarget,
    int IlTransformCount,
    int AstTransformCount,
    int FileLoaderCount,
    int AssemblyListCount,
    string LoadedAssemblyName,
    bool MetadataLoaded,
    int TopLevelTypeCount,
    int UserTypeCount,
    int NamespaceCount,
    string NamespaceSummary,
    string CandidateTypes,
    string SessionLanguage,
    string SessionTheme,
    string SessionAssemblyList,
    int AvailableLanguageCount,
    string ActiveLanguageVersion,
    bool MainWindowViewModelCreated,
    IReadOnlyList<NamespaceBrowserItem> Namespaces,
    IReadOnlyList<TypeBrowserItem> Types,
    string DecompiledTypeName,
    bool DecompilationSucceeded,
    string DecompilationPreview);

public static class CompatibilityInfo
{
    public const string UpstreamTarget = "ILSpy v10.1";

    public static DecompilerIntegrationStatus GetDecompilerIntegrationStatus()
    {
        var fileLoaderRegistry = new FileLoaderRegistry();
        var settingsProvider = new InMemorySettingsProvider();
        var assemblyListManager = new AssemblyListManager(settingsProvider);
        var assemblyList = assemblyListManager.CreateList("Roma");
        var currentAssemblyPath = typeof(CompatibilityInfo).Assembly.Location;
        var loadedAssembly = assemblyList.OpenAssembly(currentAssemblyPath);
        var metadataFile = loadedAssembly.GetMetadataFileOrNull();
        var metadataLoaded = metadataFile is MetadataFile;
        var topLevelTypeCount = 0;
        var userTypeCount = 0;
        var namespaceCount = 0;
        var namespaceSummary = string.Empty;
        var candidateTypes = string.Empty;
        var sessionLanguage = string.Empty;
        var sessionTheme = string.Empty;
        var sessionAssemblyList = string.Empty;
        var availableLanguageCount = 0;
        var activeLanguageVersion = string.Empty;
        var mainWindowViewModelCreated = false;
        IReadOnlyList<NamespaceBrowserItem> namespaces = Array.Empty<NamespaceBrowserItem>();
        IReadOnlyList<TypeBrowserItem> types = Array.Empty<TypeBrowserItem>();
        var decompiledTypeName = new FullTypeName(typeof(CompatibilityInfo).FullName!);
        var decompilationSettings = new ICSharpCode.Decompiler.DecompilerSettings();
        var decompilationPreview = string.Empty;
        var decompilationSucceeded = false;
        var settingsService = new ICSharpCode.ILSpy.SettingsService();
        var exportProvider = new ICSharpCode.ILSpy.App.RomaExportProvider(settingsService);
        var dockWorkspace = exportProvider.DockWorkspace;
        var languageService = new LanguageService(
            new Language[]
            {
                new RomaCSharpLanguage(),
                new RomaILLanguage()
            },
            settingsService,
            dockWorkspace);
        exportProvider.LanguageService = languageService;
        var mainWindowViewModel = new MainWindowViewModel(settingsService, languageService, dockWorkspace);

        sessionLanguage = settingsService.SessionSettings.LanguageSettings.LanguageId ?? string.Empty;
        sessionTheme = settingsService.SessionSettings.Theme ?? string.Empty;
        sessionAssemblyList = settingsService.SessionSettings.ActiveAssemblyList ?? string.Empty;
        availableLanguageCount = languageService.AllLanguages.Count;
        activeLanguageVersion = languageService.LanguageVersion?.DisplayName ?? string.Empty;
        mainWindowViewModelCreated = mainWindowViewModel.Workspace is not null
            && ReferenceEquals(mainWindowViewModel.LanguageService, languageService)
            && ReferenceEquals(mainWindowViewModel.AssemblyListManager, settingsService.AssemblyListManager);

        if (metadataFile is not null)
        {
            var metadata = metadataFile.Metadata;
            var topLevelTypes = metadata.GetTopLevelTypeDefinitions().ToArray();
            var userTopLevelTypes = topLevelTypes
                .Where(handle => IsUserTopLevelType(handle, metadata))
                .ToArray();
            topLevelTypeCount = topLevelTypes.Length;
            var selectedTypeHandle = userTopLevelTypes.FirstOrDefault();
            userTypeCount = userTopLevelTypes.Length;
            var namespaceGroups = userTopLevelTypes
                .Select(handle => handle.GetFullTypeName(metadata))
                .GroupBy(type => type.TopLevelTypeName.Namespace, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToArray();
            namespaces = namespaceGroups
                .Select(group => new NamespaceBrowserItem(
                    string.IsNullOrEmpty(group.Key) ? "<global>" : group.Key,
                    group.Count()))
                .ToArray();
            types = userTopLevelTypes
                .Select(handle => new TypeBrowserItem(handle.GetFullTypeName(metadata).FullName))
                .ToArray();
            namespaceCount = namespaceGroups.Length;
            namespaceSummary = string.Join(
                ", ",
                namespaces
                    .Take(3)
                    .Select(item => $"{item.Name} ({item.TypeCount})"));
            candidateTypes = string.Join(
                ", ",
                types
                    .Take(3)
                    .Select(item => item.FullName));

            if (!selectedTypeHandle.IsNil)
            {
                decompiledTypeName = selectedTypeHandle.GetFullTypeName(metadata);
            }

            var decompiler = new CSharpDecompiler(
                metadataFile,
                metadataFile.GetAssemblyResolver(),
                decompilationSettings);
            var decompiledSource = decompiler.DecompileTypeAsString(decompiledTypeName);
            decompilationPreview = GetPreviewLine(decompiledSource);
            decompilationSucceeded = decompilationPreview.Length > 0;
        }

        return new(
            UpstreamTarget,
            CSharpDecompiler.GetILTransforms().Count,
            CSharpDecompiler.GetAstTransforms().Count,
            fileLoaderRegistry.RegisteredLoaders.Count,
            assemblyListManager.AssemblyLists.Count,
            loadedAssembly.ShortName,
            metadataLoaded,
            topLevelTypeCount,
            userTypeCount,
            namespaceCount,
            namespaceSummary,
            candidateTypes,
            sessionLanguage,
            sessionTheme,
            sessionAssemblyList,
            availableLanguageCount,
            activeLanguageVersion,
            mainWindowViewModelCreated,
            namespaces,
            types,
            decompiledTypeName.FullName,
            decompilationSucceeded,
            decompilationPreview);
    }

    private static bool IsUserTopLevelType(TypeDefinitionHandle handle, MetadataReader metadata)
    {
        var fullTypeName = handle.GetFullTypeName(metadata);
        return fullTypeName.Name != "<Module>"
            && !handle.IsCompilerGeneratedOrIsInCompilerGeneratedClass(metadata);
    }

    private static string GetPreviewLine(string decompiledSource)
        => decompiledSource
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(static line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            ?.Trim()
            ?? string.Empty;

    private sealed class InMemorySettingsProvider : ISettingsProvider
    {
        private readonly XElement _root = new("ILSpy");

        public XElement this[XName section] => _root.Element(section) ?? new XElement(section);

        public void Update(Action<XElement> action) => action(_root);

        public void SaveSettings(XElement section)
            => Update(root =>
            {
                var existing = root.Element(section.Name);
                if (existing is not null)
                {
                    existing.ReplaceWith(section);
                }
                else
                {
                    root.Add(section);
                }
            });
    }
}
