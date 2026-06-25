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
