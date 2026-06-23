using System;
using System.Collections.Generic;
using System.Collections.Specialized;

using ICSharpCode.ILSpyX.TreeView;

using Microsoft.UI.Xaml.Controls;

namespace Roma.Host.Analyzers;

public sealed partial class AnalyzerPane : UserControl
{
    readonly RomaAnalyzerViewModel _vm;
    readonly Dictionary<TreeViewNode, SharpTreeNode> _map = [];
    // Per-model-node CollectionChanged subscription, so async analyzer results (added on a background
    // thread via ThreadingSupport) are mirrored into the WinUI tree — otherwise the node stays on
    // "Loading...".
    readonly Dictionary<SharpTreeNode, NotifyCollectionChangedEventHandler> _childHandlers = [];
    readonly TreeView _tree;

    public AnalyzerPane(RomaAnalyzerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();

        _tree = new TreeView { SelectionMode = TreeViewSelectionMode.Single };
        var template = SharpTreeViewAdapter.BuildItemTemplate();
        if (template is not null) _tree.ItemTemplate = template;

        _tree.Expanding += OnTreeExpanding;
        _tree.SelectionChanged += OnTreeSelectionChanged;

        _vm.Root.Children.CollectionChanged += OnRootChildrenChanged;

        _treeHost.Content = _tree;
    }

    private void OnRootChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (e.NewItems != null)
                foreach (SharpTreeNode n in e.NewItems)
                    _tree.RootNodes.Add(CreateNode(n));
            if (e.OldItems != null)
                foreach (SharpTreeNode n in e.OldItems)
                    RemoveRootNode(n);
        });
    }

    // On expand, materialize the model node's children (synchronous loads land immediately; async
    // analyzer searches add a "Loading..." node and then stream results, both mirrored by the
    // CollectionChanged handler wired in CreateNode).
    private void OnTreeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (!_map.TryGetValue(args.Node, out var node))
            return;
        node.EnsureLazyChildren();
        args.Node.HasUnrealizedChildren = false;
    }

    private void OnTreeSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (args.AddedItems.Count > 0
            && args.AddedItems[0] is TreeViewNode tvn
            && _map.TryGetValue(tvn, out var node))
        {
            node.ActivateItem(StubRoutedEventArgs.Instance);
        }
    }

    private TreeViewNode CreateNode(SharpTreeNode node)
    {
        var tvn = new TreeViewNode
        {
            Content = SharpTreeViewAdapter.BuildViewModel(node),
            HasUnrealizedChildren = node.LazyLoading || node.Children.Count > 0,
        };
        _map[tvn] = node;

        // Mirror current children (e.g. already-loaded), then observe future changes so async
        // analyzer results replace the "Loading..." placeholder in the live tree.
        foreach (SharpTreeNode child in node.Children)
            tvn.Children.Add(CreateNode(child));
        if (tvn.Children.Count > 0)
            tvn.HasUnrealizedChildren = false;

        if (node.Children is INotifyCollectionChanged incc)
        {
            NotifyCollectionChangedEventHandler handler =
                (_, e) => DispatcherQueue.TryEnqueue(() => OnModelChildrenChanged(tvn, node, e));
            _childHandlers[node] = handler;
            incc.CollectionChanged += handler;
        }

        return tvn;
    }

    // Reconciles a TreeViewNode's children with its model node as the model's Children collection
    // changes (analyzer searches add results / remove the "Loading..." node asynchronously).
    private void OnModelChildrenChanged(TreeViewNode tvn, SharpTreeNode node, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                InsertChildren(tvn, e.NewItems, e.NewStartingIndex);
                break;
            case NotifyCollectionChangedAction.Remove:
                RemoveChildren(tvn, e.OldItems);
                break;
            case NotifyCollectionChangedAction.Replace:
                RemoveChildren(tvn, e.OldItems);
                InsertChildren(tvn, e.NewItems, e.NewStartingIndex);
                break;
            case NotifyCollectionChangedAction.Reset:
                foreach (var child in new List<TreeViewNode>(EnumerateChildren(tvn)))
                    DetachAndRemove(tvn, child);
                foreach (SharpTreeNode child in node.Children)
                    tvn.Children.Add(CreateNode(child));
                break;
        }
        tvn.HasUnrealizedChildren = false;
    }

    private void InsertChildren(TreeViewNode tvn, System.Collections.IList? items, int startIndex)
    {
        if (items is null) return;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not SharpTreeNode child) continue;
            var childTvn = CreateNode(child);
            var index = startIndex >= 0 ? startIndex + i : tvn.Children.Count;
            if (index < 0 || index > tvn.Children.Count) index = tvn.Children.Count;
            tvn.Children.Insert(index, childTvn);
        }
    }

    private void RemoveChildren(TreeViewNode tvn, System.Collections.IList? items)
    {
        if (items is null) return;
        foreach (var item in items)
        {
            if (item is not SharpTreeNode child) continue;
            foreach (var childTvn in EnumerateChildren(tvn))
                if (_map.TryGetValue(childTvn, out var m) && ReferenceEquals(m, child))
                {
                    DetachAndRemove(tvn, childTvn);
                    break;
                }
        }
    }

    private static IEnumerable<TreeViewNode> EnumerateChildren(TreeViewNode tvn)
    {
        foreach (var c in tvn.Children) yield return c;
    }

    // Removes a child TreeViewNode and tears down the subscriptions/maps for it and its descendants.
    private void DetachAndRemove(TreeViewNode parent, TreeViewNode child)
    {
        Detach(child);
        parent.Children.Remove(child);
    }

    private void RemoveRootNode(SharpTreeNode node)
    {
        foreach (var tvn in new List<TreeViewNode>(_tree.RootNodes))
            if (_map.TryGetValue(tvn, out var m) && ReferenceEquals(m, node))
            {
                Detach(tvn);
                _tree.RootNodes.Remove(tvn);
                break;
            }
    }

    private void Detach(TreeViewNode tvn)
    {
        foreach (var child in EnumerateChildren(tvn))
            Detach(child);
        if (_map.TryGetValue(tvn, out var node))
        {
            if (_childHandlers.Remove(node, out var handler)
                && node.Children is INotifyCollectionChanged incc)
                incc.CollectionChanged -= handler;
            _map.Remove(tvn);
        }
    }
}
