using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpy;
using ICSharpCode.ILSpy.Metadata;
using ICSharpCode.ILSpy.TreeNodes;
using ICSharpCode.ILSpyX.TreeView;

namespace Roma.Host;

// Maps ILSpyX SharpTreeNode subtypes to icons from the central ICSharpCode.ILSpy.Images
// catalogue (RomaImagesStub), mirroring the upstream ILSpy node → Images.* mapping.
// Returns (Base, Overlay?) — Overlay is null when no accessibility/static overlay applies;
// Roma renders the two as separate layers since WinUI can't composite SvgImageSource.
internal static class RomaTreeIconProvider
{
    public static (ImageSource? Base, ImageSource? Overlay) GetIcons(SharpTreeNode node) =>
        GetIcons(node, node.IsExpanded);

    public static (ImageSource? Base, ImageSource? Overlay) GetIcons(SharpTreeNode node, bool isExpanded) =>
        node switch
        {
            AssemblyTreeNode a     => (a.LoadedAssembly.HasLoadError ? Images.AssemblyWarning : Images.Assembly, null),
            NamespaceTreeNode      => (Images.Namespace, null),
            ReferenceFolderTreeNode => (Images.ReferenceFolder, null),
            AssemblyReferenceTreeNode => (Images.Library, Images.OverlayReference),
            PackageFolderTreeNode  => (isExpanded ? Images.FolderOpen : Images.FolderClosed, null),
            TypeTreeNode t         => GetTypeIcons(t),
            MethodTreeNode m       => GetMethodIcons(m),
            FieldTreeNode f        => GetFieldIcons(f),
            PropertyTreeNode p     => GetPropertyIcons(p),
            EventTreeNode e        => GetEventIcons(e),
            ResourceListTreeNode   => (isExpanded ? Images.FolderOpen : Images.FolderClosed, null),
            _ when node.GetType().Name is "ImageResourceEntryNode" or "IconResourceEntryNode" or "CursorResourceEntryNode" or "ImageListResourceEntryNode"
                => (Images.ResourceImage, null),
            _ when node.GetType().Name is "XmlResourceNode"
                => (GetXmlResourceIcon(node), null),
            ResourceTreeNode r     => (GetResourceIcon(r), null),
            ResourceEntryNode      => (Images.Resource, null),
            MetadataTreeNode       => (Images.Metadata, null),
            MetadataTablesTreeNode => (Images.MetadataTableGroup, null),
            DebugMetadataTablesTreeNode => (Images.MetadataTableGroup, null),
            MetadataTableTreeNode  => (Images.MetadataTable, null),
            MetadataHeapTreeNode   => (Images.Heap, null),
            _ when node.GetType().Name is "DosHeaderTreeNode" or "CoffHeaderTreeNode" or "OptionalHeaderTreeNode"
                => (Images.Header, null),
            _ when node.GetType().Name is "DataDirectoriesTreeNode" or "DebugDirectoryTreeNode"
                => (isExpanded ? Images.ListFolderOpen : Images.ListFolder, null),
            _ when node.GetType().Name is "CodeViewTreeNode" or "PdbChecksumTreeNode" or "DebugDirectoryEntryTreeNode"
                => (Images.MetadataTable, null),
            _ when node.GetType().Name.Contains("SubType")  => (Images.SubTypes, null),
            _ when node.GetType().Name.Contains("SuperType") => (Images.SuperTypes, null),
            _                      => (null, null),
        };

    private static ImageSource GetResourceIcon(ResourceTreeNode node)
    {
        var typeName = node.GetType().Name;
        var text = node.Text?.ToString() ?? string.Empty;
        return typeName switch
        {
            "ResourcesFileTreeNode" => Images.ResourceResourcesFile,
            _ when text.EndsWith(".resources", System.StringComparison.OrdinalIgnoreCase) => Images.ResourceResourcesFile,
            "XmlResourceNode" => GetXmlResourceIcon(node),
            _ => Images.Resource,
        };
    }

    private static ImageSource GetXmlResourceIcon(SharpTreeNode node)
    {
        var text = node.Text?.ToString() ?? string.Empty;
        var extension = System.IO.Path.GetExtension(text).ToLowerInvariant();
        return extension switch
        {
            ".xml" => Images.ResourceXml,
            ".xsd" => Images.ResourceXsd,
            ".xsl" or ".xslt" => Images.ResourceXslt,
            _ => Images.Resource,
        };
    }

    private static (ImageSource?, ImageSource?) GetTypeIcons(TypeTreeNode t)
    {
        var type = t.TypeDefinition;
        var typeIcon = TypeTreeNode.GetTypeIcon(type, out bool isStatic);
        var overlay = TypeTreeNode.GetOverlayIcon(type);
        var overlayImage = Images.OverlayFor(overlay);
        if (isStatic && overlayImage is null) overlayImage = Images.OverlayStatic;
        return (Images.BaseFor(typeIcon), overlayImage);
    }

    private static (ImageSource?, ImageSource?) GetMethodIcons(MethodTreeNode m)
    {
        var method = m.MethodDefinition;
        bool isExtension = method.IsExtensionMethod;
        ImageSource baseImage;
        if (method.IsOperator)
            baseImage = Images.Operator;
        else if (isExtension)
            baseImage = Images.ExtensionMethod;
        else if (method.IsConstructor || method.IsDestructor)
            baseImage = Images.Constructor;
        else if (method.HasAttribute(KnownAttribute.DllImport))
            baseImage = Images.PInvokeMethod;
        else
            baseImage = method.IsVirtual ? Images.VirtualMethod : Images.Method;

        var overlayImage = Images.OverlayFor(Images.GetOverlayIcon(method.Accessibility));
        if (method.IsStatic && !isExtension && overlayImage is null)
            overlayImage = Images.OverlayStatic;
        return (baseImage, overlayImage);
    }

    private static (ImageSource?, ImageSource?) GetFieldIcons(FieldTreeNode f)
    {
        var field = f.FieldDefinition;
        ImageSource baseImage;
        if (field.DeclaringType?.Kind == TypeKind.Enum)
            baseImage = Images.EnumValue;
        else if (field.IsConst)
            baseImage = Images.Literal;
        else if (field.IsReadOnly)
            baseImage = Images.FieldReadOnly;
        else
            baseImage = Images.Field;

        var overlayImage = Images.OverlayFor(Images.GetOverlayIcon(field.Accessibility));
        if (field.IsStatic && !field.IsConst && overlayImage is null)
            overlayImage = Images.OverlayStatic;
        return (baseImage, overlayImage);
    }

    private static (ImageSource?, ImageSource?) GetPropertyIcons(PropertyTreeNode p)
    {
        var prop = p.PropertyDefinition;
        var baseImage = prop.IsIndexer ? Images.Indexer : Images.Property;
        var getter = prop.Getter ?? prop.Setter;
        var overlayImage = getter is not null
            ? Images.OverlayFor(Images.GetOverlayIcon(getter.Accessibility))
            : null;
        if (getter?.IsStatic == true && overlayImage is null)
            overlayImage = Images.OverlayStatic;
        return (baseImage, overlayImage);
    }

    private static (ImageSource?, ImageSource?) GetEventIcons(EventTreeNode e)
    {
        var ev = e.EventDefinition;
        var adder = ev.AddAccessor;
        var overlayImage = adder is not null
            ? Images.OverlayFor(Images.GetOverlayIcon(adder.Accessibility))
            : null;
        return (Images.Event, overlayImage);
    }
}
