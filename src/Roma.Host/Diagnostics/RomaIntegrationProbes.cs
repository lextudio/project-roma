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
        var autoFilter = grid is not null && DataGridExtensions.DataGridFilter.GetIsAutoFilterEnabled(grid);

        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        sb.Append($"\"table\":{Json(tableName)},");
        sb.Append($"\"nodeText\":{Json(nodeText)},");
        sb.Append($"\"documentTitle\":{Json(page._activeModel?.Title)},");
        sb.Append($"\"contentType\":{Json(page._nodeContent?.Content?.GetType().FullName)},");
        sb.Append($"\"hasGrid\":{(grid is null ? "false" : "true")},");
        sb.Append($"\"rows\":{grid?.Items.Count ?? 0},");
        sb.Append($"\"columns\":{grid?.Columns.Count ?? 0},");
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
        string? error)
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
        sb.Append($"\"before\":{before.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
        sb.Append($"\"after\":{after.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
        sb.Append($"\"widthUnit\":{Json(column?.Width.UnitType.ToString())}");
        if (error is not null) sb.Append(',').Append($"\"error\":{Json(error)}");
        sb.Append('}');
        return sb.ToString();
    }

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
