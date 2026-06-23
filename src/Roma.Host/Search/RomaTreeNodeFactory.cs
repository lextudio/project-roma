using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.ILSpy.TreeNodes;
using ICSharpCode.ILSpyX.Abstractions;

namespace Roma.Host;

// Minimal ITreeNodeFactory for the search engine's resource search strategy.
// Both tree node types are already compiled into Roma.Host via linked csproj items.
internal sealed class RomaTreeNodeFactory : ITreeNodeFactory
{
    public ITreeNode Create(Resource resource) => ResourceTreeNode.Create(resource);

    public ITreeNode CreateResourcesList(MetadataFile module)
        => new ICSharpCode.ILSpy.TreeNodes.ResourceListTreeNode(module);
}
