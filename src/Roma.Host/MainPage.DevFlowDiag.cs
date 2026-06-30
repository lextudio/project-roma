#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AvalonDock.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ICSharpCode.ILSpyX.TreeView;
using ICSharpCode.ILSpy.TreeNodes;
using ICSharpCode.Decompiler.TypeSystem;
using LeXtudio.DevFlow.Agent.Core;

namespace Roma.Host;

public sealed partial class MainPage
{
    [DevFlowAction("select-metadata-node", Description = "Select the first child of the Metadata tree node and trigger View()")]
    public static void SelectMetadataNode()
    {
        var page = _current;
        if (page is null) return;
        page.DispatcherQueue.TryEnqueue(() => page.SelectMetadataNodeImpl());
        System.Threading.Thread.Sleep(500);
    }

    [DevFlowAction("roma.decompiler-state", Description = "Reports the live Roma decompiler text view state.")]
    public static string GetDecompilerState()
    {
        var page = _current;
        if (page is null)
            return "MainPage is not available.";

        string result = string.Empty;
        page.DispatcherQueue.TryEnqueue(() =>
        {
            var task = page._lastDecompileTask;
            result =
                $"lastNode={page._lastDecompileNode ?? string.Empty}\n" +
                $"lastTaskStatus={task?.Status.ToString() ?? "none"}\n" +
                $"lastError={page._lastDecompileError ?? string.Empty}\n" +
                $"documentTitle={page._activeModel?.Title ?? string.Empty}\n" +
                $"selectedNodeType={page._lastSelectedNode?.GetType().FullName ?? string.Empty}\n" +
                $"selectedNodeText={page._lastSelectedNode?.Text?.ToString() ?? string.Empty}\n" +
                $"selectedNodeFullTypeName={(page._lastSelectedNode as TypeTreeNode)?.TypeDefinition.FullName ?? string.Empty}\n" +
                (page._decompilerTextView?.GetUnoDebugSnapshot() ?? "DecompilerTextView is not available.");
        });
        System.Threading.Thread.Sleep(500);
        return result;
    }

    [DevFlowAction("roma.metadata-tree-state", Description = "Reports the live Roma metadata subtree shape and icon mapping.")]
    public static string GetMetadataTreeState()
    {
        var page = _current;
        if (page is null)
            return "MainPage is not available.";

        string result = string.Empty;
        using var completed = new System.Threading.ManualResetEventSlim();
        page.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var root = page._assemblyContext.Root;
                root.EnsureLazyChildren();

                var sb = new StringBuilder();
                sb.AppendLine($"root={DescribeTreeNode(root)}");

                foreach (SharpTreeNode child in root.Children)
                {
                    var text = child.Text?.ToString() ?? string.Empty;
                    if (text.Contains("Metadata", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("Resources", StringComparison.OrdinalIgnoreCase))
                    {
                        AppendTreeNode(sb, child, 0, 3);
                    }
                }

                result = sb.ToString();
            }
            catch (Exception ex)
            {
                result = ex.ToString();
            }
            finally
            {
                completed.Set();
            }
        });
        completed.Wait(5000);
        return result;
    }

    private static void AppendTreeNode(StringBuilder sb, SharpTreeNode node, int depth, int maxDepth)
    {
        node.EnsureLazyChildren();
        sb.Append(' ', depth * 2);
        sb.AppendLine(DescribeTreeNode(node));

        if (depth >= maxDepth)
            return;

        foreach (SharpTreeNode child in node.Children)
            AppendTreeNode(sb, child, depth + 1, maxDepth);
    }

    private static string DescribeTreeNode(SharpTreeNode node)
    {
        var (baseIcon, overlayIcon) = RomaTreeIconProvider.GetIcons(node);
        var (closedIcon, _) = RomaTreeIconProvider.GetIcons(node, isExpanded: false);
        var (openIcon, _) = RomaTreeIconProvider.GetIcons(node, isExpanded: true);
        var baseUri = IconUri(baseIcon);
        var overlayUri = IconUri(overlayIcon);
        var closedUri = IconUri(closedIcon);
        var openUri = IconUri(openIcon);
        var ilspy = node as ILSpyTreeNode;
        return
            $"text=\"{node.Text}\" " +
            $"type={node.GetType().FullName} " +
            $"children={node.Children.Count} lazy={node.LazyLoading} " +
            $"icon={baseUri ?? "<null>"} closed={closedUri ?? "<null>"} open={openUri ?? "<null>"} overlay={overlayUri ?? "<null>"} " +
            $"publicApi={ilspy?.IsPublicAPI.ToString() ?? "<n/a>"} autoLoaded={ilspy?.IsAutoLoaded.ToString() ?? "<n/a>"}";
    }

    private static string IconUri(Microsoft.UI.Xaml.Media.ImageSource? icon) =>
        (icon as SvgImageSource)?.UriSource?.ToString() ?? "<null>";

    [DevFlowAction("roma.dock-autohide-state", Description = "Reports DockingManager auto-hide strip bounds and resolved VS2013 brushes.")]
    public static string GetDockAutoHideState()
    {
        var page = _current;
        if (page is null)
            return "MainPage is not available.";

        string result = string.Empty;
        page.DispatcherQueue.TryEnqueue(() =>
        {
            var sb = new StringBuilder();
            sb.AppendLine($"dock.background={BrushToString(page.DockManager.Background)}");
            sb.AppendLine($"dock.actualTheme={page.DockManager.ActualTheme}");
            foreach (var key in new[]
            {
                "UnoDock_VS2013_Background",
                "UnoDock_VS2013_TabBarBackground",
                "UnoDock_VS2013_ContentBackground",
                "UnoDock_VS2013_ResizerBackground",
                "UnoDock_VS2013_TabBarBorderBrush",
                "UnoDock_VS2013_TabText",
            })
            {
                sb.AppendLine($"{key}={BrushToString(FindResourceBrush(page.DockManager, key))}");
            }

            foreach (var side in FindVisualChildren<LayoutAnchorSideControl>(page.DockManager))
            {
                var transform = side.TransformToVisual(page);
                var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                var tabBarBrush = FindResourceBrush(side, "UnoDock_VS2013_TabBarBackground");
                sb.AppendLine(
                    $"side[{side.GetHashCode():x}] bounds={point.X:0},{point.Y:0},{side.ActualWidth:0}x{side.ActualHeight:0} " +
                    $"background={BrushToString(side.Background)} " +
                    $"tabBar={BrushToString(tabBarBrush)}");
            }

            result = sb.ToString();
        });
        System.Threading.Thread.Sleep(500);
        return result;
    }

    private static Brush? FindResourceBrush(FrameworkElement start, string key)
    {
        if (start.Resources.TryGetValue(key, out var local) && local is Brush localBrush)
            return localBrush;

        DependencyObject? current = start;
        while (current != null)
        {
            if (current is FrameworkElement fe &&
                fe.Resources.TryGetValue(key, out var scoped) &&
                scoped is Brush scopedBrush)
                return scopedBrush;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    internal static string BrushToString(Brush? brush)
    {
        return brush switch
        {
            SolidColorBrush solid => solid.Color.ToString(),
            null => "<null>",
            _ => brush.GetType().Name,
        };
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    [DevFlowAction("roma.select-ilspy-app", Description = "Selects ICSharpCode.ILSpy.App and triggers the decompiler view.")]
    public static void SelectIlSpyAppNode()
    {
        var page = _current;
        if (page is null) return;
        page.DispatcherQueue.TryEnqueue(() =>
        {
            var target = page.FindTypeNode("ICSharpCode.ILSpy.App")
                ?? (page._assemblyContext.PrimaryAssemblyNode is { } asm
                    ? page.FindNodeByPath(asm, "ICSharpCode.ILSpy", "App")
                    : null);
            if (target is not null)
                page.OnTreeNodeSelected(target);
        });
        System.Threading.Thread.Sleep(1000);
    }

    [DevFlowAction("roma.select-resources-file", Description = "Selects the first .resources node and reports the rendered view.")]
    public static string SelectResourcesFileNode()
    {
        var page = _current;
        if (page is null)
            return "MainPage is not available.";

        string result = string.Empty;
        using var completed = new System.Threading.ManualResetEventSlim();
        page.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                page._assemblyContext.Root.EnsureLazyChildren();
                var target = page.FindNode(page._assemblyContext.Root, n =>
                    n.Text?.ToString()?.EndsWith(".resources", StringComparison.OrdinalIgnoreCase) == true);
                if (target is null)
                {
                    result = ".resources node not found.";
                    return;
                }

                page.OnTreeNodeSelected(target);
                var panel = page._nodeContent?.Content as Microsoft.UI.Xaml.Controls.Grid;
                var saveButton = panel?.Children.OfType<Microsoft.UI.Xaml.Controls.Button>().FirstOrDefault();
                var dataGrid = panel?.Children.OfType<System.Windows.Controls.DataGrid>().FirstOrDefault();
                result =
                    $"selected={target.Text}\n" +
                    $"nodeType={target.GetType().FullName}\n" +
                    $"documentTitle={page._activeModel?.Title}\n" +
                    $"contentType={page._nodeContent?.Content?.GetType().FullName ?? "<null>"}\n" +
                    $"hasSaveButton={saveButton is not null}\n" +
                    $"hasGrid={dataGrid is not null}\n" +
                    $"gridRows={dataGrid?.Items.Count ?? 0}";
            }
            finally
            {
                completed.Set();
            }
        });
        completed.Wait(5000);
        return result;
    }

    [DevFlowAction("roma.select-resource", Description = "Selects a resource node by suffix and reports the rendered surface.")]
    public static string SelectResourceNode(string suffix)
    {
        var page = _current;
        if (page is null)
            return "MainPage is not available.";

        string result = string.Empty;
        using var completed = new System.Threading.ManualResetEventSlim();
        page.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                page._assemblyContext.Root.EnsureLazyChildren();
                var target = page.FindNode(page._assemblyContext.Root, n =>
                    n.Text?.ToString()?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true);
                if (target is null)
                {
                    result = $"Resource ending with '{suffix}' not found.";
                    return;
                }

                page.OnTreeNodeSelected(target);
                result =
                    $"selected={target.Text}\n" +
                    $"nodeType={target.GetType().FullName}\n" +
                    $"documentTitle={page._activeModel?.Title}\n" +
                    $"codeEditorVisible={page._codeEditor?.Visibility}\n" +
                    $"nodeHostVisible={page._nodeHost?.Visibility}\n" +
                    $"decompilerVisible={page._decompilerTextView?.Visibility}\n" +
                    $"textLength={page._codeEditor?.Text?.Length ?? 0}";
            }
            finally
            {
                completed.Set();
            }
        });
        completed.Wait(5000);
        return result;
    }

    [DevFlowAction("activate-tool-pane", Description = "Activate the Assembly Browser tool pane (sets IsActive=true, as a tab click does)")]
    public static void ActivateToolPane()
    {
        var page = _current;
        if (page is null) return;
        page.DispatcherQueue.TryEnqueue(() => page.DockWorkspace.ShowToolPane("assemblyListPane"));
        System.Threading.Thread.Sleep(400);
    }

    [DevFlowAction("roma.apply-theme", Description = "Applies a theme by name (diagnostic).")]
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

    [DevFlowAction("roma.theme-state", Description = "Reports Roma theme state for the root, menu, toolbar, dock, and editor.")]
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

    internal SharpTreeNode? FindNode(SharpTreeNode root, Func<SharpTreeNode, bool> predicate)
    {
        if (predicate(root))
            return root;

        root.EnsureLazyChildren();
        foreach (SharpTreeNode child in root.Children)
        {
            var match = FindNode(child, predicate);
            if (match is not null)
                return match;
        }

        return null;
    }

    private TypeTreeNode? FindTypeNode(string fullTypeName)
    {
        if (_assemblyContext.PrimaryAssemblyNode is not { } assemblyNode)
            return null;

        try
        {
            var type = _assemblyContext.Decompiler.TypeSystem.FindType(new FullTypeName(fullTypeName)).GetDefinition();
            return assemblyNode.FindTypeNode(type);
        }
        catch
        {
            return null;
        }
    }

    private TypeTreeNode? FindTypeNode(SharpTreeNode root, string fullTypeName)
    {
        root.EnsureLazyChildren();
        foreach (SharpTreeNode child in root.Children)
        {
            if (child is TypeTreeNode typeNode &&
                string.Equals(typeNode.TypeDefinition.FullName, fullTypeName, StringComparison.Ordinal))
                return typeNode;

            var match = FindTypeNode(child, fullTypeName);
            if (match is not null)
                return match;
        }

        return null;
    }

    private SharpTreeNode? FindNodeByPath(SharpTreeNode root, params string[] path)
    {
        SharpTreeNode current = root;
        foreach (var part in path)
        {
            current.EnsureLazyChildren();
            SharpTreeNode? next = null;
            foreach (SharpTreeNode child in current.Children)
            {
                if (string.Equals(child.Text?.ToString(), part, StringComparison.Ordinal))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
                return null;
            current = next;
        }

        return current;
    }

    private void SelectMetadataNodeImpl()
    {
        if (_assemblyContext.PrimaryAssemblyNode is not { } root)
            return;
        root.EnsureLazyChildren();
        SharpTreeNode? metadataNode = null;
        foreach (SharpTreeNode child in root.Children)
        {
            if (child.Text?.ToString() == "Metadata") { metadataNode = child; break; }
        }
        if (metadataNode is null) return;
        metadataNode.EnsureLazyChildren();
        if (metadataNode.Children.Count == 0) return;
        OnTreeNodeSelected(metadataNode.Children[0]);
    }
}
#endif
