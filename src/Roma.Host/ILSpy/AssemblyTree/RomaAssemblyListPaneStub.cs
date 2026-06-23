using System;
using System.Linq;

using ICSharpCode.ILSpyX.TreeView;

namespace ICSharpCode.ILSpy.AssemblyTree
{
    // AssemblyListPane is the WPF XAML view (AssemblyListPane.xaml + codebehind extending the WPF
    // SharpTreeView). Roma renders the tree via its own WinUI SharpTreeView, so the upstream view
    // isn't linked. AssemblyTreeModel holds an `AssemblyListPane? activeView` and calls
    // ScrollIntoView/FocusNode/LockUpdates on it from SelectNode. This Roma implementation forwards
    // those to the live WinUI SharpTreeView so reference jumps / Back-Forward / session restore reveal
    // and highlight the target node in the tree (the WinUI TreeView materializes children lazily on
    // expand, so the ancestor chain must be expanded top-down before the node can be selected).
    public class AssemblyListPane
    {
        private readonly ICSharpCode.ILSpy.Controls.TreeView.SharpTreeView _tree;

        public AssemblyListPane(ICSharpCode.ILSpy.Controls.TreeView.SharpTreeView tree)
        {
            _tree = tree;
        }

        public void ScrollIntoView(SharpTreeNode node) => RevealAndSelect(node);

        public void FocusNode(SharpTreeNode node) => RevealAndSelect(node);

        private void RevealAndSelect(SharpTreeNode node)
        {
            try
            {
                // Root-first ancestor chain (the invisible AssemblyListTreeNode root has no TreeViewNode).
                foreach (var ancestor in node.AncestorsAndSelf().Reverse())
                {
                    if (ReferenceEquals(ancestor, node))
                        continue;
                    if (_tree.TreeViewNodeFor(ancestor) is { } tvn)
                        tvn.IsExpanded = true;
                }
                if (_tree.TreeViewNodeFor(node) is { } target)
                {
                    _tree.SelectedNode = target;
                    _tree.SetSelection([node]);
                }
            }
            catch
            {
                // Tree highlight is best-effort; never let it break navigation/display.
            }
        }

        public IDisposable LockUpdates() => NoOpDisposable.Instance;

        private sealed class NoOpDisposable : IDisposable
        {
            public static readonly NoOpDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
