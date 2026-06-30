using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.ILSpyX.TreeView;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Roma.Host;

// ViewModel stored as TreeViewNode.Content — the DataTemplate (x:DataType=TreeNodeViewModel)
// binds to these properties via {x:Bind}. Must be a top-level public type (XAML can't reference nested types).
public sealed class TreeNodeViewModel
{
    public string Text { get; init; } = string.Empty;
    public Microsoft.UI.Xaml.Media.ImageSource? BaseIcon { get; init; }
    public Microsoft.UI.Xaml.Media.ImageSource? OverlayIcon { get; init; }
    public Brush? Foreground { get; init; }
    public Visibility HasIcon =>
        BaseIcon is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OverlayVisibility =>
        OverlayIcon is not null ? Visibility.Visible : Visibility.Collapsed;
}

// Adapts an ILSpyX SharpTreeNode hierarchy into a WinUI TreeView.
// Each item is represented by a TreeNodeViewModel whose BaseIcon / OverlayIcon are
// SvgImageSource objects — bound in a DataTemplate supplied by the caller so the
// ContentPresenter gets proper ImageSource values instead of a UIElement.ToString().
internal static class SharpTreeViewAdapter
{
    // itemTemplate: a DataTemplate whose DataContext is a TreeNodeViewModel. When null
    // the default template renders Text only (no icons).
    public static ICSharpCode.ILSpy.Controls.TreeView.SharpTreeView Build(
        SharpTreeNode root,
        Action<SharpTreeNode>? onSelect = null,
        DataTemplate? itemTemplate = null)
    {
        var tree = new ICSharpCode.ILSpy.Controls.TreeView.SharpTreeView
        {
            SelectionMode = TreeViewSelectionMode.Single,
        };
        if (itemTemplate is not null)
            tree.ItemTemplate = itemTemplate;

        TreeViewNode CreateNode(SharpTreeNode node)
        {
            var tvn = new TreeViewNode
            {
                Content = BuildViewModel(node, node.IsExpanded),
                HasUnrealizedChildren = node.LazyLoading || node.Children.Count > 0,
            };
            tree.Register(node, tvn);
            return tvn;
        }

        // Materialize the child TreeViewNodes of an expanded parent. Shared by the Expanding event
        // (user-driven expansion) and SharpTreeView.EnsureVisible (programmatic path reveal — e.g.
        // session restore / reference jumps), so a node deep in the tree can be made visible without
        // waiting for WinUI to raise Expanding for each ancestor.
        void RealizeChildren(TreeViewNode tvn)
        {
            var node = tree.NodeFor(tvn);
            if (node is null)
                return;

            tvn.Content = BuildViewModel(node, isExpanded: true);

            if (tvn.HasUnrealizedChildren)
            {
                node.EnsureLazyChildren();
                foreach (SharpTreeNode child in node.Children)
                    tvn.Children.Add(CreateNode(child));

                tvn.HasUnrealizedChildren = false;
            }
        }

        tree.RealizeChildren = RealizeChildren;
        tree.Expanding += (_, args) => RealizeChildren(args.Node);

        tree.Collapsed += (_, args) =>
        {
            var node = tree.NodeFor(args.Node);
            if (node is not null)
                args.Node.Content = BuildViewModel(node, isExpanded: false);
        };

        if (onSelect is not null)
        {
            tree.ItemInvoked += (_, args) =>
            {
                if (args.InvokedItem is TreeViewNode tvn && tree.NodeFor(tvn) is { } node)
                {
                    tree.SetSelection([node]);
                    onSelect(node);
                }
            };
            tree.SelectionChanged += (_, args) =>
            {
                if (args.AddedItems.Count > 0 && args.AddedItems[0] is TreeViewNode tvn && tree.NodeFor(tvn) is { } node)
                {
                    tree.SetSelection([node]);
                    onSelect(node);
                }
            };
        }

        // ILSpy treats the root (AssemblyListTreeNode) as the *invisible* root: its children
        // — one AssemblyTreeNode per loaded assembly — are the top-level tree items. Mirror that
        // here, and observe the root's Children collection so assemblies opened/removed later
        // (the AssemblyList.CollectionChanged → AssemblyListTreeNode path) appear/disappear live.
        root.EnsureLazyChildren();
        foreach (SharpTreeNode child in root.Children)
        {
            var topNode = CreateNode(child);
            tree.RootNodes.Add(topNode);
            // Preserve the prior UX where the bundled (first) assembly shows expanded on launch.
            if (tree.RootNodes.Count == 1)
                topNode.IsExpanded = true;
        }

        if (root.Children is System.Collections.Specialized.INotifyCollectionChanged incc)
        {
            incc.CollectionChanged += (_, e) =>
            {
                void Apply()
                {
                    switch (e.Action)
                    {
                        case System.Collections.Specialized.NotifyCollectionChangedAction.Add:
                            if (e.NewItems is not null)
                            {
                                var index = e.NewStartingIndex >= 0 ? e.NewStartingIndex : tree.RootNodes.Count;
                                foreach (SharpTreeNode added in e.NewItems)
                                {
                                    var tvn = CreateNode(added);
                                    if (index >= 0 && index <= tree.RootNodes.Count)
                                        tree.RootNodes.Insert(index++, tvn);
                                    else
                                        tree.RootNodes.Add(tvn);
                                }
                            }
                            break;
                        case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
                            if (e.OldItems is not null)
                                foreach (SharpTreeNode removed in e.OldItems)
                                {
                                    if (tree.TreeViewNodeFor(removed) is { } tvn)
                                    {
                                        tree.RootNodes.Remove(tvn);
                                        tree.Unregister(removed);
                                    }
                                }
                            break;
                        case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                            foreach (var n in tree.RootNodes.ToList())
                                if (tree.NodeFor(n) is { } sn)
                                    tree.Unregister(sn);
                            tree.RootNodes.Clear();
                            root.EnsureLazyChildren();
                            foreach (SharpTreeNode child in root.Children)
                                tree.RootNodes.Add(CreateNode(child));
                            break;
                    }
                }

                // AssemblyList mutations may originate off the UI thread; tree mutation must be
                // on it. DispatcherQueue marshals (and runs inline when already on the UI thread).
                if (tree.DispatcherQueue is { } dq && !dq.HasThreadAccess)
                    dq.TryEnqueue(Apply);
                else
                    Apply();
            };
        }

        return tree;
    }

    internal static DataTemplate? BuildItemTemplate()
    {
        const string xaml =
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
            "  <StackPanel Orientation=\"Horizontal\" Spacing=\"4\" VerticalAlignment=\"Center\">" +
            "    <Grid Width=\"16\" Height=\"16\" Visibility=\"{Binding Content.HasIcon}\">" +
            "      <Image Width=\"16\" Height=\"16\" Source=\"{Binding Content.BaseIcon}\"/>" +
            "      <Image Width=\"16\" Height=\"16\" Source=\"{Binding Content.OverlayIcon}\" Visibility=\"{Binding Content.OverlayVisibility}\"/>" +
            "    </Grid>" +
            "    <TextBlock Text=\"{Binding Content.Text}\" Foreground=\"{Binding Content.Foreground}\" VerticalAlignment=\"Center\"/>" +
            "  </StackPanel>" +
            "</DataTemplate>";
        try { return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml); }
        catch { return null; }
    }

    internal static TreeNodeViewModel BuildViewModel(SharpTreeNode node) =>
        BuildViewModel(node, node.IsExpanded);

    internal static TreeNodeViewModel BuildViewModel(SharpTreeNode node, bool isExpanded)
    {
        var (baseIcon, overlayIcon) = TryGetIcons(node, isExpanded);
        return new TreeNodeViewModel
        {
            Text = node.Text?.ToString() ?? string.Empty,
            BaseIcon    = baseIcon,
            OverlayIcon = overlayIcon,
            Foreground = GetForeground(node),
        };
    }

    private static Brush? GetForeground(SharpTreeNode node)
    {
        if (node is ICSharpCode.ILSpy.TreeNodes.ILSpyTreeNode ilspyNode)
        {
            // Steel blue (auto-loaded) and grey (non-public) read on both themes, matching ILSpy.
            if (ilspyNode.IsAutoLoaded)
                return new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x46, 0x82, 0xB4));
            if (!ilspyNode.IsPublicAPI)
                return new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x6D, 0x6D, 0x6D));
        }

        // Normal nodes: explicit theme-appropriate text colour. The template binds Foreground, so a
        // null here would not reliably inherit the themed default inside the SharpTreeView container.
        // The tree is rebuilt on theme change, so this re-resolves to the active theme.
        return ICSharpCode.ILSpy.Images.IsDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF1, 0xF1, 0xF1))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E));
    }

    private static (Microsoft.UI.Xaml.Media.ImageSource?, Microsoft.UI.Xaml.Media.ImageSource?) TryGetIcons(SharpTreeNode node, bool isExpanded)
    {
        try { return RomaTreeIconProvider.GetIcons(node, isExpanded); }
        catch { return (null, null); }
    }
}
