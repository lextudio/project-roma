using System.Text.Json;

using Xunit;

namespace Roma.IntegrationTests;

// End-to-end behaviors locked down via the DevFlow probe surface. Each test establishes its own
// precondition (open/clear/wipe+restart) so it does not depend on test ordering, even though the
// whole collection shares a single (expensive) Roma process.
[Collection("Roma app")]
public sealed class RomaIntegrationTests
{
    // A real .NET assembly that's always present to use as test input.
    const string SampleAssembly = @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\9.0.4\System.Net.Http.dll";

    readonly RomaAppFixture _app;
    public RomaIntegrationTests(RomaAppFixture app) => _app = app;

    static int Rows(JsonElement s) => s.GetProperty("rows").GetInt32();
    static string Title(JsonElement s) => s.GetProperty("documentTitle").GetString() ?? "";
    static int DocLength(JsonElement s) => s.GetProperty("documentLength").GetInt32();
    static string Selected(JsonElement s) => s.GetProperty("selectedText").GetString() ?? "";

    [Fact]
    public async Task Open_AddsAssemblyRow()
    {
        await _app.InvokeAsync("roma.probe.clear");
        var state = await _app.InvokeAsync("roma.probe.open", SampleAssembly);

        Assert.True(Rows(state) >= 1, $"expected >=1 row after open, got {Rows(state)}");
    }

    [Fact]
    public async Task SelectRow_PopulatesDocument()
    {
        await _app.InvokeAsync("roma.probe.clear");
        await _app.InvokeAsync("roma.probe.open", SampleAssembly);
        await _app.InvokeAsync("roma.probe.select-row", 0);

        // Decompilation is async — poll until the document has content.
        var state = await _app.PollAsync("roma.probe.state", s => DocLength(s) > 0);

        Assert.True(DocLength(state) > 0, $"expected decompiled content, got documentLength={DocLength(state)}");
        Assert.False(string.IsNullOrEmpty(Title(state)), "expected a non-empty document title");
    }

    [Fact]
    public async Task DeleteSelected_AutoSelectsNextAssembly()
    {
        // Two distinct top-level assemblies so there is a "next" to fall to.
        await _app.InvokeAsync("roma.probe.clear");
        await _app.InvokeAsync("roma.probe.open", SampleAssembly);
        var seeded = await _app.InvokeAsync("roma.probe.open",
            @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\9.0.4\System.Linq.dll");
        Assert.True(Rows(seeded) >= 2, $"need >=2 rows to test next-selection, got {Rows(seeded)}");

        await _app.InvokeAsync("roma.probe.select-row", 0);
        var deletedState = await _app.InvokeAsync("roma.probe.delete-selected");

        Assert.True(deletedState.GetProperty("ok").GetBoolean(), "delete should succeed");
        Assert.False(string.IsNullOrEmpty(Selected(deletedState)), "a next assembly should be auto-selected");
        var deletedName = deletedState.GetProperty("deleted").GetString();
        Assert.NotEqual(deletedName, Selected(deletedState));
    }

    [Fact]
    public async Task DeleteLastAssembly_ResetsDocumentToNewTab()
    {
        await _app.InvokeAsync("roma.probe.clear");
        await _app.InvokeAsync("roma.probe.open", SampleAssembly);
        await _app.InvokeAsync("roma.probe.select-row", 0);
        await _app.PollAsync("roma.probe.state", s => DocLength(s) > 0); // ensure something is shown first

        await _app.InvokeAsync("roma.probe.clear"); // remove the last assembly

        // The reset-to-New-Tab is marshalled onto the UI thread, so poll for it.
        var state = await _app.PollAsync("roma.probe.state", s => Title(s) == "New Tab" && DocLength(s) == 0);

        Assert.Equal("New Tab", Title(state));
        Assert.Equal(0, DocLength(state));
        Assert.Equal(0, Rows(state));
    }

    [Fact]
    public async Task EmptiedList_StaysBlankAfterRestart()
    {
        await _app.InvokeAsync("roma.probe.clear");
        await Task.Delay(1500); // let the AssemblyList auto-save (BeginInvoke) flush to settings

        await _app.RestartAsync();

        var state = await _app.InvokeAsync("roma.probe.state");
        Assert.Equal(0, Rows(state));
        Assert.True(state.GetProperty("listPersisted").GetBoolean(), "an emptied list should remain persisted");
    }

    [Fact]
    public async Task ShowAbout_DisplaysAboutPage()
    {
        var state = await _app.InvokeAsync("roma.probe.show-about");
        Assert.Equal("About", Title(state));
        Assert.True(DocLength(state) > 0, $"About page should have content, got documentLength={DocLength(state)}");
    }

    [Fact]
    public async Task UpdateBanner_ShowsWithMessage()
    {
        var state = await _app.InvokeAsync("roma.probe.update-banner", "A new version is available.", true);
        Assert.True(state.GetProperty("bannerVisible").GetBoolean(), "update banner should be visible after being shown");
    }

    [Fact]
    public async Task BlankStartup_ShowsAboutPage()
    {
        await _app.InvokeAsync("roma.probe.clear");
        await Task.Delay(1500); // let the emptied list auto-save before restart
        await _app.RestartAsync();

        // The About page is shown once the document tab's view is realized (deferred via the pending
        // flag), which can lag after a restart — poll generously.
        var state = await _app.PollAsync("roma.probe.state", s => Title(s) == "About", timeoutMs: 20000);
        Assert.Equal("About", Title(state));
        Assert.Equal(0, Rows(state));
    }

    [Fact]
    public async Task UnoPlatformPreset_IsCrossPlatformAndPopulated()
    {
        var result = await _app.InvokeAsync("roma.probe.create-preset", "Uno Platform");
        Assert.True(result.GetProperty("createdRows").GetInt32() > 0,
            "the cross-platform Uno Platform preset should populate from the runtime dir");
        var names = result.GetProperty("lists").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("Uno Platform", names);
    }

    [Fact]
    public async Task SwitchList_RebuildsTree()
    {
        await _app.InvokeAsync("roma.probe.create-preset", "Uno Platform");
        await _app.InvokeAsync("roma.probe.create-list", "Switch Target");

        var uno = await _app.InvokeAsync("roma.probe.switch-list", "Uno Platform");
        Assert.Equal("Uno Platform", uno.GetProperty("activeList").GetString());
        Assert.True(Rows(uno) > 0, "switching to a populated list should rebuild the tree with its assemblies");

        // Switching to a different (empty) list changes the active list and clears the tree.
        var other = await _app.InvokeAsync("roma.probe.switch-list", "Switch Target");
        Assert.Equal("Switch Target", other.GetProperty("activeList").GetString());
        Assert.Equal(0, Rows(other));
    }

    [Fact]
    public async Task ManageAssemblyListsDialog_Constructs()
    {
        var result = await _app.InvokeAsync("roma.probe.manage-dialog-builds");
        Assert.True(result.GetProperty("built").GetBoolean());
        Assert.True(result.GetProperty("presetCount").GetInt32() >= 4, "expected the 4 presets incl. Uno Platform");
    }

    [Fact]
    public async Task FirstRun_SeedsUnoPlatformAsActiveList()
    {
        _app.WipeSettings();
        await _app.RestartAsync();

        // The "Roma" demo list is gone; first run seeds the cross-platform "Uno Platform" list and
        // makes it active (Windows additionally gets the ILSpy GAC default lists).
        var state = await _app.InvokeAsync("roma.probe.state");
        Assert.Equal("Uno Platform", state.GetProperty("activeList").GetString());
        Assert.True(Rows(state) >= 1, $"Uno Platform should seed its core assemblies, got {Rows(state)} rows");

        var lists = await _app.InvokeAsync("roma.probe.lists");
        var names = lists.GetProperty("lists").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.DoesNotContain("Roma", names);
    }

    [Fact]
    public async Task NavigateToReference_JumpsToTypeNode()
    {
        // Load a well-known assembly and navigate to its first TypeDef (token 0x02000001).
        // The probe calls HandleNavigateToReference, which should now drill to the type node
        // (not just select the assembly) — verified by documentTitle changing away from the
        // assembly name to the resolved type name.
        await _app.InvokeAsync("roma.probe.clear");
        await _app.InvokeAsync("roma.probe.open", SampleAssembly);
        await _app.InvokeAsync("roma.probe.select-row", 0);
        await _app.PollAsync("roma.probe.state", s => DocLength(s) > 0);

        // TypeDef table RID 1 — the first type in System.Net.Http.dll (usually "<Module>").
        // RID 2 (0x02000002) is more likely to be a real navigable class.
        var before = await _app.InvokeAsync("roma.probe.state");
        var nav = await _app.InvokeAsync("roma.probe.navigate", SampleAssembly, "02000002");

        // If the error field is absent, navigation succeeded.
        Assert.False(nav.TryGetProperty("error", out _),
            $"navigation probe returned error: {(nav.TryGetProperty("error", out var err) ? err.GetString() : "none")}");

        // After navigation the document should update — poll to let async decompile settle.
        var after = await _app.PollAsync("roma.probe.state", s => DocLength(s) > 0 && Title(s) != Title(before));
        Assert.NotEqual(Title(before), Title(after));
    }

    [Fact]
    public async Task MetadataTable_RendersDataGrid()
    {
        await _app.InvokeAsync("roma.probe.clear");
        var assemblyPath = typeof(System.Net.Http.HttpClient).Assembly.Location;

        var state = await _app.InvokeAsync("roma.probe.metadata-open-table", assemblyPath, "TypeDef");

        Assert.Equal("TypeDef", state.GetProperty("table").GetString());
        var raw = state.ToString();
        Assert.True(state.GetProperty("hasGrid").GetBoolean(), $"metadata table View() should render a DataGrid: {raw}");
        Assert.True(state.GetProperty("rows").GetInt32() > 0, $"TypeDef table should contain rows: {raw}");
        Assert.True(state.GetProperty("columns").GetInt32() > 0, $"TypeDef table should generate columns: {raw}");
        Assert.True(state.GetProperty("autoGenerateColumns").GetBoolean(), $"metadata tables should use AutoGenerateColumns: {raw}");
        Assert.True(state.GetProperty("autoFilterEnabled").GetBoolean(), $"metadata tables should enable DataGridExtensions auto-filter: {raw}");
        Assert.NotEmpty(state.GetProperty("headers").EnumerateArray());
    }

    [Fact]
    public async Task MetadataHeader_RowDetailsRendersNestedDataGrid()
    {
        await _app.InvokeAsync("roma.probe.clear");
        var assemblyPath = typeof(System.Net.Http.HttpClient).Assembly.Location;

        var state = await _app.InvokeAsync("roma.probe.metadata-header-row-details", assemblyPath, "COFF Header");

        var raw = state.ToString();
        Assert.Equal("COFF Header", state.GetProperty("header").GetString());
        Assert.Equal("Characteristics", state.GetProperty("member").GetString());
        Assert.True(state.GetProperty("hasGrid").GetBoolean(), $"header should render a DataGrid: {raw}");
        Assert.True(state.GetProperty("rows").GetInt32() > 0, $"header grid should contain rows: {raw}");
        Assert.True(state.GetProperty("columns").GetInt32() > 0, $"header grid should contain columns: {raw}");
        Assert.True(state.GetProperty("hasSelector").GetBoolean(), $"row-details selector should be active: {raw}");
        Assert.Contains("CharacteristicsDataTemplateSelector", state.GetProperty("selectorType").GetString());
        Assert.Contains("ShimDataTemplate", state.GetProperty("templateType").GetString());
        Assert.True(state.GetProperty("detailsGrid").GetBoolean(), $"row details should render nested DataGrid: {raw}");
        Assert.True(state.GetProperty("detailsRows").GetInt32() > 0, $"nested details grid should contain rows: {raw}");
        Assert.True(state.GetProperty("detailsColumns").GetInt32() > 0, $"nested details grid should contain columns: {raw}");
    }

    [Fact]
    public async Task MetadataOptionalHeader_RowDetailsRendersNestedDataGrid()
    {
        await _app.InvokeAsync("roma.probe.clear");
        var assemblyPath = typeof(System.Net.Http.HttpClient).Assembly.Location;

        var state = await _app.InvokeAsync("roma.probe.metadata-header-row-details", assemblyPath, "Optional Header");

        var raw = state.ToString();
        Assert.Equal("Optional Header", state.GetProperty("header").GetString());
        Assert.Equal("DLL Characteristics", state.GetProperty("member").GetString());
        Assert.True(state.GetProperty("hasGrid").GetBoolean(), $"optional header should render a DataGrid: {raw}");
        Assert.True(state.GetProperty("rows").GetInt32() > 0, $"optional header grid should contain rows: {raw}");
        Assert.True(state.GetProperty("columns").GetInt32() > 0, $"optional header grid should contain columns: {raw}");
        Assert.True(state.GetProperty("hasSelector").GetBoolean(), $"row-details selector should be active: {raw}");
        Assert.Contains("CharacteristicsDataTemplateSelector", state.GetProperty("selectorType").GetString());
        Assert.Contains("ShimDataTemplate", state.GetProperty("templateType").GetString());
        Assert.True(state.GetProperty("detailsGrid").GetBoolean(), $"row details should render nested DataGrid: {raw}");
        Assert.True(state.GetProperty("detailsRows").GetInt32() > 0, $"nested details grid should contain rows: {raw}");
        Assert.True(state.GetProperty("detailsColumns").GetInt32() > 0, $"nested details grid should contain columns: {raw}");
    }

    [Fact]
    public async Task MetadataCustomDebugInformation_RowDetailsRenders()
    {
        await _app.InvokeAsync("roma.probe.clear");
        var assemblyPath = typeof(RomaIntegrationTests).Assembly.Location;

        var state = await _app.InvokeAsync("roma.probe.metadata-custom-debug-row-details", assemblyPath);

        var raw = state.ToString();
        Assert.False(state.TryGetProperty("error", out _), $"custom debug row details probe failed: {raw}");
        Assert.Equal("CustomDebugInformation", state.GetProperty("table").GetString());
        Assert.True(state.GetProperty("hasGrid").GetBoolean(), $"CustomDebugInformation should render a DataGrid: {raw}");
        Assert.True(state.GetProperty("rows").GetInt32() > 0, $"CustomDebugInformation should contain rows: {raw}");
        Assert.True(state.GetProperty("hasSelector").GetBoolean(), $"row-details selector should be active: {raw}");
        Assert.Contains("CustomDebugInformationDetailsTemplateSelector", state.GetProperty("selectorType").GetString());
        Assert.Contains("ShimDataTemplate", state.GetProperty("templateType").GetString());

        var renderedGrid = state.GetProperty("detailsGrid").GetBoolean();
        var renderedText = state.GetProperty("detailsTextLength").GetInt32() > 0;
        Assert.True(renderedGrid || renderedText, $"row details should render a nested grid or text blob: {raw}");
    }

    [Fact]
    public async Task MetadataTableViews_ReportsUpstreamXamlResourceTranslation()
    {
        var state = await _app.InvokeAsync("roma.probe.metadata-xaml-resources");

        var raw = state.ToString();
        Assert.True(state.GetProperty("xamlPresent").GetBoolean(), $"MetadataTableViews.xaml should be copied to output: {raw}");

        var translated = state.GetProperty("translated").EnumerateArray().Select(x => x.GetString()).ToArray();
        var fallback = state.GetProperty("fallback").EnumerateArray().Select(x => x.GetString()).ToArray();
        var skipped = state.GetProperty("skipped").EnumerateArray().Select(x => x.GetString()).ToArray();

        Assert.Contains("DataGridCellStyle", translated);
        Assert.Contains("DefaultFilter", translated);
        Assert.Contains("CustomDebugInformationDetailsTextBlob", translated);
        Assert.Contains("CustomDebugInformationDetailsDataGrid", translated);
        Assert.Contains("HeaderFlagsDetailsDataGrid", translated);
        Assert.DoesNotContain("CustomDebugInformationDetailsDataGrid", fallback);
        Assert.DoesNotContain("CustomDebugInformationDetailsTextBlob", fallback);
        Assert.DoesNotContain("HeaderFlagsDetailsDataGrid", fallback);
        Assert.Contains("byteWidthConverter", skipped);
    }
}
