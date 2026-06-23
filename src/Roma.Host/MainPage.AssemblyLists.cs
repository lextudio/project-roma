using System.Linq;

using ICSharpCode.ILSpy.TreeNodes;
using ICSharpCode.ILSpyX;

using Roma.Host.Analyzers;

using Microsoft.UI.Xaml;

namespace Roma.Host;

// Multi-list support (session 34): switching the active assembly list at runtime and the Manage
// Assembly Lists dialog. ILSpy rebuilds its window around a new AssemblyListTreeNode; Roma instead
// rebuilds just the assembly-browser tree and swaps it into the (observable) browser pane Content,
// reusing all the live document/analyzer wiring.
public sealed partial class MainPage
{
    // Guards against re-entrancy when ReloadActiveAssemblyList sets the combo selection, which would
    // otherwise fire OnAssemblyListSelectionChanged → ReloadActiveAssemblyList again.
    private bool _switchingList;

    // Loads the named list and rebinds the UI to it: swaps the context's Root/AssemblyList, re-inits
    // the analyzer, rebuilds the browser tree, resets the document, and records the choice.
    internal void ReloadActiveAssemblyList(string name)
    {
        if (_switchingList)
            return;
        var manager = _assemblyContext.AssemblyListManager;
        if (string.IsNullOrEmpty(name) || !manager.AssemblyLists.Contains(name))
            return;
        if (string.Equals(name, _assemblyContext.AssemblyList.ListName, System.StringComparison.Ordinal)
            && _browserPaneModel is not null)
            return; // already active

        _switchingList = true;
        try
        {
            var newList = manager.LoadList(name);
            newList.UseDebugSymbols = true;
            // Route the new list through the model so it keeps owning the displayed tree (model.Root),
            // matching the unified-tree setup from RomaAssemblyTree. Falls back to a standalone root if
            // the model isn't available yet (shouldn't happen post-init).
            AssemblyListTreeNode newRoot;
            if (_assemblyTreeModel is not null)
            {
                _assemblyTreeModel.ShowAssemblyListForHost(newList);
                newRoot = (AssemblyListTreeNode)_assemblyTreeModel.Root!;
            }
            else
            {
                newRoot = new AssemblyListTreeNode(newList);
            }
            _assemblyContext.SwitchList(newRoot, newList);
            _assemblyContext.SettingsService.SessionSettings.ActiveAssemblyList = name;

            // The export provider serves the analyzer tree from RomaAnalyzerContext.AssemblyList.
            RomaAnalyzerContext.Initialize(_assemblyContext.LanguageService, newList);

            // Rebuild the assembly-browser tree on the new root and show it in the pane. This also
            // re-points _assemblyTree and re-subscribes the empty-list → reset handler to the new root.
            if (_browserPaneModel is not null)
                _browserPaneModel.Content = CreateAssemblyBrowserContent();

            // Clear stale document content (it may reference assemblies no longer loaded). Show the
            // About page when the new list is empty, mirroring the blank-tree startup behavior.
            if (!newList.GetAssemblies().Any())
                ShowAboutPage();
            else
                ResetToNewTab();

            SyncAssemblyListCombo(name);
        }
        finally
        {
            _switchingList = false;
        }
    }

    private void SyncAssemblyListCombo(string name)
    {
        if (_assemblyListCombo is not null && !Equals(_assemblyListCombo.SelectedItem, name))
            _assemblyListCombo.SelectedItem = name;
    }

    // Opens the Manage Assembly Lists dialog; on "Open"/double-click it activates the chosen list.
    private async void ShowManageAssemblyListsDialog()
    {
        var dialog = new Options.ManageAssemblyListsDialog(
            _assemblyContext.AssemblyListManager,
            name => ReloadActiveAssemblyList(name))
        {
            XamlRoot = XamlRoot,
        };
        // Preselect the active list.
        await dialog.ShowAsync();
    }
}
