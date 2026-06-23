using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.ILSpyX.TreeView;
using Microsoft.UI.Xaml.Controls;

namespace ICSharpCode.ILSpy.Controls.TreeView
{
    // Roma implementation of SharpTreeView on top of WinUI TreeView.
    // Exposes the API surface the linked ILSpy code consumes — GetTopLevelSelection()
    // (TextViewContext.Create), LockUpdates() (ReloadAssembly), and the
    // SelectedItem(s)/ScrollIntoView/FocusNode surface AssemblyListPane-style callers use —
    // without porting the full WPF control. The node↔TreeViewNode maps are populated by
    // SharpTreeViewAdapter as it materializes the tree.
    public sealed class SharpTreeView : Microsoft.UI.Xaml.Controls.TreeView
    {
        private readonly Dictionary<SharpTreeNode, TreeViewNode> _forward = new();
        private readonly Dictionary<TreeViewNode, SharpTreeNode> _reverse = new();
        private readonly List<SharpTreeNode> _selectedNodes = new();

        internal void Register(SharpTreeNode sharpNode, TreeViewNode treeViewNode)
        {
            _forward[sharpNode] = treeViewNode;
            _reverse[treeViewNode] = sharpNode;
        }

        internal SharpTreeNode? NodeFor(TreeViewNode tvn)
            => _reverse.TryGetValue(tvn, out var n) ? n : null;

        internal TreeViewNode? TreeViewNodeFor(SharpTreeNode node)
            => _forward.TryGetValue(node, out var tvn) ? tvn : null;

        internal void Unregister(SharpTreeNode node)
        {
            if (_forward.TryGetValue(node, out var tvn))
            {
                _forward.Remove(node);
                _reverse.Remove(tvn);
                // Drop any selection pointing at the departing node. Crucial after AssemblyList.Unload/
                // Clear: the node's LoadedAssembly metadata is disposed, so later reads of its Text
                // (which decodes the assembly version) would hit freed memory and crash the process
                // with an AccessViolationException (uncatchable). Clearing selection here prevents that.
                if (ReferenceEquals(SelectedNode, tvn))
                    SelectedNode = null;
            }
            _selectedNodes.Remove(node);
        }

        // Called by the adapter's selection handler to keep the SharpTreeNode selection
        // in sync with the WinUI TreeView (single-select today, list-ready for the future).
        internal void SetSelection(IEnumerable<SharpTreeNode> nodes)
        {
            _selectedNodes.Clear();
            _selectedNodes.AddRange(nodes);
        }

        public IReadOnlyList<SharpTreeNode> SelectedItems => _selectedNodes;

        public SharpTreeNode? SelectedItem => _selectedNodes.Count > 0 ? _selectedNodes[0] : null;

        // Mirrors SharpTreeView.GetTopLevelSelection(): the selected nodes that are not
        // descendants of another selected node. With single selection this is just the node.
        public IEnumerable<SharpTreeNode> GetTopLevelSelection()
        {
            var selectionHash = new HashSet<SharpTreeNode>(_selectedNodes);
            return _selectedNodes.Where(item => item.Ancestors().All(a => !selectionHash.Contains(a)));
        }

        // Mirrors ILSpy's SharpTreeView ApplicationCommands.Delete binding: Del removes the
        // top-level selected nodes that allow deletion. For AssemblyTreeNode, Delete() →
        // AssemblyList.Unload(), whose CollectionChanged drives the tree to drop the row. Using
        // the generic SharpTreeNode.CanDelete()/Delete() keeps this working for any deletable
        // node type, exactly as upstream.
        protected override void OnKeyDown(Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Delete && DeleteSelectedNodes())
            {
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }

        // Deletes the top-level selected nodes if they all allow it (ILSpy's
        // HandleExecuted_Delete + HandleCanExecute_Delete). After deletion, selects the node now at
        // the deleted position (the next sibling, or the new last) and focuses it — matching ILSpy,
        // which restores selection to flattener[selectedIndex] after a Delete. Returns true if
        // anything was deleted.
        public bool DeleteSelectedNodes()
        {
            var nodes = GetTopLevelSelection().ToList();
            if (nodes.Count == 0 || !nodes.All(n => n.CanDelete()))
                return false;

            // Remember where the (first) deleted node sat among the top-level rows. Removal runs
            // synchronously (AssemblyList.Unload → CollectionChanged → the adapter drops the row),
            // so RootNodes is up to date once Delete() returns.
            int selectIndex = TreeViewNodeFor(nodes[0]) is { } tvn ? RootNodes.IndexOf(tvn) : -1;

            foreach (var node in nodes)
                node.Delete();

            if (selectIndex >= 0 && RootNodes.Count > 0)
                SelectAndFocus(RootNodes[Math.Min(selectIndex, RootNodes.Count - 1)]);
            return true;
        }

        // Programmatically selects + focuses a top-level row. Setting SelectedNode raises
        // SelectionChanged, which the adapter routes to its onSelect callback (decompile/activate).
        private void SelectAndFocus(TreeViewNode tvn)
        {
            SelectedNode = tvn;
            if (NodeFor(tvn) is { } node)
                SetSelection(new[] { node });
        }

        public IDisposable LockUpdates() => NoOpDisposable.Instance;

        public void ScrollIntoView(SharpTreeNode node) { }

        public void FocusNode(SharpTreeNode node) { }

        private sealed class NoOpDisposable : IDisposable
        {
            public static readonly NoOpDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
