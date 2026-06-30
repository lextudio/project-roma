#if DEBUG
using System;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Threading;

using LeXtudio.DevFlow.Agent.Core;

namespace Roma.Host;

// Stable DevFlow probe surface that the Roma.IntegrationTests xUnit suite drives over the DevFlow
// REST API (http://localhost:9223/api/v1/invoke/actions/<name>). Unlike the throwaway "diag-*"
// actions used during interactive debugging, these are a *contract*: each returns a small JSON
// snapshot so tests can assert on concrete state. Keep them side-effect-explicit and read-only
// unless the name says otherwise.
//
// Compiled only in Debug, so the probe surface never ships in Release.
public sealed partial class MainPage
{
    // Read-only snapshot of the assembly tree + active document. Tests poll this after an action
    // because some effects (decompile, the reset-to-New-Tab marshal) complete asynchronously.
    [DevFlowAction("roma.probe.state", Description = "PROBE: assembly tree + document snapshot as JSON.")]
    public static string ProbeState() => RunOnUi(page => Snapshot(page));

    // Exercises the real GitHub release update check (lextudio/project-roma) and reports the
    // latest version + download URL + current app version + whether an update is available.
    [DevFlowAction("roma.probe.check-update", Description = "PROBE: query project-roma GitHub releases; returns version comparison JSON.")]
    public static string ProbeCheckUpdate()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        var current = ICSharpCode.ILSpy.Updates.AppUpdateService.CurrentVersion;
        sb.Append($"\"current\":{Json(current.ToString())},");
        try
        {
            var info = ICSharpCode.ILSpy.Updates.UpdateService.GetLatestVersionAsync().GetAwaiter().GetResult();
            sb.Append($"\"latest\":{Json(info.Version?.ToString())},");
            sb.Append($"\"downloadUrl\":{Json(info.DownloadUrl)},");
            sb.Append($"\"updateAvailable\":{(info.Version > current ? "true" : "false")}");
        }
        catch (Exception ex)
        {
            sb.Append($"\"error\":{Json(ex.Message)}");
        }
        sb.Append('}');
        return sb.ToString();
    }

    // Verifies the Roma host settings (terminal preference) round-trip and the RecentFontsCache.
    [DevFlowAction("roma.probe.host-settings", Description = "PROBE: round-trip RomaHostSettings terminal pref + RecentFontsCache.")]
    public static string ProbeHostSettings() => RunOnUi(page =>
    {
        var settings = page._assemblyContext.SettingsService.GetSettings<RomaHostSettings>();

        // Round-trip the terminal preference.
        var original = settings.PreferredTerminalApp;
        settings.PreferredTerminalApp = "iTerm2";
        var readBack = settings.PreferredTerminalApp;
        settings.PreferredTerminalApp = original;

        // RecentFontsCache round-trip.
        var updated = RecentFontsCache.Update("RomaProbeFont");
        var top = updated.Count > 0 ? updated[0] : null;

        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"terminalRoundTrip\":{(readBack == "iTerm2" ? "true" : "false")},");
        sb.Append($"\"recentFontsEnabled\":{(settings.RecentFontsEnabled ? "true" : "false")},");
        sb.Append($"\"recentFontTop\":{Json(top)},");
        sb.Append($"\"recentFontCachesIt\":{(top == "RomaProbeFont" ? "true" : "false")}");
        sb.Append('}');
        return sb.ToString();
    });

    // Opens an assembly by file path (File → Open equivalent) and returns the resulting state.
    [DevFlowAction("roma.probe.open", Description = "PROBE: open an assembly by path; returns state JSON.")]
    public static string ProbeOpen(string path) => RunOnUi(page =>
    {
        page._assemblyContext.AssemblyList.OpenAssembly(path);
        return Snapshot(page);
    });

    // Removes every assembly (the "user cleared the list" case) and returns the resulting state.
    [DevFlowAction("roma.probe.clear", Description = "PROBE: clear all assemblies; returns state JSON.")]
    public static string ProbeClear() => RunOnUi(page =>
    {
        page._assemblyContext.AssemblyList.Clear();
        return Snapshot(page);
    });

    // Selects the top-level row at the given index (drives the same path as a click → decompile)
    // and returns state. Decompilation is async, so the document fields settle on a later poll.
    [DevFlowAction("roma.probe.select-row", Description = "PROBE: select top-level row by index; returns state JSON.")]
    public static string ProbeSelectRow(int index) => RunOnUi(page =>
    {
        var tree = page._assemblyTree;
        if (tree is null || index < 0 || index >= tree.RootNodes.Count)
            return Snapshot(page, error: $"index {index} out of range (rows={tree?.RootNodes.Count ?? 0})");
        var tvn = tree.RootNodes[index];
        tree.SelectedNode = tvn;
        if (tree.NodeFor(tvn) is { } node)
        {
            tree.SetSelection(new[] { node });
            page.OnTreeNodeSelected(node);
        }
        return Snapshot(page);
    });

    // Deletes the currently selected top-level row (Del-key path) — which also auto-selects the
    // next sibling — and returns state.
    [DevFlowAction("roma.probe.delete-selected", Description = "PROBE: delete selected row; returns state JSON.")]
    public static string ProbeDeleteSelected() => RunOnUi(page =>
    {
        var deleted = page._assemblyTree?.SelectedItem?.Text?.ToString();
        var ok = page._assemblyTree?.DeleteSelectedNodes() ?? false;
        return Snapshot(page, note: $"\"deleted\":{Json(deleted)},\"ok\":{(ok ? "true" : "false")}");
    });

    // Navigates to an entity by its metadata token within the currently loaded assemblies. Exercises
    // the real navigation path: a NavigateToReferenceEventArgs on the MessageBus is handled by
    // AssemblyTreeModel.JumpToReference → JumpToReferenceAsync → SelectNode → the host's SelectedItems
    // observer renders it. assemblyPath: a loaded assembly; token: hex metadata token (e.g. "02000002").
    [DevFlowAction("roma.probe.navigate", Description = "PROBE: navigate to entity by assembly path + hex token; returns state JSON.")]
    public static string ProbeNavigate(string assemblyPath, string hexToken) => RunOnUi(page =>
    {
        if (!uint.TryParse(hexToken, System.Globalization.NumberStyles.HexNumber, null, out var rawToken))
            return Snapshot(page, error: $"invalid hex token '{hexToken}'");
        var file = page._assemblyContext.AssemblyList.GetAssemblies()
            .Select(a => a.GetMetadataFileOrNull())
            .FirstOrDefault(f => f is not null
                && string.Equals(f.FileName, assemblyPath, StringComparison.OrdinalIgnoreCase));
        if (file is null)
            return Snapshot(page, error: $"assembly not loaded: {assemblyPath}");
        var handle = System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle((int)rawToken);
        var entityRef = new ICSharpCode.ILSpy.EntityReference(file, handle, protocol: "decompile");
        ICSharpCode.ILSpy.Util.MessageBus.Send(page,
            new ICSharpCode.ILSpy.Util.NavigateToReferenceEventArgs(entityRef));
        return Snapshot(page);
    });

    // Reproduces session-restore tree reveal: finds a node deep in the tree by its display text
    // (FindNode materializes only the MODEL hierarchy, leaving the WinUI TreeViewNodes unexpanded —
    // exactly the restore precondition), then runs the real reveal path AssemblyTreeModel.SelectNode →
    // activeView.ScrollIntoView → SharpTreeView.EnsureVisible. Reports whether the target got a
    // TreeViewNode, became the tree's selection, and whether every ancestor ended up expanded.
    [DevFlowAction("roma.probe.reveal-node", Description = "PROBE: reveal a deep tree node by text (session-restore path); returns reveal JSON.")]
    public static string ProbeRevealNode(string assemblyPath, string nodeText) => RunOnUi(page =>
    {
        if (!page._assemblyContext.AssemblyList.GetAssemblies()
                .Select(a => a.GetMetadataFileOrNull())
                .Any(f => f is not null && string.Equals(f.FileName, assemblyPath, StringComparison.OrdinalIgnoreCase)))
        {
            page._assemblyContext.AssemblyList.OpenAssembly(assemblyPath);
        }

        var target = page.FindNode(page._assemblyContext.Root,
            n => string.Equals(n.Text?.ToString(), nodeText, StringComparison.Ordinal));
        if (target is null)
            return $"{{\"error\":{Json($"node not found: {nodeText}")}}}";

        var tree = page._assemblyTree;
        if (tree is null)
            return "{\"error\":\"tree not available\"}";

        var ancestors = target.Ancestors().Where(a => a.Parent is not null).ToList();
        var expandedBefore = ancestors.Count(a => tree.TreeViewNodeFor(a)?.IsExpanded == true);

        // The real restore call.
        page._assemblyTreeModel.SelectNode(target);

        var tvnExists = tree.TreeViewNodeFor(target) is not null;
        var expandedAfter = ancestors.Count(a => tree.TreeViewNodeFor(a)?.IsExpanded == true);
        var selectedText = tree.SelectedItem?.Text?.ToString();
        var isSelected = ReferenceEquals(tree.SelectedItem, target);

        return $"{{\"node\":{Json(nodeText)}," +
               $"\"ancestorCount\":{ancestors.Count}," +
               $"\"expandedBefore\":{expandedBefore}," +
               $"\"expandedAfter\":{expandedAfter}," +
               $"\"treeViewNodeRealized\":{(tvnExists ? "true" : "false")}," +
               $"\"selectedText\":{Json(selectedText)}," +
               $"\"isSelected\":{(isSelected ? "true" : "false")}," +
               $"\"revealed\":{(tvnExists && isSelected && expandedAfter == ancestors.Count ? "true" : "false")}}}";
    });

    // Shows the About page in the document area and returns state (documentTitle should be "About").
    [DevFlowAction("roma.probe.show-about", Description = "PROBE: show the About page; returns state JSON.")]
    public static string ProbeShowAbout() => RunOnUi(page =>
    {
        page.ShowAboutPage();
        return Snapshot(page);
    });

    // Drives the update banner UI directly (no network) so its wiring can be asserted deterministically.
    [DevFlowAction("roma.probe.update-banner", Description = "PROBE: show the update banner with a message; returns state JSON.")]
    public static string ProbeUpdateBanner(string message, bool withDownload) => RunOnUi(page =>
    {
        page.ShowUpdateBanner(message, withDownload ? "https://example.invalid/download" : null);
        return Snapshot(page);
    });

    // Reports the known assembly lists and the active one.
    [DevFlowAction("roma.probe.lists", Description = "PROBE: list known assembly lists + active list as JSON.")]
    public static string ProbeLists() => RunOnUi(page =>
    {
        var names = page._assemblyContext.AssemblyListManager.AssemblyLists;
        var arr = string.Join(",", names.Select(Json));
        return $"{{\"lists\":[{arr}],\"active\":{Json(page._assemblyContext.AssemblyList.ListName)}}}";
    });

    // Creates a preconfigured list (".NET 4 (WPF)"/".NET 3.5"/"ASP.NET (MVC3)"/"Uno Platform"); the
    // Uno Platform preset is cross-platform (current runtime dir). Returns the updated lists JSON.
    [DevFlowAction("roma.probe.create-preset", Description = "PROBE: create a preconfigured assembly list by key; returns lists JSON.")]
    public static string ProbeCreatePreset(string key) => RunOnUi(page =>
    {
        var manager = page._assemblyContext.AssemblyListManager;
        var list = RomaAssemblyListPresets.IsUnoPlatform(key)
            ? RomaAssemblyListPresets.CreateUnoPlatformList(manager)
            : manager.CreateDefaultList(key);
        manager.AddListIfNotExists(list);
        var names = manager.AssemblyLists;
        var arr = string.Join(",", names.Select(Json));
        return $"{{\"lists\":[{arr}],\"created\":{Json(key)},\"createdRows\":{list.GetAssemblies().Length}}}";
    });

    // Creates a new empty assembly list by name (the dialog's "New" action).
    [DevFlowAction("roma.probe.create-list", Description = "PROBE: create an empty assembly list by name; returns lists JSON.")]
    public static string ProbeCreateList(string name) => RunOnUi(page =>
    {
        var manager = page._assemblyContext.AssemblyListManager;
        manager.AddListIfNotExists(manager.CreateList(name));
        var arr = string.Join(",", manager.AssemblyLists.Select(Json));
        return $"{{\"lists\":[{arr}],\"created\":{Json(name)}}}";
    });

    // Switches the active assembly list (same path as the toolbar combo / dialog Open).
    [DevFlowAction("roma.probe.switch-list", Description = "PROBE: switch the active assembly list by name; returns state JSON.")]
    public static string ProbeSwitchList(string name) => RunOnUi(page =>
    {
        page.ReloadActiveAssemblyList(name);
        return Snapshot(page);
    });

    // Constructs the Manage Assembly Lists dialog (without showing it) to verify its wiring — the
    // ListView binding and preset flyout build without throwing. Reports the preset count.
    [DevFlowAction("roma.probe.manage-dialog-builds", Description = "PROBE: construct the Manage Assembly Lists dialog; returns JSON.")]
    public static string ProbeManageDialogBuilds() => RunOnUi(page =>
    {
        var dialog = new Options.ManageAssemblyListsDialog(
            page._assemblyContext.AssemblyListManager, _ => { });
        return $"{{\"built\":true,\"presetCount\":{dialog.PresetCount}}}";
    });

    // Opens an assembly if needed, selects a metadata table node, and reports the live DataGrid
    // produced by the real ILSpy MetadataTableTreeNode.View() -> Helpers.PrepareDataGrid path.
    [DevFlowAction("roma.probe.metadata-open-table", Description = "PROBE: open/select metadata table; returns DataGrid snapshot JSON.")]
    public static string ProbeMetadataOpenTable(string assemblyPath, string tableName) => RunOnUi(page =>
    {
        if (!page._assemblyContext.AssemblyList.GetAssemblies()
                .Select(a => a.GetMetadataFileOrNull())
                .Any(f => f is not null && string.Equals(f.FileName, assemblyPath, StringComparison.OrdinalIgnoreCase)))
        {
            page._assemblyContext.AssemblyList.OpenAssembly(assemblyPath);
        }

        if (!Enum.TryParse<TableIndex>(tableName, ignoreCase: true, out var table))
            return Snapshot(page, error: $"unknown metadata table '{tableName}'");

        var target = page.FindNode(page._assemblyContext.Root, node =>
            node is ICSharpCode.ILSpy.Metadata.MetadataTableTreeNode tableNode
            && tableNode.Kind == table);
        if (target is null)
            return Snapshot(page, error: $"metadata table '{table}' not found");

        page.OnTreeNodeSelected(target);
        return MetadataGridSnapshot(page, target.Text?.ToString(), table.ToString());
    });

    [DevFlowAction("roma.probe.metadata-hex-filter", Description = "PROBE: open a metadata table, trigger a HEX column filter control, and return filtered row state.")]
    public static string ProbeMetadataHexFilter(string assemblyPath, string tableName) => RunOnUi(page =>
    {
        var grid = OpenMetadataGrid(page, assemblyPath, tableName, out var error);
        if (grid is null)
            return MetadataHexFilterSnapshot(null, -1, null, null, 0, 0, 0, 0, 0, false, false, error ?? "metadata table did not render a DataGrid");

        grid.UpdateLayout();
        var before = RealizedRowItems(grid).Count;
        var hexColumnIndex = FirstHexFilterColumnIndex(grid);
        if (hexColumnIndex < 0)
            return MetadataHexFilterSnapshot(grid, -1, null, null, before, before, 0, 0, 0, false, false, "no HEX filter column found");

        var column = grid.Columns[hexColumnIndex];
        var bindingPath = column is System.Windows.Controls.DataGridBoundColumn boundColumn
            ? (boundColumn.Binding as System.Windows.Data.Binding)?.Path?.Path
            : null;
        if (string.IsNullOrEmpty(bindingPath))
            return MetadataHexFilterSnapshot(grid, hexColumnIndex, column.Header?.ToString(), null, before, before, 0, 0, 0, false, false, "HEX filter column has no binding path");

        var firstValue = grid.Items.Cast<object?>()
            .Select(item => ReadBindingValue(item, bindingPath))
            .FirstOrDefault(value => value is not null);
        if (firstValue is null)
            return MetadataHexFilterSnapshot(grid, hexColumnIndex, column.Header?.ToString(), null, before, before, 0, 0, 0, false, false, "HEX filter column has no values");

        var filterText = string.Format("{0:x8}", firstValue);
        var header = MetadataHeaderAt(grid, hexColumnIndex);
        if (header is null)
            return MetadataHexFilterSnapshot(grid, hexColumnIndex, column.Header?.ToString(), filterText, before, before, 0, 0, 0, false, false, "metadata header was not realized");

        var filterButton = FindDescendant<Microsoft.UI.Xaml.Controls.Button>(header.Content);
        if (filterButton is null)
            return MetadataHexFilterSnapshot(grid, hexColumnIndex, column.Header?.ToString(), filterText, before, before, 0, 0, 0, false, false, "HEX filter button was not realized");

        var clicked = InvokeButtonClick(filterButton);
        var textBox = FindDescendant<Microsoft.UI.Xaml.Controls.TextBox>(header.Content);
        if (textBox is null)
            return MetadataHexFilterSnapshot(grid, hexColumnIndex, column.Header?.ToString(), filterText, before, before, 0, 0, 0, clicked, false, "HEX filter text box was not realized");

        var inputVisible = textBox.Visibility == Microsoft.UI.Xaml.Visibility.Visible
            && IsAncestorVisible(textBox);
        var textEntered = SetTextBoxTextThroughAutomation(textBox, filterText);
        if (textBox.Tag is Action applyFilter)
        {
            applyFilter();
        }

        grid.UpdateLayout();

        var filteredItems = RealizedRowItems(grid);
        var after = filteredItems.Count;
        var activeFilters = ActiveColumnFilterCount(grid);
        var filterMatches = grid.Items.Cast<object?>()
            .Count(item => DataGridExtensions.DataGridFilter.MatchesAllFilters(grid, item));
        var matching = filteredItems.Count(item =>
        {
            var value = ReadBindingValue(item, bindingPath);
            return value is not null
                && string.Format("{0:x8}", value).IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
        });

        return MetadataHexFilterSnapshot(
            grid,
            hexColumnIndex,
            column.Header?.ToString(),
            filterText,
            before,
            after,
            matching,
            activeFilters,
            filterMatches,
            clicked,
            inputVisible && textEntered,
            null);
    });

    [DevFlowAction("roma.probe.metadata-hex-filter-typing", Description = "PROBE: verifies HEX filter TextBox survives user typing (TextChanged) without being destroyed by a grid rebuild.")]
    public static string ProbeMetadataHexFilterTyping(string assemblyPath, string tableName) => RunOnUi(page =>
    {
        var grid = OpenMetadataGrid(page, assemblyPath, tableName, out var error);
        if (grid is null)
            return MetadataHexFilterTypingSnapshot(false, false, false, false, error ?? "metadata table did not render a DataGrid");

        grid.UpdateLayout();

        var hexColumnIndex = FirstHexFilterColumnIndex(grid);
        if (hexColumnIndex < 0)
            return MetadataHexFilterTypingSnapshot(false, false, false, false, "no HEX filter column found");

        var column = grid.Columns[hexColumnIndex];
        var bindingPath = column is System.Windows.Controls.DataGridBoundColumn bc
            ? (bc.Binding as System.Windows.Data.Binding)?.Path?.Path
            : null;
        var firstValue = string.IsNullOrEmpty(bindingPath) ? null
            : grid.Items.Cast<object?>()
                .Select(item => ReadBindingValue(item, bindingPath))
                .FirstOrDefault(v => v is not null);

        // Step 1 — open the filter panel and get the TextBox.
        var header = MetadataHeaderAt(grid, hexColumnIndex);
        var filterButton = header is null ? null : FindDescendant<Microsoft.UI.Xaml.Controls.Button>(header.Content);
        if (filterButton is not null)
            InvokeButtonClick(filterButton);

        var textBox = header is null ? null : FindDescendant<Microsoft.UI.Xaml.Controls.TextBox>(header.Content);
        if (textBox is null)
            return MetadataHexFilterTypingSnapshot(false, false, false, false, "HEX filter text box was not realized before typing");

        // Step 2 — simulate typing one character via TextChanged.
        // In the OLD code this called BuildShimVisualTree(), destroying the TextBox.
        // In the FIXED code it calls RefreshFilteredRows(), leaving the header intact.
        var firstChar = firstValue is not null ? string.Format("{0:x8}", firstValue)[0].ToString() : "0";
        SetTextBoxTextThroughAutomation(textBox, firstChar);

        // Step 3 — re-fetch the header and look for a TextBox.
        // After RefreshFilteredRows the header is still there; after BuildShimVisualTree it is gone.
        var headerAfter = MetadataHeaderAt(grid, hexColumnIndex);
        var textBoxAfter = headerAfter is null ? null
            : FindDescendant<Microsoft.UI.Xaml.Controls.TextBox>(headerAfter.Content);

        var textBoxSurvived = textBoxAfter is not null;
        var textPreserved   = textBoxAfter is not null && textBoxAfter.Text == firstChar;
        var filterActive    = DataGridExtensions.DataGridFilter.GetIsAutoFilterEnabled(grid)
                              && ActiveColumnFilterCount(grid) > 0;
        // Original TextBox reference should still be in the live tree (same object, not replaced).
        var sameInstance    = ReferenceEquals(textBoxAfter, textBox);

        return MetadataHexFilterTypingSnapshot(textBoxSurvived, textPreserved, filterActive, sameInstance, null);
    });

    static string MetadataHexFilterTypingSnapshot(
        bool textBoxSurvived,
        bool textPreserved,
        bool filterActive,
        bool sameInstance,
        string? error)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"textBoxSurvived\":{(textBoxSurvived ? "true" : "false")},");
        sb.Append($"\"textPreserved\":{(textPreserved ? "true" : "false")},");
        sb.Append($"\"filterActive\":{(filterActive ? "true" : "false")},");
        sb.Append($"\"sameInstance\":{(sameInstance ? "true" : "false")}");
        if (error is not null) sb.Append(',').Append($"\"error\":{Json(error)}");
        sb.Append('}');
        return sb.ToString();
    }

    // ── HEX-filter probe helpers ──────────────────────────────────────────────
    // Reconstructed support for ProbeMetadataHexFilter / ProbeMetadataHexFilterTyping.
    // Visual-tree + automation helpers stay generic; filter-state inspection uses the
    // same reflection approach the resize probes use for the shim's non-public members.

    // First column whose DataGridExtensions filter editor is a HEX control, else -1.
    static int FirstHexFilterColumnIndex(System.Windows.Controls.DataGrid grid)
    {
        for (var i = 0; i < grid.Columns.Count; i++)
        {
            if (DataGridExtensions.DataGridFilterColumn.GetTemplate(grid.Columns[i])
                    is DataGridExtensions.FilterControlTemplate template
                && template.Kind == DataGridExtensions.FilterKind.Hex)
            {
                return i;
            }
        }

        return -1;
    }

    // Items backing the DataGridRow containers currently realized in the visual tree
    // (post-filter), in display order.
    static System.Collections.Generic.List<object?> RealizedRowItems(System.Windows.Controls.DataGrid grid)
    {
        var items = new System.Collections.Generic.List<object?>();
        foreach (var row in FindDescendants<System.Windows.Controls.DataGridRow>(grid))
        {
            items.Add(row.Item);
        }

        return items;
    }

    // Number of columns with an active content filter. The filter state lives in the
    // shim's internal DataGridFilter.State.ColumnFilters; reflect into it (Roma.Host is
    // not in LeXtudio.Windows' InternalsVisibleTo list).
    static int ActiveColumnFilterCount(System.Windows.Controls.DataGrid grid)
    {
        var getState = typeof(DataGridExtensions.DataGridFilter).GetMethod(
            "GetState",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var state = getState?.Invoke(null, [grid]);
        if (state is null)
            return 0;

        var field = state.GetType().GetField(
            "ColumnFilters",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(state) is not System.Collections.IDictionary filters)
            return 0;

        var count = 0;
        foreach (var value in filters.Values)
        {
            if (value is not null)
                count++;
        }

        return count;
    }

    // First visual descendant of type T (including the root itself), depth-first.
    static T? FindDescendant<T>(object? root) where T : class
    {
        if (root is not Microsoft.UI.Xaml.DependencyObject start)
            return null;
        if (start is T self)
            return self;
        return FindDescendants<T>(start).FirstOrDefault();
    }

    // All visual descendants of type T (excluding the root), depth-first.
    static System.Collections.Generic.IEnumerable<T> FindDescendants<T>(Microsoft.UI.Xaml.DependencyObject? root)
        where T : class
    {
        if (root is null)
            yield break;

        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }

    // True if no ancestor (or the element itself) is collapsed.
    static bool IsAncestorVisible(Microsoft.UI.Xaml.FrameworkElement element)
    {
        Microsoft.UI.Xaml.DependencyObject? current = element;
        while (current is not null)
        {
            if (current is Microsoft.UI.Xaml.UIElement ue
                && ue.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed)
            {
                return false;
            }

            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }

        return true;
    }

    // Invoke a Button the way the UI automation layer would (raises Click).
    static bool InvokeButtonClick(Microsoft.UI.Xaml.Controls.Button button)
    {
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(button);
        if (peer?.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)
                is Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider invoke)
        {
            invoke.Invoke();
            return true;
        }

        return false;
    }

    // Set a TextBox's text the way real typing would, so the TextChanged handler fires.
    static bool SetTextBoxTextThroughAutomation(Microsoft.UI.Xaml.Controls.TextBox textBox, string text)
    {
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(textBox);
        if (peer?.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Value)
                is Microsoft.UI.Xaml.Automation.Provider.IValueProvider value)
        {
            value.SetValue(text);
            return true;
        }

        textBox.Text = text;
        return true;
    }

    static string MetadataHexFilterSnapshot(
        System.Windows.Controls.DataGrid? grid,
        int columnIndex,
        string? header,
        string? filterText,
        int before,
        int after,
        int matching,
        int activeFilters,
        int filterMatches,
        bool buttonClicked,
        bool inputApplied,
        string? error)
    {
        _ = grid;
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"rendered\":{(grid is not null ? "true" : "false")},");
        sb.Append($"\"columnIndex\":{columnIndex},");
        sb.Append($"\"header\":{Json(header)},");
        sb.Append($"\"filterText\":{Json(filterText)},");
        sb.Append($"\"before\":{before},");
        sb.Append($"\"after\":{after},");
        sb.Append($"\"matching\":{matching},");
        sb.Append($"\"activeFilters\":{activeFilters},");
        sb.Append($"\"filterMatches\":{filterMatches},");
        sb.Append($"\"buttonClicked\":{(buttonClicked ? "true" : "false")},");
        sb.Append($"\"inputApplied\":{(inputApplied ? "true" : "false")}");
        if (error is not null) sb.Append(',').Append($"\"error\":{Json(error)}");
        sb.Append('}');
        return sb.ToString();
    }

    // ── Row virtualization probe (Session 119, Slice 5) ───────────────────────
    // Opens a (large) metadata table, switches the DataGrid to the virtualized
    // DataGridRowsPresenter path, and reports how many rows are realized vs total —
    // the proof that only a viewport-sized window materializes. Then scrolls and
    // reports the window shifted.
    [DevFlowAction("roma.probe.metadata-virtualization", Description = "PROBE: enable row virtualization on a metadata table; report realized-vs-total row windowing.")]
    public static string ProbeMetadataVirtualization(string assemblyPath, string tableName) => RunOnUi(page =>
    {
        var grid = OpenMetadataGrid(page, assemblyPath, tableName, out var error);
        if (grid is null)
            return VirtualizationSnapshot(0, 0, 0, -1, -1, 0, 0, error ?? "metadata table did not render a DataGrid");

        grid.UpdateLayout();
        var total = grid.Items.Count;

        // Enable the virtualized DataGridRowsPresenter path (internal member; reflect in).
        var enable = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "ShimSetRowVirtualization",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var enabled = enable?.Invoke(grid, [true]) as bool? == true;
        if (!enabled)
            return VirtualizationSnapshot(total, 0, 0, -1, -1, 0, 0, "could not enable row virtualization");

        grid.UpdateLayout();

        var realizedInitial = RealizedVisibleRowCount(grid);
        var firstInitial = FirstRealizedRowIndex(grid);
        var headerCells = FindDescendants<System.Windows.Controls.Primitives.DataGridColumnHeader>(grid).Count();

        // Drive the realized window to the middle deterministically (the engine's
        // viewport seam), avoiding async scroll/viewport-callback timing. The window
        // should shift to rows around the middle index.
        var realizedAfter = realizedInitial;
        var firstAfter = firstInitial;
        var presenter = FindDescendant<System.Windows.Controls.Primitives.DataGridRowsPresenter>(grid);
        var extentReport = presenter?.DesiredSize.Height ?? 0d;
        if (presenter is not null)
        {
            var extent = presenter.DesiredSize.Height;
            // Use a fixed, screen-sized viewport so the window-shift is isolated from
            // layout-dependent ScrollViewer.ViewportHeight (which can read unbounded here).
            const double viewportHeight = 500d;

            var forceViewport = typeof(System.Windows.Controls.VirtualizingStackPanel).GetMethod(
                "ShimForceViewport",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            forceViewport?.Invoke(presenter, [extent / 2, viewportHeight]);
            grid.UpdateLayout();

            realizedAfter = RealizedVisibleRowCount(grid);
            firstAfter = FirstRealizedRowIndex(grid);
        }

        return VirtualizationSnapshot(total, realizedInitial, realizedAfter, firstInitial, firstAfter, extentReport, headerCells, null);
    });

    // Verifies the virtualized presenter realizes over the FILTERED view (Slice 8): apply a
    // hex column filter, then confirm the realization count tracks the filter-matching count.
    [DevFlowAction("roma.probe.metadata-virtualization-filter", Description = "PROBE: enable virtualization, apply a column filter, verify the realized view tracks the filtered item count.")]
    public static string ProbeMetadataVirtualizationFilter(string assemblyPath, string tableName) => RunOnUi(page =>
    {
        var grid = OpenMetadataGrid(page, assemblyPath, tableName, out var error);
        if (grid is null)
            return VirtualizationFilterSnapshot(0, 0, 0, 0, error ?? "metadata table did not render a DataGrid");

        grid.UpdateLayout();
        var total = grid.Items.Count;

        var enable = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "ShimSetRowVirtualization", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (enable?.Invoke(grid, [true]) as bool? != true)
            return VirtualizationFilterSnapshot(total, 0, 0, 0, "could not enable row virtualization");
        grid.UpdateLayout();

        var viewBefore = ShimRealizationCount(grid);

        // Pick a hex column and filter on the first row's exact value (matches a small subset).
        var hexColumnIndex = FirstHexFilterColumnIndex(grid);
        if (hexColumnIndex < 0)
            return VirtualizationFilterSnapshot(total, viewBefore, viewBefore, 0, "no HEX filter column found");
        var column = grid.Columns[hexColumnIndex];
        var bindingPath = column is System.Windows.Controls.DataGridBoundColumn bound
            ? (bound.Binding as System.Windows.Data.Binding)?.Path?.Path : null;
        var firstValue = string.IsNullOrEmpty(bindingPath) ? null
            : grid.Items.Cast<object?>().Select(i => ReadBindingValue(i, bindingPath)).FirstOrDefault(v => v is not null);
        if (firstValue is null)
            return VirtualizationFilterSnapshot(total, viewBefore, viewBefore, 0, "HEX filter column has no values");
        var filterText = string.Format("{0:x8}", firstValue);

        // Apply the filter through the shim's internal filter state (reflection — Roma.Host
        // is not in InternalsVisibleTo), then invalidate the realization view.
        var filterType = typeof(DataGridExtensions.DataGridFilter);
        var state = filterType.GetMethod("GetState", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, [grid]);
        if (state is null)
            return VirtualizationFilterSnapshot(total, viewBefore, viewBefore, 0, "filter state unavailable");
        state.GetType().GetField("IsAutoFilterEnabled")?.SetValue(state, true);
        if (state.GetType().GetField("ColumnFilters", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(state) is System.Collections.IDictionary filters)
            filters[column] = new DataGridExtensions.HexContentFilter(filterText);

        // Expected count must be computed before ShimApplyFilterView mutates Items into the
        // filtered view (after which Items.Count IS the filtered count).
        var expected = grid.Items.Cast<object?>().Count(i => DataGridExtensions.DataGridFilter.MatchesAllFilters(grid, i));

        typeof(System.Windows.Controls.DataGrid).GetMethod(
            "ShimApplyFilterView", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(grid, null);
        grid.UpdateLayout();

        // Items is now the filtered+sorted view (WPF ICollectionView.Filter).
        var viewAfter = grid.Items.Count;
        return VirtualizationFilterSnapshot(total, viewBefore, viewAfter, expected, null);
    });

    static int ShimRealizationCount(System.Windows.Controls.DataGrid grid) => grid.Items.Count;

    static string VirtualizationFilterSnapshot(int total, int viewBefore, int viewAfter, int expected, string? error)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"total\":{total},");
        sb.Append($"\"viewBefore\":{viewBefore},");
        sb.Append($"\"viewAfter\":{viewAfter},");
        sb.Append($"\"expected\":{expected},");
        sb.Append($"\"honorsFilter\":{(viewAfter == expected && viewAfter < total ? "true" : "false")}");
        if (error is not null) sb.Append(',').Append($"\"error\":{Json(error)}");
        sb.Append('}');
        return sb.ToString();
    }

    // Verifies off-screen scroll-into-view (A1): an item far outside the realized window is
    // not realized, and ShimScrollItemIntoView devirtualizes it (scrolls + realizes).
    [DevFlowAction("roma.probe.metadata-scroll-into-view", Description = "PROBE: enable virtualization, scroll an off-screen item into view, verify it devirtualizes.")]
    public static string ProbeMetadataScrollIntoView(string assemblyPath, string tableName) => RunOnUi(page =>
    {
        var grid = OpenMetadataGrid(page, assemblyPath, tableName, out var error);
        if (grid is null)
            return ScrollIntoViewSnapshot(0, -1, false, false, -1, error ?? "metadata table did not render a DataGrid");

        grid.UpdateLayout();
        var total = grid.Items.Count;

        var enable = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "ShimSetRowVirtualization", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (enable?.Invoke(grid, [true]) as bool? != true)
            return ScrollIntoViewSnapshot(total, -1, false, false, -1, "could not enable row virtualization");
        grid.UpdateLayout();

        if (total <= 0)
            return ScrollIntoViewSnapshot(total, -1, false, false, -1, "empty table");

        // Pin a small viewport at the top so the target index is genuinely off-screen
        // (the first measure's fallback viewport can otherwise be large enough to realize it).
        var presenter = FindDescendant<System.Windows.Controls.Primitives.DataGridRowsPresenter>(grid);
        var forceViewport = typeof(System.Windows.Controls.VirtualizingStackPanel).GetMethod(
            "ShimForceViewport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (presenter is not null)
        {
            forceViewport?.Invoke(presenter, [0d, 500d]);
            grid.UpdateLayout();
        }

        // Pick an index well outside that top window.
        var targetIndex = Math.Min(total - 1, total / 2 + 300);
        var targetItem = grid.Items[targetIndex];
        var realizedBefore = grid.ItemContainerGenerator.ContainerFromItem(targetItem) is not null;

        var scroll = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "ShimScrollItemIntoView", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var realizedAfter = scroll?.Invoke(grid, [targetItem]) as bool? == true;
        var firstAfter = FirstRealizedRowIndex(grid);

        return ScrollIntoViewSnapshot(total, targetIndex, realizedBefore, realizedAfter, firstAfter, null);
    });

    // Verifies off-screen selection end-to-end via ILSpy's real SelectItem extension: selecting
    // a virtualized off-screen item scrolls it into view, realizes it, and marks it selected.
    [DevFlowAction("roma.probe.metadata-select-offscreen", Description = "PROBE: enable virtualization, SelectItem an off-screen item, verify it devirtualizes and is selected.")]
    public static string ProbeMetadataSelectOffscreen(string assemblyPath, string tableName) => RunOnUi(page =>
    {
        var grid = OpenMetadataGrid(page, assemblyPath, tableName, out var error);
        if (grid is null)
            return SelectOffscreenSnapshot(0, -1, false, false, false, -1, error ?? "metadata table did not render a DataGrid");

        grid.UpdateLayout();
        var total = grid.Items.Count;

        var enable = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "ShimSetRowVirtualization", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (enable?.Invoke(grid, [true]) as bool? != true)
            return SelectOffscreenSnapshot(total, -1, false, false, false, -1, "could not enable row virtualization");
        grid.UpdateLayout();

        if (total <= 0)
            return SelectOffscreenSnapshot(total, -1, false, false, false, -1, "empty table");

        // Pin a small top viewport so the target is off-screen.
        var presenter = FindDescendant<System.Windows.Controls.Primitives.DataGridRowsPresenter>(grid);
        typeof(System.Windows.Controls.VirtualizingStackPanel).GetMethod(
            "ShimForceViewport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(presenter, [0d, 500d]);
        grid.UpdateLayout();

        var targetIndex = Math.Min(total - 1, total / 2 + 300);
        var targetItem = grid.Items[targetIndex];
        var realizedBefore = grid.ItemContainerGenerator.ContainerFromItem(targetItem) is not null;

        // The exact call Roma's metadata navigation makes.
        ICSharpCode.ILSpy.ExtensionMethods.SelectItem(grid, targetItem!);

        // Robust, virtualization-proof outcome: the engine selection points at the target,
        // so the row shows selected whenever realized (now or after the scroll settles).
        var engineSelected = ReferenceEquals(grid.SelectedItem, targetItem);
        var container = grid.ItemContainerGenerator.ContainerFromItem(targetItem);
        var realizedAndVisuallySelected = container is System.Windows.Controls.DataGridRow row && row.IsSelected;
        var firstAfter = FirstRealizedRowIndex(grid);
        return SelectOffscreenSnapshot(total, targetIndex, realizedBefore, engineSelected, realizedAndVisuallySelected, firstAfter, null);
    });

    // Verifies keyboard navigation reaches virtualized off-screen rows (e.g. End / PageDown):
    // MoveSelectionToIndex selects + scrolls to an item far outside the realized window.
    [DevFlowAction("roma.probe.metadata-keyboard-offscreen", Description = "PROBE: enable virtualization, keyboard-navigate to an off-screen row, verify it selects + scrolls into view.")]
    public static string ProbeMetadataKeyboardOffscreen(string assemblyPath, string tableName) => RunOnUi(page =>
    {
        var grid = OpenMetadataGrid(page, assemblyPath, tableName, out var error);
        if (grid is null)
            return SelectOffscreenSnapshot(0, -1, false, false, false, -1, error ?? "metadata table did not render a DataGrid");

        grid.UpdateLayout();
        var total = grid.Items.Count;
        var enable = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "ShimSetRowVirtualization", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (enable?.Invoke(grid, [true]) as bool? != true || total <= 0)
            return SelectOffscreenSnapshot(total, -1, false, false, false, -1, "could not enable row virtualization / empty");
        grid.UpdateLayout();

        var presenter = FindDescendant<System.Windows.Controls.Primitives.DataGridRowsPresenter>(grid);
        typeof(System.Windows.Controls.VirtualizingStackPanel).GetMethod(
            "ShimForceViewport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(presenter, [0d, 500d]);
        grid.UpdateLayout();

        var targetIndex = Math.Min(total - 1, total / 2 + 300);
        var realizedBefore = grid.ItemContainerGenerator.ContainerFromItem(grid.Items[targetIndex]) is not null;

        // Drive the row-navigation keyboard path (Home/End/PageDown all route through this).
        typeof(System.Windows.Controls.DataGrid).GetMethod(
            "MoveSelectionToIndex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(grid, [targetIndex]);

        var engineSelected = ReferenceEquals(grid.SelectedItem, grid.Items[targetIndex]);
        var container = grid.ItemContainerGenerator.ContainerFromItem(grid.Items[targetIndex]);
        var visuallySelected = container is System.Windows.Controls.DataGridRow row && row.IsSelected;
        return SelectOffscreenSnapshot(total, targetIndex, realizedBefore, engineSelected, visuallySelected, FirstRealizedRowIndex(grid), null);
    });

    static string SelectOffscreenSnapshot(int total, int targetIndex, bool realizedBefore, bool engineSelected, bool visuallySelected, int firstAfter, string? error)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"total\":{total},");
        sb.Append($"\"targetIndex\":{targetIndex},");
        sb.Append($"\"realizedBefore\":{(realizedBefore ? "true" : "false")},");
        sb.Append($"\"engineSelected\":{(engineSelected ? "true" : "false")},");
        sb.Append($"\"visuallySelected\":{(visuallySelected ? "true" : "false")},");
        sb.Append($"\"firstAfter\":{firstAfter}");
        if (error is not null) sb.Append(',').Append($"\"error\":{Json(error)}");
        sb.Append('}');
        return sb.ToString();
    }

    static string ScrollIntoViewSnapshot(int total, int targetIndex, bool realizedBefore, bool realizedAfter, int firstAfter, string? error)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"total\":{total},");
        sb.Append($"\"targetIndex\":{targetIndex},");
        sb.Append($"\"realizedBefore\":{(realizedBefore ? "true" : "false")},");
        sb.Append($"\"realizedAfter\":{(realizedAfter ? "true" : "false")},");
        sb.Append($"\"firstAfter\":{firstAfter},");
        // Proof of BringIndexIntoView: after scrolling, the target is realized AND the realized
        // window now starts at the target (within the cache band) — i.e. it was brought to the
        // top of the viewport. (realizedBefore is informational: the probe layout's effective
        // viewport can be large enough to already realize the target.)
        var broughtToTarget = realizedAfter && firstAfter >= 0 && targetIndex - firstAfter is >= 0 and <= 30;
        sb.Append($"\"broughtToTarget\":{(broughtToTarget ? "true" : "false")}");
        if (error is not null) sb.Append(',').Append($"\"error\":{Json(error)}");
        sb.Append('}');
        return sb.ToString();
    }

    // Count of DataGridRow containers currently visible (excludes collapsed/recycled).
    static int RealizedVisibleRowCount(System.Windows.Controls.DataGrid grid)
    {
        var count = 0;
        foreach (var row in FindDescendants<System.Windows.Controls.DataGridRow>(grid))
        {
            if (row.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
                count++;
        }

        return count;
    }

    // Smallest item index among the realized (visible) rows, or -1 if none.
    static int FirstRealizedRowIndex(System.Windows.Controls.DataGrid grid)
    {
        var first = -1;
        foreach (var row in FindDescendants<System.Windows.Controls.DataGridRow>(grid))
        {
            if (row.Visibility != Microsoft.UI.Xaml.Visibility.Visible || row.Item is null)
                continue;
            var index = grid.Items.IndexOf(row.Item);
            if (index >= 0 && (first < 0 || index < first))
                first = index;
        }

        return first;
    }

    static string VirtualizationSnapshot(
        int total, int realizedInitial, int realizedAfterScroll, int firstInitial, int firstAfterScroll, double extent, int headerCells, string? error)
    {
        var rowHeight = total > 0 ? extent / total : 0d;
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"total\":{total},");
        sb.Append($"\"realizedInitial\":{realizedInitial},");
        sb.Append($"\"realizedAfterScroll\":{realizedAfterScroll},");
        sb.Append($"\"firstInitial\":{firstInitial},");
        sb.Append($"\"firstAfterScroll\":{firstAfterScroll},");
        sb.Append($"\"extent\":{extent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)},");
        sb.Append($"\"rowHeight\":{rowHeight.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},");
        sb.Append($"\"headerCells\":{headerCells},");
        sb.Append($"\"virtualized\":{(total > 0 && realizedInitial > 0 && realizedInitial < total ? "true" : "false")}");
        if (error is not null) sb.Append(',').Append($"\"error\":{Json(error)}");
        sb.Append('}');
        return sb.ToString();
    }

    [DevFlowAction("roma.probe.metadata-header-row-details", Description = "PROBE: open PE header details; returns row-details DataGrid snapshot JSON.")]
    public static string ProbeMetadataHeaderRowDetails(string assemblyPath, string headerName) => RunOnUi(page =>
    {
        if (!page._assemblyContext.AssemblyList.GetAssemblies()
                .Select(a => a.GetMetadataFileOrNull())
                .Any(f => f is not null && string.Equals(f.FileName, assemblyPath, StringComparison.OrdinalIgnoreCase)))
        {
            page._assemblyContext.AssemblyList.OpenAssembly(assemblyPath);
        }

        string expectedMember = headerName.Equals("Optional Header", StringComparison.OrdinalIgnoreCase)
            ? "DLL Characteristics"
            : "Characteristics";

        var target = page.FindNode(page._assemblyContext.Root, node =>
            string.Equals(node.Text?.ToString(), headerName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return Snapshot(page, error: $"metadata header '{headerName}' not found");

        page.OnTreeNodeSelected(target);

        var grid = page._nodeContent?.Content as System.Windows.Controls.DataGrid;
        if (grid is null)
            return HeaderDetailsSnapshot(page, headerName, expectedMember, null, null, null, null, "header did not render a DataGrid");

        var item = grid.Items.Cast<object?>().FirstOrDefault(x =>
            string.Equals(ReadBindingString(x, "Member"), expectedMember, StringComparison.Ordinal));
        if (item is null)
            return HeaderDetailsSnapshot(page, headerName, expectedMember, grid, null, null, null, "details item not found");

        grid.SelectedItem = item;
        grid.SetDetailsVisibilityForItem(item, Microsoft.UI.Xaml.Visibility.Visible);

        var selector = grid.RowDetailsTemplateSelector;
        var template = selector?.SelectTemplate(item, grid);
        Microsoft.UI.Xaml.FrameworkElement? detailsElement = null;
        if (template is System.Windows.Controls.IWpfTemplateBridge bridge)
        {
            detailsElement = bridge.LoadContent(item, grid);
        }

        var detailsGrid = detailsElement as System.Windows.Controls.DataGrid;
        return HeaderDetailsSnapshot(page, headerName, expectedMember, grid, selector, template, detailsGrid, null);
    });

    [DevFlowAction("roma.probe.metadata-resize-column", Description = "PROBE: resize a metadata DataGrid column; returns width snapshot JSON.")]
    public static string ProbeMetadataResizeColumn(string assemblyPath, string tableName, int columnIndex, double delta) => RunOnUi(page =>
    {
        if (!page._assemblyContext.AssemblyList.GetAssemblies()
                .Select(a => a.GetMetadataFileOrNull())
                .Any(f => f is not null && string.Equals(f.FileName, assemblyPath, StringComparison.OrdinalIgnoreCase)))
        {
            page._assemblyContext.AssemblyList.OpenAssembly(assemblyPath);
        }

        if (!Enum.TryParse<TableIndex>(tableName, ignoreCase: true, out var table))
            return Snapshot(page, error: $"unknown metadata table '{tableName}'");

        var target = page.FindNode(page._assemblyContext.Root, node =>
            node is ICSharpCode.ILSpy.Metadata.MetadataTableTreeNode tableNode
            && tableNode.Kind == table);
        if (target is null)
            return Snapshot(page, error: $"metadata table '{table}' not found");

        page.OnTreeNodeSelected(target);
        var grid = page._nodeContent?.Content as System.Windows.Controls.DataGrid;
        if (grid is null)
            return MetadataResizeSnapshot(null, columnIndex, false, 0, 0, "metadata table did not render a DataGrid");

        grid.UpdateLayout();
        if (columnIndex < 0 || columnIndex >= grid.Columns.Count)
            return MetadataResizeSnapshot(grid, columnIndex, false, 0, 0, $"column index {columnIndex} out of range");

        var column = grid.Columns[columnIndex];
        var before = EffectiveColumnWidth(column);
        var method = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "ShimTryResizeColumn",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var resized = method?.Invoke(grid, [column, delta]) as bool? == true;
        grid.UpdateLayout();
        var after = EffectiveColumnWidth(column);
        return MetadataResizeSnapshot(grid, columnIndex, resized, before, after, null);
    });

    [DevFlowAction("roma.probe.metadata-autosize-column", Description = "PROBE: auto-size a metadata DataGrid column; returns width snapshot JSON.")]
    public static string ProbeMetadataAutoSizeColumn(string assemblyPath, string tableName, int columnIndex) => RunOnUi(page =>
    {
        if (!page._assemblyContext.AssemblyList.GetAssemblies()
                .Select(a => a.GetMetadataFileOrNull())
                .Any(f => f is not null && string.Equals(f.FileName, assemblyPath, StringComparison.OrdinalIgnoreCase)))
        {
            page._assemblyContext.AssemblyList.OpenAssembly(assemblyPath);
        }

        if (!Enum.TryParse<TableIndex>(tableName, ignoreCase: true, out var table))
            return Snapshot(page, error: $"unknown metadata table '{tableName}'");

        var target = page.FindNode(page._assemblyContext.Root, node =>
            node is ICSharpCode.ILSpy.Metadata.MetadataTableTreeNode tableNode
            && tableNode.Kind == table);
        if (target is null)
            return Snapshot(page, error: $"metadata table '{table}' not found");

        page.OnTreeNodeSelected(target);
        var grid = page._nodeContent?.Content as System.Windows.Controls.DataGrid;
        if (grid is null)
            return MetadataResizeSnapshot(null, columnIndex, false, 0, 0, "metadata table did not render a DataGrid");

        grid.UpdateLayout();
        if (columnIndex < 0 || columnIndex >= grid.Columns.Count)
            return MetadataResizeSnapshot(grid, columnIndex, false, 0, 0, $"column index {columnIndex} out of range");

        var column = grid.Columns[columnIndex];
        var resize = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "ShimTryResizeColumn",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        _ = resize?.Invoke(grid, [column, -10_000.0]);
        grid.UpdateLayout();
        var before = EffectiveColumnWidth(column);

        var bestFitMethod = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "ShimBestFitColumnWidth",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var bestFit = bestFitMethod?.Invoke(grid, [column]) as double? ?? double.NaN;
        var autoSize = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "ShimTryAutoSizeColumn",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var resized = autoSize?.Invoke(grid, [column]) as bool? == true;
        grid.UpdateLayout();
        var after = EffectiveColumnWidth(column);
        return MetadataResizeSnapshot(grid, columnIndex, resized, before, after, null, bestFit);
    });

    [DevFlowAction("roma.probe.metadata-gripper-resize-column", Description = "PROBE: resize a metadata DataGrid column through the realized header gripper.")]
    public static string ProbeMetadataGripperResizeColumn(string assemblyPath, string tableName, int columnIndex, double delta) => RunOnUi(page =>
    {
        var grid = OpenMetadataGrid(page, assemblyPath, tableName, out var error);
        if (grid is null)
            return MetadataResizeSnapshot(null, columnIndex, false, 0, 0, error ?? "metadata table did not render a DataGrid");

        grid.UpdateLayout();
        if (columnIndex < 0 || columnIndex >= grid.Columns.Count)
            return MetadataResizeSnapshot(grid, columnIndex, false, 0, 0, $"column index {columnIndex} out of range");

        var header = MetadataHeaderAt(grid, columnIndex);
        if (header is null)
            return MetadataResizeSnapshot(grid, columnIndex, false, 0, 0, "metadata header was not realized");

        header.ApplyTemplate();
        var gripper = HeaderGripper(header, "_rightGripper");
        if (gripper is null)
            return MetadataResizeSnapshot(grid, columnIndex, false, 0, 0, "right header gripper was not hooked");
        if (!HeaderGripperHasCursor(gripper))
            return MetadataResizeSnapshot(grid, columnIndex, false, 0, 0, "right header gripper has no resize cursor");

        var column = grid.Columns[columnIndex];
        var before = EffectiveColumnWidth(column);
        gripper.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0) { Source = gripper });
        gripper.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(delta, 0) { Source = gripper });
        gripper.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(delta, 0, false) { Source = gripper });
        grid.UpdateLayout();
        var after = EffectiveColumnWidth(column);
        return MetadataResizeSnapshot(grid, columnIndex, after > before, before, after, null);
    });

    [DevFlowAction("roma.probe.metadata-gripper-autosize-column", Description = "PROBE: auto-size a metadata DataGrid column through the realized header gripper double-click.")]
    public static string ProbeMetadataGripperAutoSizeColumn(string assemblyPath, string tableName, int columnIndex) => RunOnUi(page =>
        ProbeMetadataGripperAutoSizeColumnCore(page, assemblyPath, tableName, columnIndex, -10_000.0));

    [DevFlowAction("roma.probe.metadata-gripper-autosize-wide-column", Description = "PROBE: shrink a wide metadata DataGrid column to best-fit through the realized header gripper double-click.")]
    public static string ProbeMetadataGripperAutoSizeWideColumn(string assemblyPath, string tableName, int columnIndex) => RunOnUi(page =>
        ProbeMetadataGripperAutoSizeColumnCore(page, assemblyPath, tableName, columnIndex, 10_000.0));

    static string ProbeMetadataGripperAutoSizeColumnCore(MainPage page, string assemblyPath, string tableName, int columnIndex, double initialResizeDelta)
    {
        var grid = OpenMetadataGrid(page, assemblyPath, tableName, out var error);
        if (grid is null)
            return MetadataResizeSnapshot(null, columnIndex, false, 0, 0, error ?? "metadata table did not render a DataGrid");

        grid.UpdateLayout();
        if (columnIndex < 0 || columnIndex >= grid.Columns.Count)
            return MetadataResizeSnapshot(grid, columnIndex, false, 0, 0, $"column index {columnIndex} out of range");

        var column = grid.Columns[columnIndex];
        var resize = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "ShimTryResizeColumn",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        _ = resize?.Invoke(grid, [column, initialResizeDelta]);
        grid.UpdateLayout();

        var header = MetadataHeaderAt(grid, columnIndex);
        if (header is null)
            return MetadataResizeSnapshot(grid, columnIndex, false, 0, 0, "metadata header was not realized");

        header.ApplyTemplate();
        var gripper = HeaderGripper(header, "_rightGripper");
        if (gripper is null)
            return MetadataResizeSnapshot(grid, columnIndex, false, 0, 0, "right header gripper was not hooked");
        if (!HeaderGripperHasCursor(gripper))
            return MetadataResizeSnapshot(grid, columnIndex, false, 0, 0, "right header gripper has no resize cursor");

        var before = EffectiveColumnWidth(column);
        var bestFitMethod = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "ShimBestFitColumnWidth",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var bestFit = bestFitMethod?.Invoke(grid, [column]) as double? ?? double.NaN;
        var mouseDoubleClickEvent = typeof(System.Windows.Controls.Primitives.Thumb).GetField(
            "MouseDoubleClickEvent",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null)
            as System.Windows.RoutedEvent;
        if (mouseDoubleClickEvent is null)
            return MetadataResizeSnapshot(grid, columnIndex, false, before, before, "Thumb.MouseDoubleClickEvent not found", bestFit);

        gripper.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs
        {
            RoutedEvent = mouseDoubleClickEvent,
            Source = gripper,
            ClickCount = 2,
        });
        grid.UpdateLayout();
        var after = EffectiveColumnWidth(column);
        return MetadataResizeSnapshot(grid, columnIndex, Math.Abs(after - before) > 0.5, before, after, null, bestFit);
    }

    [DevFlowAction("roma.probe.metadata-resize-left-edge", Description = "PROBE: resize via a metadata header's left edge; returns width snapshot JSON.")]
    public static string ProbeMetadataResizeLeftEdge(string assemblyPath, string tableName, int headerIndex, double delta) => RunOnUi(page =>
    {
        if (!page._assemblyContext.AssemblyList.GetAssemblies()
                .Select(a => a.GetMetadataFileOrNull())
                .Any(f => f is not null && string.Equals(f.FileName, assemblyPath, StringComparison.OrdinalIgnoreCase)))
        {
            page._assemblyContext.AssemblyList.OpenAssembly(assemblyPath);
        }

        if (!Enum.TryParse<TableIndex>(tableName, ignoreCase: true, out var table))
            return Snapshot(page, error: $"unknown metadata table '{tableName}'");

        var target = page.FindNode(page._assemblyContext.Root, node =>
            node is ICSharpCode.ILSpy.Metadata.MetadataTableTreeNode tableNode
            && tableNode.Kind == table);
        if (target is null)
            return Snapshot(page, error: $"metadata table '{table}' not found");

        page.OnTreeNodeSelected(target);
        var grid = page._nodeContent?.Content as System.Windows.Controls.DataGrid;
        if (grid is null)
            return MetadataResizeSnapshot(null, headerIndex, false, 0, 0, "metadata table did not render a DataGrid");

        grid.UpdateLayout();
        var headerCells = typeof(System.Windows.Controls.DataGrid).GetField(
            "_headerCells",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(grid)
            as System.Collections.IList;
        if (headerIndex < 0 || headerIndex >= grid.Columns.Count)
            return MetadataResizeSnapshot(grid, headerIndex, false, 0, 0, $"header index {headerIndex} out of range");

        var header = headerIndex < headerCells?.Count
            ? headerCells[headerIndex]
            : null;
        if (header is null)
        {
            var columnForHeader = grid.Columns[headerIndex];
            var preparedHeader = new System.Windows.Controls.Primitives.DataGridColumnHeader
            {
                Content = columnForHeader.Header,
                Width = Math.Max(40, EffectiveColumnWidth(columnForHeader)),
            };
            typeof(System.Windows.Controls.Primitives.DataGridColumnHeader).GetMethod(
                "PrepareColumnHeader",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(preparedHeader, [columnForHeader.Header, columnForHeader]);
            header = preparedHeader;
        }

        var prevColumn = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "PreviousVisibleColumn",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var column = prevColumn?.Invoke(grid, [grid.Columns[headerIndex]]) as System.Windows.Controls.DataGridColumn;
        if (column is null)
            return MetadataResizeSnapshot(grid, headerIndex, false, 0, 0, "left edge did not resolve a resize column");

        var columnIndex = grid.Columns.IndexOf(column);
        var before = EffectiveColumnWidth(column);
        var resize = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "ShimTryResizeColumn",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var resized = resize?.Invoke(grid, [column, delta]) as bool? == true;
        grid.UpdateLayout();
        var after = EffectiveColumnWidth(column);
        return MetadataResizeSnapshot(grid, columnIndex, resized, before, after, null);
    });

    [DevFlowAction("roma.probe.metadata-copy-selection", Description = "PROBE: copy selected metadata DataGrid row; returns clipboard snapshot JSON.")]
    public static string ProbeMetadataCopySelection(string assemblyPath, string tableName, bool includeHeader) => RunOnUi(page =>
    {
        if (!page._assemblyContext.AssemblyList.GetAssemblies()
                .Select(a => a.GetMetadataFileOrNull())
                .Any(f => f is not null && string.Equals(f.FileName, assemblyPath, StringComparison.OrdinalIgnoreCase)))
        {
            page._assemblyContext.AssemblyList.OpenAssembly(assemblyPath);
        }

        if (!Enum.TryParse<TableIndex>(tableName, ignoreCase: true, out var table))
            return MetadataClipboardSnapshot(null, false, null, null, null, $"unknown metadata table '{tableName}'");

        var target = page.FindNode(page._assemblyContext.Root, node =>
            node is ICSharpCode.ILSpy.Metadata.MetadataTableTreeNode tableNode
            && tableNode.Kind == table);
        if (target is null)
            return MetadataClipboardSnapshot(null, false, null, null, null, $"metadata table '{table}' not found");

        page.OnTreeNodeSelected(target);
        var grid = page._nodeContent?.Content as System.Windows.Controls.DataGrid;
        if (grid is null)
            return MetadataClipboardSnapshot(null, false, null, null, null, "metadata table did not render a DataGrid");

        grid.UpdateLayout();
        var item = grid.Items.Cast<object?>().FirstOrDefault();
        if (item is null)
            return MetadataClipboardSnapshot(grid, false, null, null, null, "metadata table has no rows");

        grid.ClipboardCopyMode = includeHeader
            ? System.Windows.Controls.DataGridClipboardCopyMode.IncludeHeader
            : System.Windows.Controls.DataGridClipboardCopyMode.ExcludeHeader;
        grid.SelectedItem = item;
        if (!grid.SelectedItems.Contains(item))
        {
            grid.SelectedItems.Add(item);
        }

        var method = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "ShimBuildClipboardDataObject",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var data = method?.Invoke(grid, []) as System.Windows.DataObject;
        var text = data?.GetData(System.Windows.DataFormats.UnicodeText)?.ToString();
        var csv = data?.GetData(System.Windows.DataFormats.CommaSeparatedValue)?.ToString();
        return MetadataClipboardSnapshot(grid, data is not null, text, csv, item, null);
    });

    [DevFlowAction("roma.probe.metadata-keyboard-selection", Description = "PROBE: select-all and move current cell in a metadata DataGrid; returns selection snapshot JSON.")]
    public static string ProbeMetadataKeyboardSelection(string assemblyPath, string tableName) => RunOnUi(page =>
    {
        if (!page._assemblyContext.AssemblyList.GetAssemblies()
                .Select(a => a.GetMetadataFileOrNull())
                .Any(f => f is not null && string.Equals(f.FileName, assemblyPath, StringComparison.OrdinalIgnoreCase)))
        {
            page._assemblyContext.AssemblyList.OpenAssembly(assemblyPath);
        }

        if (!Enum.TryParse<TableIndex>(tableName, ignoreCase: true, out var table))
            return MetadataKeyboardSelectionSnapshot(null, false, false, null, null, $"unknown metadata table '{tableName}'");

        var target = page.FindNode(page._assemblyContext.Root, node =>
            node is ICSharpCode.ILSpy.Metadata.MetadataTableTreeNode tableNode
            && tableNode.Kind == table);
        if (target is null)
            return MetadataKeyboardSelectionSnapshot(null, false, false, null, null, $"metadata table '{table}' not found");

        page.OnTreeNodeSelected(target);
        var grid = page._nodeContent?.Content as System.Windows.Controls.DataGrid;
        if (grid is null)
            return MetadataKeyboardSelectionSnapshot(null, false, false, null, null, "metadata table did not render a DataGrid");

        grid.UpdateLayout();
        grid.SelectionUnit = System.Windows.Controls.DataGridSelectionUnit.Cell;
        var selectAll = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "ShimSelectAllCells",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var move = typeof(System.Windows.Controls.DataGrid).GetMethod(
            "MoveCurrentCellByOffset",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        var selected = selectAll?.Invoke(grid, []) as bool? == true;
        var beforeHeader = grid.CurrentCell.Column?.Header?.ToString();
        var moved = move?.Invoke(grid, [0, 1, false]) as bool? == true;
        var afterHeader = grid.CurrentCell.Column?.Header?.ToString();
        return MetadataKeyboardSelectionSnapshot(grid, selected, moved, beforeHeader, afterHeader, null);
    });

    [DevFlowAction("roma.probe.metadata-custom-debug-row-details", Description = "PROBE: open CustomDebugInformation and materialize first row details.")]
    public static string ProbeMetadataCustomDebugRowDetails(string assemblyPath) => RunOnUi(page =>
    {
        if (!page._assemblyContext.AssemblyList.GetAssemblies()
                .Select(a => a.GetMetadataFileOrNull())
                .Any(f => f is not null && string.Equals(f.FileName, assemblyPath, StringComparison.OrdinalIgnoreCase)))
        {
            page._assemblyContext.AssemblyList.OpenAssembly(assemblyPath);
        }

        var target = page.FindNode(page._assemblyContext.Root, node =>
            node is ICSharpCode.ILSpy.Metadata.MetadataTableTreeNode tableNode
            && tableNode.Kind == TableIndex.CustomDebugInformation);
        if (target is null)
            return CustomDebugDetailsSnapshot(page, null, null, null, null, null, "CustomDebugInformation table not found");

        page.OnTreeNodeSelected(target);

        var grid = page._nodeContent?.Content as System.Windows.Controls.DataGrid;
        if (grid is null)
            return CustomDebugDetailsSnapshot(page, null, null, null, null, null, "CustomDebugInformation did not render a DataGrid");

        var item = grid.Items.Cast<object?>().FirstOrDefault(x => ReadBindingValue(x, "RowDetails") is not null);
        if (item is null)
            return CustomDebugDetailsSnapshot(page, grid, null, null, null, null, "no CustomDebugInformation row with details");

        grid.SelectedItem = item;
        grid.SetDetailsVisibilityForItem(item, Microsoft.UI.Xaml.Visibility.Visible);

        var selector = grid.RowDetailsTemplateSelector;
        var template = selector?.SelectTemplate(item, grid);
        Microsoft.UI.Xaml.FrameworkElement? detailsElement = null;
        if (template is System.Windows.Controls.IWpfTemplateBridge bridge)
        {
            detailsElement = bridge.LoadContent(item, grid);
        }

        return CustomDebugDetailsSnapshot(page, grid, selector, template, detailsElement, item, null);
    });

    [DevFlowAction("roma.probe.metadata-xaml-resources", Description = "PROBE: report upstream MetadataTableViews.xaml resource translation.")]
    public static string ProbeMetadataXamlResources() => RunOnUi(page =>
    {
        _ = ICSharpCode.ILSpy.Metadata.MetadataTableViews.Instance;
        var report = ICSharpCode.ILSpy.Metadata.MetadataTableViews.LastTranslationReport;
        var xamlPath = System.IO.Path.Combine(AppContext.BaseDirectory, "ILSpy", "Metadata", "MetadataTableViews.xaml");

        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"xamlPresent\":{(System.IO.File.Exists(xamlPath) ? "true" : "false")},");
        sb.Append($"\"translated\":[{JsonArray(report?.TranslatedKeys)}],");
        sb.Append($"\"fallback\":[{JsonArray(report?.FallbackKeys)}],");
        sb.Append($"\"skipped\":[{JsonArray(report?.SkippedKeys)}]");
        sb.Append('}');
        return sb.ToString();
    });

    [DevFlowAction("roma.probe.flags-tooltip-xaml-resources", Description = "PROBE: report upstream FlagsTooltip.xaml resource translation.")]
    public static string ProbeFlagsTooltipXamlResources() => RunOnUi(page =>
    {
        var xamlPath = System.IO.Path.Combine(AppContext.BaseDirectory, "ILSpy", "Metadata", "FlagsTooltip.xaml");
        System.Windows.Controls.WpfXamlResourceTranslationReport? report = null;
        if (System.IO.File.Exists(xamlPath))
        {
            _ = System.Windows.Controls.WpfXamlResourceTranslator.TranslateResourceDictionary(
                System.IO.File.ReadAllText(xamlPath),
                ResolveFlagsTooltipXamlType,
                out report);
        }

        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"xamlPresent\":{(System.IO.File.Exists(xamlPath) ? "true" : "false")},");
        sb.Append($"\"translated\":[{JsonArray(report?.TranslatedKeys)}],");
        sb.Append($"\"fallback\":[{JsonArray(report?.FallbackKeys)}],");
        sb.Append($"\"skipped\":[{JsonArray(report?.SkippedKeys)}]");
        sb.Append('}');
        return sb.ToString();
    });

    [DevFlowAction("roma.probe.resource-tables-xaml-resources", Description = "PROBE: report upstream Resource*Table.xaml resource translation.")]
    public static string ProbeResourceTablesXamlResources() => RunOnUi(page =>
    {
        var stringTablePath = System.IO.Path.Combine(AppContext.BaseDirectory, "ILSpy", "Controls", "ResourceStringTable.xaml");
        var objectTablePath = System.IO.Path.Combine(AppContext.BaseDirectory, "ILSpy", "Controls", "ResourceObjectTable.xaml");
        var stringReport = TranslateResourceTableXaml(stringTablePath);
        var objectReport = TranslateResourceTableXaml(objectTablePath);

        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"stringXamlPresent\":{(System.IO.File.Exists(stringTablePath) ? "true" : "false")},");
        sb.Append($"\"stringTranslated\":[{JsonArray(stringReport?.TranslatedKeys)}],");
        sb.Append($"\"stringFallback\":[{JsonArray(stringReport?.FallbackKeys)}],");
        sb.Append($"\"stringSkipped\":[{JsonArray(stringReport?.SkippedKeys)}],");
        sb.Append($"\"objectXamlPresent\":{(System.IO.File.Exists(objectTablePath) ? "true" : "false")},");
        sb.Append($"\"objectTranslated\":[{JsonArray(objectReport?.TranslatedKeys)}],");
        sb.Append($"\"objectFallback\":[{JsonArray(objectReport?.FallbackKeys)}],");
        sb.Append($"\"objectSkipped\":[{JsonArray(objectReport?.SkippedKeys)}]");
        sb.Append('}');
        return sb.ToString();
    });

    private static System.Windows.Controls.WpfXamlResourceTranslationReport? TranslateResourceTableXaml(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        _ = System.Windows.Controls.WpfXamlResourceTranslator.TranslateResourceDictionary(
            System.IO.File.ReadAllText(path),
            ResolveResourceTableXamlType,
            out var report);
        return report;
    }

    private static Type? ResolveResourceTableXamlType(string name)
        => name switch
        {
            "ListViewItem" => typeof(Microsoft.UI.Xaml.Controls.ListViewItem),
            _ => null
        };

    private static Type? ResolveFlagsTooltipXamlType(string name)
        => name switch
        {
            "NullVisibilityConverter" or "local:NullVisibilityConverter" => typeof(ICSharpCode.ILSpy.Metadata.NullVisibilityConverter),
            "local:MultipleChoiceGroup" => typeof(ICSharpCode.ILSpy.Metadata.MultipleChoiceGroup),
            "local:SingleChoiceGroup" => typeof(ICSharpCode.ILSpy.Metadata.SingleChoiceGroup),
            _ => null
        };

    // ---- helpers ----

    static string RunOnUi(Func<MainPage, string> body)
    {
        var page = _current;
        if (page is null) return "{\"error\":\"MainPage not available\"}";
        string result = "{\"error\":\"timeout\"}";
        using var done = new ManualResetEventSlim();
        page.DispatcherQueue.TryEnqueue(() =>
        {
            try { result = body(page); }
            catch (Exception ex) { result = $"{{\"error\":{Json(ex.Message)}}}"; }
            finally { done.Set(); }
        });
        done.Wait(TimeSpan.FromSeconds(30));
        return result;
    }

    // Builds the JSON state snapshot. `note` injects extra raw JSON members (already escaped).
    static string Snapshot(MainPage page, string? error = null, string? note = null)
    {
        var tree = page._assemblyTree;
        int rows = tree?.RootNodes.Count ?? 0;
        // Whether the active list's name is still registered (it stays so even when its assemblies are
        // cleared — clearing empties the list, it doesn't delete the list definition).
        bool listPersisted = page._assemblyContext.AssemblyListManager.AssemblyLists
            .Contains(page._assemblyContext.AssemblyList.ListName);
        string? title = page._activeModel?.Title;
        string? selected = tree?.SelectedItem?.Text?.ToString();
        int docLength = ParseDocumentLength(page._decompilerTextView?.GetUnoDebugSnapshot());
        bool bannerVisible = page._updateBanner?.Visibility == Microsoft.UI.Xaml.Visibility.Visible;
        string? activeList = page._assemblyContext.AssemblyList.ListName;

        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"rows\":{rows},");
        sb.Append($"\"listPersisted\":{(listPersisted ? "true" : "false")},");
        sb.Append($"\"documentTitle\":{Json(title)},");
        sb.Append($"\"documentLength\":{docLength},");
        sb.Append($"\"bannerVisible\":{(bannerVisible ? "true" : "false")},");
        sb.Append($"\"activeList\":{Json(activeList)},");
        sb.Append($"\"selectedText\":{Json(selected)}");
        if (note is not null) sb.Append(',').Append(note);
        if (error is not null) sb.Append(',').Append($"\"error\":{Json(error)}");
        sb.Append('}');
        return sb.ToString();
    }

    static string MetadataGridSnapshot(MainPage page, string? nodeText, string tableName)
    {
        var grid = page._nodeContent?.Content as System.Windows.Controls.DataGrid;
        grid?.UpdateLayout();
        var headers = grid is null
            ? ""
            : string.Join(",", grid.Columns.Cast<System.Windows.Controls.DataGridColumn>()
                .Select(c => Json(c.Header?.ToString())));

        var widths = grid is null
            ? ""
            : string.Join(",", grid.Columns.Cast<System.Windows.Controls.DataGridColumn>()
                .Select(c => JsonNumber(EffectiveColumnWidth(c))));

        var autoFilter = grid is not null && DataGridExtensions.DataGridFilter.GetIsAutoFilterEnabled(grid);

        var dataGridType = grid?.GetType().BaseType;
        var headerCellsField = dataGridType?.GetField("_headerCells",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var headerCells = headerCellsField?.GetValue(grid) as System.Collections.IList;
        var headerCellsCount = headerCells?.Count ?? -1;
        var visibleField = dataGridType?.GetField("_visibleColumns",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var visibleList = visibleField?.GetValue(grid) as System.Collections.IList;
        var visibleCount = visibleList?.Count ?? -1;
        var hasFilters = headerCells is null || headerCells.Count == 0
            ? ""
            : string.Join(",", headerCells.Cast<object>()
                .Select(h => h.GetType().GetProperty("Content")?.GetValue(h))
                .Select(c => ContainsFilterButton(c) ? "true" : "false"));

        bool ContainsFilterButton(object? content)
        {
            // WPF-matching header structure wraps content in a 3-column Grid
            // with Thumb grippers; the filter button lives in a nested Grid
            // inside Column 1.  Walk children recursively.
            if (content is Microsoft.UI.Xaml.Controls.Panel panel)
                return panel.Children.OfType<Microsoft.UI.Xaml.Controls.Button>().Any()
                    || panel.Children.OfType<Microsoft.UI.Xaml.Controls.Grid>().Any(g => ContainsFilterButton(g));
            return false;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"table\":{Json(tableName)},");
        sb.Append($"\"nodeText\":{Json(nodeText)},");
        sb.Append($"\"documentTitle\":{Json(page._activeModel?.Title)},");
        sb.Append($"\"contentType\":{Json(page._nodeContent?.Content?.GetType().FullName)},");
        sb.Append($"\"hasGrid\":{(grid is null ? "false" : "true")},");
        sb.Append($"\"rows\":{grid?.Items.Count ?? 0},");
        sb.Append($"\"columns\":{grid?.Columns.Count ?? 0},");
        sb.Append($"\"columnWidths\":[{widths}],");
        sb.Append($"\"headerCellsCount\":{headerCellsCount},");
        sb.Append($"\"visibleColumnsCount\":{visibleCount},");
        sb.Append($"\"hasFilterButtons\":[{hasFilters}],");
        sb.Append($"\"autoGenerateColumns\":{(grid?.AutoGenerateColumns == true ? "true" : "false")},");
        sb.Append($"\"autoFilterEnabled\":{(autoFilter ? "true" : "false")},");
        sb.Append($"\"headers\":[{headers}]");
        sb.Append('}');
        return sb.ToString();
    }

    static string MetadataResizeSnapshot(
        System.Windows.Controls.DataGrid? grid,
        int columnIndex,
        bool resized,
        double before,
        double after,
        string? error,
        double? bestFit = null)
    {
        var column = grid is not null && columnIndex >= 0 && columnIndex < grid.Columns.Count
            ? grid.Columns[columnIndex]
            : null;
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"hasGrid\":{(grid is null ? "false" : "true")},");
        sb.Append($"\"columns\":{grid?.Columns.Count ?? 0},");
        sb.Append($"\"columnIndex\":{columnIndex},");
        sb.Append($"\"header\":{Json(column?.Header?.ToString())},");
        sb.Append($"\"resized\":{(resized ? "true" : "false")},");
        sb.Append($"\"before\":{JsonNumber(before)},");
        sb.Append($"\"after\":{JsonNumber(after)},");
        if (bestFit is not null)
        {
            sb.Append($"\"bestFit\":{JsonNumber(bestFit.Value)},");
        }

        if (column is not null)
        {
            sb.Append($"\"minWidth\":{JsonNumber(column.MinWidth)},");
            sb.Append($"\"maxWidth\":{JsonNumber(column.MaxWidth)},");
        }

        sb.Append($"\"widthUnit\":{Json(column?.Width.UnitType.ToString())}");
        if (error is not null) sb.Append(',').Append($"\"error\":{Json(error)}");
        sb.Append('}');
        return sb.ToString();
    }

    static string JsonNumber(double value)
        => double.IsFinite(value)
            ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "null";

    static double EffectiveColumnWidth(System.Windows.Controls.DataGridColumn column)
    {
        if (column.Width.IsAbsolute)
            return column.Width.Value;
        if (column.ActualWidth > 0)
            return column.ActualWidth;
        if (column.Width.DisplayValue > 0)
            return column.Width.DisplayValue;
        return column.Width.Value;
    }

    static System.Windows.Controls.DataGrid? OpenMetadataGrid(MainPage page, string assemblyPath, string tableName, out string? error)
    {
        error = null;
        if (!page._assemblyContext.AssemblyList.GetAssemblies()
                .Select(a => a.GetMetadataFileOrNull())
                .Any(f => f is not null && string.Equals(f.FileName, assemblyPath, StringComparison.OrdinalIgnoreCase)))
        {
            page._assemblyContext.AssemblyList.OpenAssembly(assemblyPath);
        }

        if (!Enum.TryParse<TableIndex>(tableName, ignoreCase: true, out var table))
        {
            error = $"unknown metadata table '{tableName}'";
            return null;
        }

        var target = page.FindNode(page._assemblyContext.Root, node =>
            node is ICSharpCode.ILSpy.Metadata.MetadataTableTreeNode tableNode
            && tableNode.Kind == table);
        if (target is null)
        {
            error = $"metadata table '{table}' not found";
            return null;
        }

        page.OnTreeNodeSelected(target);
        if (page._nodeContent?.Content is not System.Windows.Controls.DataGrid grid)
        {
            error = "metadata table did not render a DataGrid";
            return null;
        }

        return grid;
    }

    static System.Windows.Controls.Primitives.DataGridColumnHeader? MetadataHeaderAt(
        System.Windows.Controls.DataGrid grid,
        int columnIndex)
    {
        var headerCells = typeof(System.Windows.Controls.DataGrid).GetField(
            "_headerCells",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(grid)
            as System.Collections.IList;

        return columnIndex >= 0 && columnIndex < headerCells?.Count
            ? headerCells[columnIndex] as System.Windows.Controls.Primitives.DataGridColumnHeader
            : null;
    }

    static System.Windows.Controls.Primitives.Thumb? HeaderGripper(
        System.Windows.Controls.Primitives.DataGridColumnHeader header,
        string fieldName)
        => typeof(System.Windows.Controls.Primitives.DataGridColumnHeader).GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(header)
            as System.Windows.Controls.Primitives.Thumb;

    static bool HeaderGripperHasCursor(System.Windows.Controls.Primitives.Thumb gripper)
    {
        gripper.ApplyTemplate();
        return typeof(System.Windows.Controls.Primitives.Thumb).GetProperty(
            "HasShimCursor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(gripper)
            as bool? == true;
    }

    static string MetadataClipboardSnapshot(
        System.Windows.Controls.DataGrid? grid,
        bool copied,
        string? text,
        string? csv,
        object? item,
        string? error)
    {
        var firstLine = text?.Split(["\r\n", "\n"], StringSplitOptions.None).FirstOrDefault();
        var secondLine = text?.Split(["\r\n", "\n"], StringSplitOptions.None).Skip(1).FirstOrDefault();
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"hasGrid\":{(grid is null ? "false" : "true")},");
        sb.Append($"\"rows\":{grid?.Items.Count ?? 0},");
        sb.Append($"\"columns\":{grid?.Columns.Count ?? 0},");
        sb.Append($"\"selectedItems\":{grid?.SelectedItems.Count ?? 0},");
        sb.Append($"\"copied\":{(copied ? "true" : "false")},");
        sb.Append($"\"textLength\":{text?.Length ?? 0},");
        sb.Append($"\"csvLength\":{csv?.Length ?? 0},");
        sb.Append($"\"firstLine\":{Json(firstLine)},");
        sb.Append($"\"secondLine\":{Json(secondLine)},");
        sb.Append($"\"itemType\":{Json(item?.GetType().FullName)}");
        if (error is not null) sb.Append(',').Append($"\"error\":{Json(error)}");
        sb.Append('}');
        return sb.ToString();
    }

    static string MetadataKeyboardSelectionSnapshot(
        System.Windows.Controls.DataGrid? grid,
        bool selected,
        bool moved,
        string? beforeHeader,
        string? afterHeader,
        string? error)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"hasGrid\":{(grid is null ? "false" : "true")},");
        sb.Append($"\"rows\":{grid?.Items.Count ?? 0},");
        sb.Append($"\"columns\":{grid?.Columns.Count ?? 0},");
        sb.Append($"\"selectedCells\":{grid?.SelectedCells.Count ?? 0},");
        sb.Append($"\"selectedItems\":{grid?.SelectedItems.Count ?? 0},");
        sb.Append($"\"selectionUnit\":{Json(grid?.SelectionUnit.ToString())},");
        sb.Append($"\"selected\":{(selected ? "true" : "false")},");
        sb.Append($"\"moved\":{(moved ? "true" : "false")},");
        sb.Append($"\"beforeHeader\":{Json(beforeHeader)},");
        sb.Append($"\"afterHeader\":{Json(afterHeader)}");
        if (error is not null) sb.Append(',').Append($"\"error\":{Json(error)}");
        sb.Append('}');
        return sb.ToString();
    }

    static string HeaderDetailsSnapshot(
        MainPage page,
        string headerName,
        string memberName,
        System.Windows.Controls.DataGrid? grid,
        Microsoft.UI.Xaml.Controls.DataTemplateSelector? selector,
        Microsoft.UI.Xaml.DataTemplate? template,
        System.Windows.Controls.DataGrid? detailsGrid,
        string? error)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"header\":{Json(headerName)},");
        sb.Append($"\"member\":{Json(memberName)},");
        sb.Append($"\"documentTitle\":{Json(page._activeModel?.Title)},");
        sb.Append($"\"contentType\":{Json(page._nodeContent?.Content?.GetType().FullName)},");
        sb.Append($"\"hasGrid\":{(grid is null ? "false" : "true")},");
        sb.Append($"\"rows\":{grid?.Items.Count ?? 0},");
        sb.Append($"\"columns\":{grid?.Columns.Count ?? 0},");
        sb.Append($"\"rowDetailsMode\":{Json(grid?.RowDetailsVisibilityMode.ToString())},");
        sb.Append($"\"hasSelector\":{(selector is null ? "false" : "true")},");
        sb.Append($"\"selectorType\":{Json(selector?.GetType().FullName)},");
        sb.Append($"\"templateType\":{Json(template?.GetType().FullName)},");
        sb.Append($"\"detailsGrid\":{(detailsGrid is null ? "false" : "true")},");
        sb.Append($"\"detailsRows\":{detailsGrid?.Items.Count ?? 0},");
        sb.Append($"\"detailsColumns\":{detailsGrid?.Columns.Count ?? 0}");
        if (error is not null) sb.Append(',').Append($"\"error\":{Json(error)}");
        sb.Append('}');
        return sb.ToString();
    }

    static string CustomDebugDetailsSnapshot(
        MainPage page,
        System.Windows.Controls.DataGrid? grid,
        Microsoft.UI.Xaml.Controls.DataTemplateSelector? selector,
        Microsoft.UI.Xaml.DataTemplate? template,
        Microsoft.UI.Xaml.FrameworkElement? detailsElement,
        object? item,
        string? error)
    {
        var detailsGrid = detailsElement as System.Windows.Controls.DataGrid;
        var textBox = detailsElement as Microsoft.UI.Xaml.Controls.TextBox;

        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"table\":\"CustomDebugInformation\",");
        sb.Append($"\"documentTitle\":{Json(page._activeModel?.Title)},");
        sb.Append($"\"contentType\":{Json(page._nodeContent?.Content?.GetType().FullName)},");
        sb.Append($"\"hasGrid\":{(grid is null ? "false" : "true")},");
        sb.Append($"\"rows\":{grid?.Items.Count ?? 0},");
        sb.Append($"\"columns\":{grid?.Columns.Count ?? 0},");
        sb.Append($"\"rowKind\":{Json(ReadBindingString(item, "Kind"))},");
        sb.Append($"\"hasSelector\":{(selector is null ? "false" : "true")},");
        sb.Append($"\"selectorType\":{Json(selector?.GetType().FullName)},");
        sb.Append($"\"templateType\":{Json(template?.GetType().FullName)},");
        sb.Append($"\"detailsElementType\":{Json(detailsElement?.GetType().FullName)},");
        sb.Append($"\"detailsGrid\":{(detailsGrid is null ? "false" : "true")},");
        sb.Append($"\"detailsRows\":{detailsGrid?.Items.Count ?? 0},");
        sb.Append($"\"detailsColumns\":{detailsGrid?.Columns.Count ?? 0},");
        sb.Append($"\"detailsTextLength\":{textBox?.Text?.Length ?? 0}");
        if (error is not null) sb.Append(',').Append($"\"error\":{Json(error)}");
        sb.Append('}');
        return sb.ToString();
    }

    static string? ReadBindingString(object? item, string path)
        => ReadBindingValue(item, path)?.ToString();

    static object? ReadBindingValue(object? item, string path)
        => System.Windows.Data.BindingEvaluator.Evaluate(item, new System.Windows.Data.Binding(path));

    static int ParseDocumentLength(string? snapshot)
    {
        if (snapshot is null) return -1;
        foreach (var line in snapshot.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("documentLength=", StringComparison.Ordinal)
                && int.TryParse(t.Substring("documentLength=".Length), out var n))
                return n;
        }
        return -1;
    }

    static string Json(string? s)
    {
        if (s is null) return "null";
        var sb = new System.Text.StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    static string JsonArray(System.Collections.Generic.IEnumerable<string>? values)
        => values is null ? "" : string.Join(",", values.Select(Json));
}
#endif
