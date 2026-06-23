using System;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.Output;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpy;
using ICSharpCode.ILSpy.TreeNodes;
using ICSharpCode.ILSpyX.Abstractions;
using ICSharpCode.ILSpyX.Search;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Roma.Host;

// Roma replacement for the WPF SearchResultFactory. Stores image handles from the central
// ICSharpCode.ILSpy.Images catalogue (RomaImagesStub) in SearchResult.Image / LocationImage /
// AssemblyImage so the DataTemplate can bind Image.Source directly without a converter.
internal sealed class RomaSearchResultFactory : ISearchResultFactory
{
    readonly Language _language;

    public RomaSearchResultFactory(Language language) => _language = language;

    // ── helpers ──────────────────────────────────────────────────

    static float CalculateFitness(IEntity member)
    {
        string text = member.Name;
        if (text.StartsWith('<'))
            return 0;
        if (member.SymbolKind is SymbolKind.Constructor or SymbolKind.Destructor)
            text = member.DeclaringType.Name;
        text = ReflectionHelper.SplitTypeParameterCountFromReflectionName(text);
        return 1.0f / text.Length;
    }

    string GetLanguageSpecificName(IEntity member) => member switch
    {
        ITypeDefinition t => _language.TypeToString(t, ConversionFlags.None),
        IField f          => _language.EntityToString(f, ConversionFlags.ShowDeclaringType),
        IProperty p       => _language.EntityToString(p, ConversionFlags.ShowDeclaringType),
        IMethod m         => _language.EntityToString(m, ConversionFlags.ShowDeclaringType),
        IEvent e          => _language.EntityToString(e, ConversionFlags.ShowDeclaringType),
        _                 => throw new NotSupportedException(member?.GetType() + " not supported"),
    };

    static ImageSource GetMemberIcon(IEntity member) => member switch
    {
        ITypeDefinition t => GetTypeIcon(t),
        IField f          => GetFieldIcon(f),
        IMethod m         => GetMethodIcon(m),
        IProperty p       => p.IsIndexer ? Images.Indexer : Images.Property,
        IEvent            => Images.Event,
        _                 => Images.Class,
    };

    static ImageSource GetTypeIcon(ITypeDefinition t) => t.Kind switch
    {
        TypeKind.Enum      => Images.Enum,
        TypeKind.Struct    => Images.Struct,
        TypeKind.Interface => Images.Interface,
        TypeKind.Delegate  => Images.Delegate,
        _                  => Images.Class,
    };

    static ImageSource GetFieldIcon(IField f)
    {
        if (f.DeclaringType?.Kind == TypeKind.Enum) return Images.EnumValue;
        if (f.IsConst)                              return Images.Literal;
        if (f.IsReadOnly)                           return Images.FieldReadOnly;
        return Images.Field;
    }

    static ImageSource GetMethodIcon(IMethod m)
    {
        if (m.IsOperator)                           return Images.Operator;
        if (m.IsExtensionMethod)                    return Images.ExtensionMethod;
        if (m.IsConstructor || m.IsDestructor)      return Images.Constructor;
        if (m.HasAttribute(KnownAttribute.DllImport)) return Images.PInvokeMethod;
        return m.IsVirtual ? Images.VirtualMethod : Images.Method;
    }

    // ── ISearchResultFactory ─────────────────────────────────────

    public MemberSearchResult Create(IEntity entity)
    {
        var declaringType = entity.DeclaringTypeDefinition;
        return new MemberSearchResult
        {
            Member        = entity,
            Fitness       = CalculateFitness(entity),
            Name          = GetLanguageSpecificName(entity),
            Location      = declaringType != null
                ? _language.TypeToString(declaringType,
                    ConversionFlags.UseFullyQualifiedEntityNames | ConversionFlags.UseFullyQualifiedTypeNames)
                : entity.Namespace,
            Assembly      = entity.ParentModule.FullAssemblyName,
            ToolTip       = entity.ParentModule.MetadataFile?.FileName,
            Image         = GetMemberIcon(entity),
            LocationImage = declaringType != null
                ? GetTypeIcon(declaringType)
                : Images.Namespace,
            AssemblyImage = Images.Assembly,
        };
    }

    public ResourceSearchResult Create(MetadataFile module, Resource resource, ITreeNode node, ITreeNode parent)
        => new()
        {
            Resource      = resource,
            Fitness       = 1.0f / resource.Name.Length,
            Name          = resource.Name,
            Location      = (string)parent.Text,
            Assembly      = module.FullName,
            ToolTip       = module.FileName,
            Image         = Images.Resource,
            LocationImage = Images.FolderOpen,
            AssemblyImage = Images.Assembly,
        };

    public AssemblySearchResult Create(MetadataFile module)
        => new()
        {
            Module        = module,
            Fitness       = 1.0f / module.Name.Length,
            Name          = module.Name,
            Location      = module.FileName,
            Assembly      = module.FullName,
            ToolTip       = module.FileName,
            Image         = Images.Assembly,
            LocationImage = Images.Library,
            AssemblyImage = Images.Assembly,
        };

    public NamespaceSearchResult Create(MetadataFile module, INamespace ns)
    {
        var name = ns.FullName.Length == 0 ? "-" : ns.FullName;
        return new()
        {
            Namespace     = ns,
            Name          = name,
            Fitness       = 1.0f / name.Length,
            Location      = module.Name,
            Assembly      = module.FullName,
            Image         = Images.Namespace,
            LocationImage = Images.Assembly,
            AssemblyImage = Images.Assembly,
        };
    }
}
