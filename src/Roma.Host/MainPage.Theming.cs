using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using UnoEdit.Skia.Desktop.Controls;

using ILSpyResources = ICSharpCode.ILSpy.Properties.Resources;

namespace Roma.Host;

// View ▸ Theme and View ▸ UI Language menus. Themes mirror ILSpy's ThemeManager.AllThemes (the same
// six names + dark/light classification); applying maps to the WinUI element theme + UnoEdit editor
// theme and persists to SessionSettings.Theme. Localization wires the UI culture through
// SessionSettings.CurrentCulture + CultureInfo + Resources.Culture (the same mechanism ILSpy uses in
// App startup), so strings sourced from Properties.Resources localize once translations are present.
public sealed partial class MainPage
{
    // ILSpy ThemeManager.AllThemes, with each theme's dark/light classification.
    private static readonly (string Name, bool IsDark)[] RomaThemes =
    [
        ("Light", false),
        ("Dark", true),
        ("VS Code Light+", false),
        ("VS Code Dark+", true),
        ("R# Light", false),
        ("R# Dark", true),
    ];

    private void InitializeThemingAndLocalization()
    {
        PopulateThemeMenu();
        PopulateUiLanguageMenu();
    }

    [LeXtudio.DevFlow.Agent.Core.DevFlowAction("roma.apply-theme", Description = "Applies a theme by name (diagnostic).")]
    public static string ApplyThemeByName(string name)
    {
        var page = _current;
        if (page is null) return "MainPage not available.";
        string result = string.Empty;
        using var done = new System.Threading.ManualResetEventSlim();
        page.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var def = RomaThemes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                if (def.Name is null) { result = $"unknown theme '{name}'"; return; }
                page.ApplyTheme(def.Name, def.IsDark);
                result = $"applied '{def.Name}' isDark={def.IsDark}";
            }
            catch (Exception ex) { result = ex.ToString(); }
            finally { done.Set(); }
        });
        done.Wait(5000);
        return result;
    }

    [LeXtudio.DevFlow.Agent.Core.DevFlowAction("roma.theme-state", Description = "Reports Roma theme state for the root, menu, toolbar, dock, and editor.")]
    public static string GetThemeState()
    {
        var page = _current;
        if (page is null) return "MainPage not available.";
        string result = string.Empty;
        using var done = new System.Threading.ManualResetEventSlim();
        page.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var root = page.XamlRoot?.Content as FrameworkElement;
                result =
                    $"sessionTheme={page._assemblyContext.SettingsService.SessionSettings.Theme ?? string.Empty}\n" +
                    $"themeManagerIsDark={ICSharpCode.ILSpy.Themes.ThemeManager.Current.IsDarkTheme}\n" +
                    $"imagesIsDark={ICSharpCode.ILSpy.Images.IsDark}\n" +
                    $"rootRequestedTheme={root?.RequestedTheme.ToString() ?? string.Empty}\n" +
                    $"pageRequestedTheme={page.RequestedTheme}\n" +
                    $"menuRequestedTheme={page._menuBar.RequestedTheme}\n" +
                    $"menuActualTheme={page._menuBar.ActualTheme}\n" +
                    $"toolbarRequestedTheme={page._toolBar.RequestedTheme}\n" +
                    $"toolbarActualTheme={page._toolBar.ActualTheme}\n" +
                    $"dockActualTheme={page.DockManager.ActualTheme}\n" +
                    $"menuBackground={BrushToString(page._menuBar.Background)}\n" +
                    $"toolbarBackground={BrushToString(page._toolBar.Background)}";
            }
            catch (Exception ex) { result = ex.ToString(); }
            finally { done.Set(); }
        });
        done.Wait(5000);
        return result;
    }

    // ── Theme ───────────────────────────────────────────────────────────────
    private void PopulateThemeMenu()
    {
        var current = _assemblyContext.SettingsService.SessionSettings.Theme;
        if (string.IsNullOrEmpty(current) || RomaThemes.All(t => t.Name != current))
            current = "Light";

        _themeMenu.Items.Clear();
        foreach (var (name, isDark) in RomaThemes)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = name,
                GroupName = "RomaTheme",
                IsChecked = name == current,
            };
            var capturedName = name;
            var capturedDark = isDark;
            item.Click += (_, _) => ApplyTheme(capturedName, capturedDark);
            _themeMenu.Items.Add(item);
        }

        var def = RomaThemes.First(t => t.Name == current);
        ApplyTheme(def.Name, def.IsDark);
    }

    internal static bool IsDarkTheme(string? themeName)
        => !string.IsNullOrEmpty(themeName) && RomaThemes.FirstOrDefault(t => t.Name == themeName).IsDark;

    private void ApplyTheme(string name, bool isDark)
    {
        // Switch the tree/menu icon glyphs to the matching SVG variant. Existing icons were resolved
        // when the tree was built, so rebuild the assembly browser content when the icon theme flips.
        bool iconThemeFlipped = ICSharpCode.ILSpy.Images.IsDark != isDark;
        ICSharpCode.ILSpy.Images.IsDark = isDark;
        // Load the named theme's SyntaxColor palette (the real ILSpy ThemeManager). This is what gives
        // each of the six themes its own distinct syntax colors instead of a single light/dark pair —
        // the setter calls UpdateTheme, which merges Themes/Theme.<name>.uno.xaml and records IsDarkTheme.
        ICSharpCode.ILSpy.Themes.ThemeManager.Current.Theme = name;
        // Push the loaded palette onto the C# highlighting definition (marks it theme-aware), so the
        // editor's ThemeAwareHighlightingColorizer renders the theme colors directly.
        var csharpDef = ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinitionByExtension(".cs");
        if (csharpDef is not null)
            ICSharpCode.ILSpy.Themes.ThemeManager.Current.ApplyHighlightingColors(csharpDef);
        if (iconThemeFlipped && _browserPaneModel is not null)
            _browserPaneModel.Content = CreateAssemblyBrowserContent();

        ApplyElementTheme(isDark);
        _assemblyContext.SettingsService.SessionSettings.Theme = name;

        // Swap the AvalonDock chrome theme so the docking panes / tab strip / splitters follow the
        // light/dark choice (the WinUI ElementTheme alone doesn't restyle the VS2013 dock resources).
        DockManager.Theme = isDark
            ? new UnoDock.Themes.VS2013.Vs2013DarkTheme()
            : new UnoDock.Themes.VS2013.Vs2013LightTheme();

        // Source the editor surface color from the ILSpy theme just loaded (ThemeManager merged
        // Theme.<name>.uno.xaml into Application resources). This restores per-theme backgrounds
        // like the Light theme's signature pale-yellow (SystemColors.InfoColor) instead of the
        // editor's hardcoded white. Falls back to the stock light/dark theme if the key is absent.
        var editorTheme = isDark ? TextEditorTheme.Dark : TextEditorTheme.Light;
        if (TryGetThemeBackground(out var bg))
            editorTheme = editorTheme.WithBackground(bg);

        // Remember the active editor theme so tab content created later (a new tab, or the initial
        // tab whose DataTemplate materializes after this runs at startup) can adopt it. Unlike
        // ILSpy — whose editor Background is a DynamicResource that auto-follows the merged theme
        // dictionary — UnoEdit's editor takes the palette imperatively, so newly-created editors
        // would otherwise default to Light and show a light surface under a dark theme.
        _currentEditorTheme = editorTheme;

        foreach (var content in _contentByModel.Values)
        {
            content.CodeEditor.Theme = editorTheme;
            content.Decompiler?.SetEditorTheme(editorTheme);
        }

        foreach (var item in _themeMenu.Items.OfType<RadioMenuFlyoutItem>())
            item.IsChecked = item.Text == name;

        // Re-render the open tab so its already-decompiled content adopts the new theme. The editor
        // chrome restyles immediately, but the decompiled text (syntax colors baked at render time)
        // must be re-emitted — otherwise switching theme leaves the current view in the old colors.
        // Deferred to low priority: applying the editor Theme above schedules a relayout that clears
        // the document, so re-decompiling synchronously here would be clobbered by that pass. Running
        // after it settles ensures the fresh decompile output survives.
        if (_lastSelectedNode is { } selected)
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => OnTreeNodeSelected(selected, recordHistory: false));
    }

    // The active theme's editor-surface color. Roma does NOT merge the ILSpy theme dictionary into
    // Application.Resources (ThemeManager's app-wide merge is compiled out under ROMA_UNO), so the
    // brush can't be resolved from app resources. ThemeManager already reads ILSpy.TextBackground
    // from the loaded theme file (for its dark/light detection) and exposes it here — that is the
    // single source of truth, mirroring ILSpy where the editor binds the same brush by DynamicResource.
    private static bool TryGetThemeBackground(out Windows.UI.Color color)
    {
        var brush = ICSharpCode.ILSpy.Themes.ThemeManager.Current.TextBackgroundBrush;
        color = brush?.Color ?? default;
        return brush is not null;
    }

    // The editor palette for the active theme, applied to tab content as it is created
    // (see MainPage.Tabs.RegisterTabContent). Defaults to Light until the first ApplyTheme.
    private TextEditorTheme _currentEditorTheme = TextEditorTheme.Light;

    private void ApplyElementTheme(bool isDark)
    {
        var elementTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;

        // Runtime theme switching in Uno must be applied at the window root so ThemeResource brushes
        // re-resolve across the whole visual tree. During startup XamlRoot is not guaranteed to be
        // ready when InitializeComponent finishes, so also set the known top-level controls directly.
        if (XamlRoot?.Content is FrameworkElement windowRoot)
            windowRoot.RequestedTheme = elementTheme;

        RequestedTheme = elementTheme;
        _menuBar.RequestedTheme = elementTheme;
        _toolBar.RequestedTheme = elementTheme;
        DockManager.RequestedTheme = elementTheme;
    }

    private void ReapplyPersistedTheme()
    {
        var current = _assemblyContext.SettingsService.SessionSettings.Theme;
        if (string.IsNullOrEmpty(current) || RomaThemes.All(t => t.Name != current))
            current = "Light";
        var def = RomaThemes.First(t => t.Name == current);
        ApplyTheme(def.Name, def.IsDark);
    }

    // ── UI Language (localization) ──────────────────────────────────────────
    private void PopulateUiLanguageMenu()
    {
        var current = _assemblyContext.SettingsService.SessionSettings.CurrentCulture;

        _uiLanguageMenu.Items.Clear();
        AddCultureItem("System Default", null, string.IsNullOrEmpty(current));
        foreach (var culture in AvailableUiCultures())
            AddCultureItem(
                $"{culture.NativeName} ({culture.Name})",
                culture.Name,
                string.Equals(current, culture.Name, StringComparison.OrdinalIgnoreCase));

        ApplyCulture(current, persist: false);
    }

    private void AddCultureItem(string label, string? culture, bool isChecked)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = label,
            GroupName = "RomaUiLang",
            IsChecked = isChecked,
            Tag = culture,
        };
        item.Click += (_, _) => ApplyCulture(culture, persist: true);
        _uiLanguageMenu.Items.Add(item);
    }

    // English (neutral) is always available; probe common cultures for satellite resource sets so the
    // menu auto-grows when translated resources are added to the build.
    private static List<CultureInfo> AvailableUiCultures()
    {
        var result = new List<CultureInfo> { new("en") };

        var manager = ILSpyResources.ResourceManager;
        foreach (var name in new[] { "de", "fr", "es", "it", "ja", "ko", "zh-Hans", "zh-Hant", "ru", "pt-BR", "cs", "pl" })
        {
            CultureInfo culture;
            try { culture = new CultureInfo(name); }
            catch (CultureNotFoundException) { continue; }

            try
            {
                if (manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false) is not null)
                    result.Add(culture);
            }
            catch { /* no satellite for this culture */ }
        }

        return result;
    }

    private void ApplyCulture(string? culture, bool persist = true)
    {
        if (persist)
            _assemblyContext.SettingsService.SessionSettings.CurrentCulture = string.IsNullOrEmpty(culture) ? null : culture;

        var uiCulture = string.IsNullOrEmpty(culture) ? CultureInfo.InstalledUICulture : new CultureInfo(culture);
        CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
        System.Threading.Thread.CurrentThread.CurrentUICulture = uiCulture;
        ILSpyResources.Culture = string.IsNullOrEmpty(culture) ? null : uiCulture;

        foreach (var item in _uiLanguageMenu.Items.OfType<RadioMenuFlyoutItem>())
            item.IsChecked = string.Equals(item.Tag as string, culture, StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(item.Tag as string) && string.IsNullOrEmpty(culture));
    }
}
