using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.ILSpy;
using SettingsService = ICSharpCode.ILSpy.Util.SettingsService;
using ICSharpCode.ILSpy.AppEnv;
using ICSharpCode.ILSpyX;
using ICSharpCode.ILSpyX.Abstractions;
using ICSharpCode.ILSpyX.Search;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ICSharpCode.ILSpyX.Extensions;
using ILSpyResources = ICSharpCode.ILSpy.Properties.Resources;

namespace Roma.Host;

public sealed partial class SearchPane : UserControl
{
    const int MAX_RESULTS = 1000;
    const int MAX_REFRESH_TIME_MS = 10;

    RunningSearch? _currentSearch;
    IComparer<SearchResult> _resultsComparer = SearchResult.ComparerByName;

    readonly AssemblyList _assemblyList;
    readonly LanguageService _languageService;
    readonly SettingsService _settingsService;
    readonly ITreeNodeFactory _treeNodeFactory;

    public ObservableCollection<SearchResult> Results { get; } = [];

    public SearchPane(
        AssemblyList assemblyList,
        LanguageService languageService,
        SettingsService settingsService)
    {
        _assemblyList    = assemblyList;
        _languageService = languageService;
        _settingsService = settingsService;
        _treeNodeFactory = new RomaTreeNodeFactory();

        InitializeComponent();

        _searchBox.PlaceholderText = ILSpyResources.WatermarkText;
        ToolTipService.SetToolTip(_searchBox, ILSpyResources.SearchPane_Search);
        _searchForLabel.Text = RemoveMnemonic(ILSpyResources._SearchFor);

        _resultList.ItemsSource = Results;

        // Populate mode combo (uses no WPF types — pure data)
        _modeCombo.ItemsSource  = SearchModes;
        _modeCombo.SelectedIndex = 0;

        // Drain result queue on the UI thread every ~16 ms (replaces CompositionTarget.Rendering)
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += (_, _) => DrainResultQueue();
        timer.Start();
    }

    static string RemoveMnemonic(string text)
    {
        var underscore = text.IndexOf('_');
        return underscore >= 0 ? text.Remove(underscore, 1) : text;
    }

    // ── Search mode descriptors (no WPF ImageSource needed) ─────

    const string IconBase = "ms-appx:///ILSpyIcons/";

    static readonly SearchModeDescriptor[] SearchModes =
    [
        new(SearchMode.TypeAndMember, "Types and Members", IconBase + "library.svg"),
        new(SearchMode.Type,          "Type",              IconBase + "class.svg"),
        new(SearchMode.Member,        "Member",            IconBase + "property.svg"),
        new(SearchMode.Method,        "Method",            IconBase + "method.svg"),
        new(SearchMode.Field,         "Field",             IconBase + "field.svg"),
        new(SearchMode.Property,      "Property",          IconBase + "property.svg"),
        new(SearchMode.Event,         "Event",             IconBase + "event.svg"),
        new(SearchMode.Literal,       "Constant",          IconBase + "literal.svg"),
        new(SearchMode.Token,         "Metadata Token",    IconBase + "library.svg"),
        new(SearchMode.Resource,      "Resource",          IconBase + "resource.svg"),
        new(SearchMode.Assembly,      "Assembly",          IconBase + "assembly.svg"),
        new(SearchMode.Namespace,     "Namespace",         IconBase + "namespace.svg"),
    ];

    internal sealed class SearchModeDescriptor(SearchMode mode, string name, string iconUri)
    {
        public SearchMode Mode { get; } = mode;
        public string Name { get; } = name;
        public Microsoft.UI.Xaml.Media.Imaging.SvgImageSource Icon { get; } = new(new Uri(iconUri));
    }

    // ── UI event handlers ────────────────────────────────────────

    void OnSearchBoxTextChanged(object sender, TextChangedEventArgs e)
        => StartSearch(_searchBox.Text);

    void OnModeSelectionChanged(object sender, SelectionChangedEventArgs e)
        => StartSearch(_searchBox.Text);

    void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Down && _resultList.Items.Count > 0)
        {
            e.Handled = true;
            _resultList.SelectedIndex = 0;
            _resultList.Focus(FocusState.Keyboard);
        }
    }

    void OnResultDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        => JumpToSelectedItem();

    void OnResultListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            JumpToSelectedItem();
        }
        else if (e.Key == Windows.System.VirtualKey.Up && _resultList.SelectedIndex == 0)
        {
            e.Handled = true;
            _resultList.SelectedIndex = -1;
            _searchBox.Focus(FocusState.Keyboard);
        }
    }

    void JumpToSelectedItem()
    {
        if (_resultList.SelectedItem is SearchResult result)
            MessageBus.Send(this, new NavigateToReferenceEventArgs(result.Reference));
    }

    // ── Result queue drain (runs on UI thread every ~16 ms) ─────

    void DrainResultQueue()
    {
        if (_currentSearch is null)
            return;

        var sw = Stopwatch.StartNew();
        int added = 0;
        while (Results.Count < MAX_RESULTS
               && sw.ElapsedMilliseconds < MAX_REFRESH_TIME_MS
               && _currentSearch.ResultQueue.TryTake(out var result))
        {
            Results.InsertSorted(result, _resultsComparer);
            added++;
        }

        if (added > 0 && Results.Count == MAX_RESULTS)
        {
            Results.Add(new SearchResult
            {
                Name          = "Search aborted — more than 1 000 results found.",
                Location      = string.Empty,
                Assembly      = string.Empty,
                Image         = null!,
                LocationImage = null!,
                AssemblyImage = null!,
            });
            _currentSearch.Cancel();
        }
    }

    // ── Search orchestration ─────────────────────────────────────

    async void StartSearch(string? searchTerm)
    {
        if (_currentSearch is not null)
        {
            _currentSearch.Cancel();
            _currentSearch = null;
        }

        _resultsComparer = SearchResult.ComparerByName;

        Results.Clear();

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            _progressBar.IsIndeterminate = false;
            return;
        }

        var mode = _modeCombo.SelectedItem is SearchModeDescriptor d ? d.Mode : SearchMode.TypeAndMember;
        var assemblies = await _assemblyList.GetAllAssemblies();

        var search = new RunningSearch(
            assemblies,
            searchTerm,
            mode,
            _languageService.Language,
            _languageService.LanguageVersion,
            _treeNodeFactory,
            _settingsService);

        _currentSearch = search;
        _progressBar.IsIndeterminate = true;

        await search.Run();

        if (_currentSearch == search)
            _progressBar.IsIndeterminate = false;
    }

    // Scope the current search to a single assembly by adding/replacing an `inassembly:` filter
    // term, then re-run — mirrors ICSharpCode.ILSpy.ScopeSearchToAssembly against Roma's own
    // SearchPane (Roma does not compile ILSpy's SearchPaneModel).
    public void ScopeToAssembly(string assemblyName)
    {
        var term = _searchBox.Text ?? string.Empty;
        var args = CommandLineTools.CommandLineToArgumentArray(term);
        bool replaced = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("inassembly:", StringComparison.OrdinalIgnoreCase))
            {
                args[i] = "inassembly:" + assemblyName;
                replaced = true;
                break;
            }
        }
        term = replaced
            ? CommandLineTools.ArgumentArrayToCommandLine(args)
            : (string.IsNullOrEmpty(term) ? "inassembly:" + assemblyName : term + " inassembly:" + assemblyName);
        _searchBox.Text = term;
        StartSearch(term);
    }

    // Focus the search box (called by parent when the pane becomes active)
    public void FocusSearchBox()
        => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => {
            _searchBox.Focus(FocusState.Programmatic);
            _searchBox.SelectAll();
        });

    // ── RunningSearch — ported verbatim from WPF SearchPane.xaml.cs ──

    sealed class RunningSearch
    {
        readonly CancellationTokenSource _cts = new();
        readonly IList<LoadedAssembly> _assemblies;
        readonly SearchRequest _searchRequest;
        readonly SearchMode _searchMode;
        readonly Language _language;
        readonly LanguageVersion? _languageVersion;
        readonly ApiVisibility _apiVisibility;
        readonly ITreeNodeFactory _treeNodeFactory;
        readonly SettingsService _settingsService;

        public IProducerConsumerCollection<SearchResult> ResultQueue { get; } = new ConcurrentQueue<SearchResult>();

        public RunningSearch(
            IList<LoadedAssembly> assemblies,
            string searchTerm,
            SearchMode searchMode,
            Language language,
            LanguageVersion? languageVersion,
            ITreeNodeFactory treeNodeFactory,
            SettingsService settingsService)
        {
            _assemblies     = assemblies;
            _language       = language;
            _languageVersion = languageVersion;
            _searchMode     = searchMode;
            _apiVisibility  = settingsService.SessionSettings.LanguageSettings.ShowApiLevel;
            _treeNodeFactory = treeNodeFactory;
            _settingsService = settingsService;
            _searchRequest  = Parse(searchTerm);
        }

        SearchRequest Parse(string input)
        {
            string[] parts = CommandLineTools.CommandLineToArgumentArray(input);

            var request  = new SearchRequest();
            var keywords = new List<string>();
            Regex?  regex = null;
            request.Mode  = _searchMode;

            foreach (string part in parts)
            {
                int prefixLength = part.IndexOfAny(['"', '/']);
                if (prefixLength < 0) prefixLength = part.Length;

                int delimiterLength;
                if (part.StartsWith('@'))
                {
                    prefixLength    = 1;
                    delimiterLength = 0;
                }
                else
                {
                    prefixLength    = part.IndexOf(':', 0, prefixLength);
                    delimiterLength = 1;
                }

                string? prefix;
                if (prefixLength <= 0)
                {
                    prefix       = null;
                    prefixLength = -1;
                }
                else
                {
                    prefix = part[..prefixLength];
                }

                string searchTerm = part[(prefixLength + delimiterLength)..].Trim();
                if (searchTerm.Length > 0)
                    searchTerm = CommandLineTools.CommandLineToArgumentArray(searchTerm)[0];
                else
                {
                    searchTerm = part;
                    prefix     = null;
                }

                if (prefix == null || prefix.Length <= 2)
                {
                    if (regex == null && searchTerm.StartsWith('/') && searchTerm.Length > 1)
                    {
                        int len = searchTerm.Length - 1;
                        if (searchTerm.EndsWith('/')) len--;
                        request.FullNameSearch |= searchTerm.Contains("\\.");
                        regex = CreateRegex(searchTerm[1..(len + 1)]);
                    }
                    else
                    {
                        request.FullNameSearch |= searchTerm.Contains('.');
                        keywords.Add(searchTerm);
                    }
                    request.OmitGenerics |= !(searchTerm.Contains('<') || searchTerm.Contains('`'));
                }

                switch (prefix?.ToUpperInvariant())
                {
                    case "@":          request.Mode = SearchMode.Token; break;
                    case "INNAMESPACE": request.InNamespace ??= searchTerm; break;
                    case "INASSEMBLY":  request.InAssembly  ??= searchTerm; break;
                    case "A":  request.AssemblySearchKind = AssemblySearchKind.NameOrFileName; request.Mode = SearchMode.Assembly; break;
                    case "AF": request.AssemblySearchKind = AssemblySearchKind.FilePath;       request.Mode = SearchMode.Assembly; break;
                    case "AN": request.AssemblySearchKind = AssemblySearchKind.FullName;       request.Mode = SearchMode.Assembly; break;
                    case "N":  request.Mode = SearchMode.Namespace; break;
                    case "TM": request.Mode = SearchMode.Member;  request.MemberSearchKind = MemberSearchKind.All;      break;
                    case "T":  request.Mode = SearchMode.Member;  request.MemberSearchKind = MemberSearchKind.Type;     break;
                    case "M":  request.Mode = SearchMode.Member;  request.MemberSearchKind = MemberSearchKind.Member;   break;
                    case "MD": request.Mode = SearchMode.Member;  request.MemberSearchKind = MemberSearchKind.Method;   break;
                    case "F":  request.Mode = SearchMode.Member;  request.MemberSearchKind = MemberSearchKind.Field;    break;
                    case "P":  request.Mode = SearchMode.Member;  request.MemberSearchKind = MemberSearchKind.Property; break;
                    case "E":  request.Mode = SearchMode.Member;  request.MemberSearchKind = MemberSearchKind.Event;    break;
                    case "C":  request.Mode = SearchMode.Literal; break;
                    case "R":  request.Mode = SearchMode.Resource; break;
                }
            }

            request.Keywords           = [.. keywords];
            request.RegEx              = regex;
            request.SearchResultFactory = new RomaSearchResultFactory(_language);
            request.TreeNodeFactory     = _treeNodeFactory;

            var decompilerSettings = _settingsService.DecompilerSettings.Clone();
            if (!Enum.TryParse(_languageVersion?.Version, out ICSharpCode.Decompiler.CSharp.LanguageVersion langVersion))
                langVersion = ICSharpCode.Decompiler.CSharp.LanguageVersion.Latest;
            decompilerSettings.SetLanguageVersion(langVersion);
            request.DecompilerSettings = decompilerSettings;

            return request;
        }

        static Regex? CreateRegex(string s)
        {
            try   { return new Regex(s, RegexOptions.Compiled); }
            catch { return null; }
        }

        public void Cancel() => _cts.Cancel();

        public async Task Run()
        {
            try
            {
                await Task.Factory.StartNew(() => {
                    var searcher = GetSearchStrategy(_searchRequest);
                    if (searcher is null) return;
                    try
                    {
                        foreach (var asm in _assemblies)
                        {
                            var module = asm.GetMetadataFileOrNull();
                            if (module is null) continue;
                            searcher.Search(module, _cts.Token);
                        }
                    }
                    catch (OperationCanceledException) { }
                }, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Current)
                .ConfigureAwait(false);
            }
            catch (TaskCanceledException) { }
        }

        AbstractSearchStrategy? GetSearchStrategy(SearchRequest request)
        {
            if (request.Keywords.Length == 0 && request.RegEx is null)
                return null;

            return request.Mode switch
            {
                SearchMode.TypeAndMember => new MemberSearchStrategy(_language, _apiVisibility, request, ResultQueue),
                SearchMode.Type          => new MemberSearchStrategy(_language, _apiVisibility, request, ResultQueue, MemberSearchKind.Type),
                SearchMode.Member        => new MemberSearchStrategy(_language, _apiVisibility, request, ResultQueue, request.MemberSearchKind),
                SearchMode.Literal       => new LiteralSearchStrategy(_language, _apiVisibility, request, ResultQueue),
                SearchMode.Method        => new MemberSearchStrategy(_language, _apiVisibility, request, ResultQueue, MemberSearchKind.Method),
                SearchMode.Field         => new MemberSearchStrategy(_language, _apiVisibility, request, ResultQueue, MemberSearchKind.Field),
                SearchMode.Property      => new MemberSearchStrategy(_language, _apiVisibility, request, ResultQueue, MemberSearchKind.Property),
                SearchMode.Event         => new MemberSearchStrategy(_language, _apiVisibility, request, ResultQueue, MemberSearchKind.Event),
                SearchMode.Token         => new MetadataTokenSearchStrategy(_language, _apiVisibility, request, ResultQueue),
                SearchMode.Resource      => new ResourceSearchStrategy(_apiVisibility, request, ResultQueue),
                SearchMode.Assembly      => new AssemblySearchStrategy(request, ResultQueue, AssemblySearchKind.NameOrFileName),
                SearchMode.Namespace     => new NamespaceSearchStrategy(request, ResultQueue),
                _                        => null,
            };
        }
    }
}
