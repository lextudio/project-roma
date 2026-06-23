using System;
using System.Linq;

using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpy;
using ICSharpCode.ILSpy.Properties;
using ICSharpCode.ILSpy.TreeNodes;
using ICSharpCode.ILSpyX.TreeView;

namespace Roma.Host.ContextMenu;

// Roma-local "Decompile to new tab". ILSpy's DecompileInNewViewCommand drives the decompile through
// AssemblyTreeModel.SelectNodes / DockWorkspace, which Roma's custom UI never observed, so it had no
// visible effect. This routes through Roma's own multi-tab surface instead: the injected callback
// opens a new DockWorkspace tab and decompiles the node into it. Same [ExportContextMenuEntry]
// metadata (Analyze category, Order 90) so it groups identically.
[ExportContextMenuEntry(Header = nameof(Resources.DecompileToNewPanel), InputGestureText = "MMB", Icon = "images/Search", Category = nameof(Resources.Analyze), Order = 90)]
internal sealed class RomaDecompileInNewTabEntry(Action<SharpTreeNode> openInNewTab) : IContextMenuEntry
{
    public bool IsVisible(TextViewContext context) => GetNode(context) != null;

    public bool IsEnabled(TextViewContext context) => GetNode(context) != null;

    public void Execute(TextViewContext context)
    {
        if (GetNode(context) is { } node)
            openInNewTab(node);
    }

    private static SharpTreeNode? GetNode(TextViewContext context)
    {
        if (context.SelectedTreeNodes is { Length: > 0 } nodes)
            return nodes[0];
        if (context.Reference?.Reference is IEntity)
            return null; // text-view references aren't tree nodes in Roma's host
        return null;
    }
}
