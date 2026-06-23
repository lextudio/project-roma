using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpy;
using ICSharpCode.ILSpy.TreeNodes;
using Resources = Roma.Host.Properties.Resources;

namespace Roma.Host.Analyzers;

// Mirrors ICSharpCode.ILSpy.AnalyzeCommand's [ExportContextMenuEntry] metadata so Roma's
// analyzer entry sorts and groups identically (Analyze category, Order 100) under the
// metadata-driven RomaTreeContextMenu builder.
[ExportContextMenuEntry(Header = nameof(Resources.Analyze), Icon = "Images/Search", Category = nameof(Resources.Analyze), InputGestureText = "Ctrl+R", Order = 100)]
internal sealed class RomaAnalyzeContextMenuCommand(RomaAnalyzerViewModel vm) : IContextMenuEntry
{
    public bool IsVisible(TextViewContext context) =>
        context.SelectedTreeNodes?.All(n => n is IMemberTreeNode) == true
        || context.Reference?.Reference is IEntity;

    public bool IsEnabled(TextViewContext context)
    {
        if (context.SelectedTreeNodes is { } nodes)
            return nodes.All(n => n is IMemberTreeNode m && m.Member is IEntity e && !e.MetadataToken.IsNil);
        return context.Reference?.Reference is IEntity;
    }

    public void Execute(TextViewContext context)
    {
        if (context.SelectedTreeNodes is { } nodes)
        {
            foreach (var n in nodes)
                if (n is IMemberTreeNode m && m.Member is IEntity e)
                    vm.Analyze(e);
        }
        else if (context.Reference?.Reference is IEntity entity)
        {
            vm.Analyze(entity);
        }
    }
}
