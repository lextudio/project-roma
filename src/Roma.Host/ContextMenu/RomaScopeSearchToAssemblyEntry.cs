using System;

using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpy;
using ICSharpCode.ILSpy.TreeNodes;
using Resources = Roma.Host.Properties.Resources;

namespace Roma.Host.ContextMenu;

// Roma-local port of ICSharpCode.ILSpy.ScopeSearchToAssembly. ILSpy's version depends on the
// MEF [Shared] SearchPaneModel, which Roma does not compile (Roma has its own SearchPane control).
// Same [ExportContextMenuEntry] metadata (Analyze category, Order 9999) so it groups identically
// under RomaTreeContextMenu; the scope action is injected so MainPage can resolve its lazily-built
// SearchPane at click time.
[ExportContextMenuEntry(Header = nameof(Resources.ScopeSearchToThisAssembly), Category = nameof(Resources.Analyze), Order = 9999)]
internal sealed class RomaScopeSearchToAssemblyEntry(Action<string> scopeToAssembly) : IContextMenuEntry
{
    public bool IsVisible(TextViewContext context) => GetAssembly(context) != null;

    public bool IsEnabled(TextViewContext context) => GetAssembly(context) != null;

    public void Execute(TextViewContext context)
    {
        if (GetAssembly(context) is { } assemblyName)
            scopeToAssembly(assemblyName);
    }

    private static string? GetAssembly(TextViewContext context)
    {
        if (context.Reference?.Reference is IEntity entity)
            return entity.ParentModule?.AssemblyName;
        if (context.SelectedTreeNodes?.Length != 1)
            return null;
        return context.SelectedTreeNodes[0] switch
        {
            AssemblyTreeNode tn => tn.LoadedAssembly.ShortName,
            IMemberTreeNode member => member.Member?.ParentModule?.AssemblyName,
            _ => null,
        };
    }
}
