using System.Linq;

using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpy.Analyzers;
using ICSharpCode.ILSpy.Analyzers.TreeNodes;
using ICSharpCode.ILSpyX.TreeView;

namespace Roma.Host.Analyzers;

public sealed class RomaAnalyzerViewModel
{
    public AnalyzerRootNode Root { get; } = new();

    public void Analyze(IEntity entity)
    {
        if (entity is null || entity.MetadataToken.IsNil) return;

        AnalyzerTreeNode? node = entity switch
        {
            ITypeDefinition td          => new AnalyzedTypeTreeNode(td, null),
            IField fd when !fd.IsConst  => new AnalyzedFieldTreeNode(fd, null),
            IMethod md                  => new AnalyzedMethodTreeNode(md, null),
            IProperty pd                => new AnalyzedPropertyTreeNode(pd, null),
            IEvent ed                   => new AnalyzedEventTreeNode(ed, null),
            _                           => null,
        };
        if (node is null) return;
        AddOrSelect(node);
    }

    void AddOrSelect(AnalyzerTreeNode node)
    {
        if (node is AnalyzerEntityTreeNode { Member: { } member })
        {
            var existing = Root.Children
                .OfType<AnalyzerEntityTreeNode>()
                .FirstOrDefault(n => n.Member == member);
            if (existing is not null) { existing.IsExpanded = true; return; }
        }
        Root.Children.Add(node);
        node.IsExpanded = true;
    }
}
