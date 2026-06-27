// Programmatic implementation of MetadataTableViews (replaces the WPF XAML resource dictionary).
//
// The upstream MetadataTableViews.xaml uses WPF-only XAML features ({x:Type}, {x:Static},
// RelativeSource FindAncestor, TemplateBinding BasedOn) that cannot be parsed by WinUI's
// XamlReader. Instead of porting the XAML to WinUI syntax, this file builds every resource
// in C# and populates the inherited ResourceDictionary directly.
//
// Resources provided:
//   DataGridCellStyle           — WinUI Style for DataGridCell (BorderThickness=0, Padding=2, VCA=Center)
//   DefaultFilter / HexFilter   — FilterControlTemplate for the auto-filter row
//   *Filter (flags types)       — FilterControlTemplate with the matching FlagsType
//   ItemContainerStyle          — Style for ListViewItem (HorizontalContentAlignment=Stretch)
//   row-details templates are translated from the upstream XAML DataTemplate entries

using System.Reflection;
using System.Reflection.Metadata;
using System.Windows.Controls;
using Microsoft.UI.Xaml.Controls;

namespace ICSharpCode.ILSpy.Metadata
{
    public partial class MetadataTableViews : System.Windows.ResourceDictionary
    {
        private static MetadataTableViews? _instance;
        public static MetadataTableViews Instance => _instance ??= new MetadataTableViews();

        public static WpfXamlResourceTranslationReport? LastTranslationReport { get; private set; }

        public MetadataTableViews()
        {
            WpfResourceFactory.Populate(this, BuildResources());
        }

        private static WpfResourceSpec[] BuildResources()
        {
            var xamlPath = Path.Combine(AppContext.BaseDirectory, "ILSpy", "Metadata", "MetadataTableViews.xaml");
            if (File.Exists(xamlPath))
            {
                var resources = WpfXamlResourceTranslator.TranslateResourceDictionary(
                    File.ReadAllText(xamlPath),
                    ResolveMetadataXamlType,
                    out var report);
                LastTranslationReport = report;
                return resources;
            }

            LastTranslationReport = null;
            return
            [
                WpfResourceSpec.TextFilter("DefaultFilter"),
                WpfResourceSpec.HexFilter("HexFilter"),
                WpfResourceSpec.FlagsFilter("AssemblyFlagsFilter", typeof(AssemblyFlags)),
                WpfResourceSpec.FlagsFilter("AssemblyHashAlgorithmFilter", typeof(AssemblyHashAlgorithm)),
                WpfResourceSpec.FlagsFilter("MethodAttributesFilter", typeof(MethodAttributes)),
                WpfResourceSpec.FlagsFilter("MethodImplAttributesFilter", typeof(MethodImplAttributes)),
                WpfResourceSpec.FlagsFilter("MethodSemanticsAttributesFilter", typeof(MethodSemanticsAttributes)),
                WpfResourceSpec.FlagsFilter("TypeAttributesFilter", typeof(TypeAttributes)),
                WpfResourceSpec.FlagsFilter("PropertyAttributesFilter", typeof(PropertyAttributes)),
                WpfResourceSpec.FlagsFilter("EventAttributesFilter", typeof(EventAttributes)),
                WpfResourceSpec.FlagsFilter("FieldAttributesFilter", typeof(FieldAttributes)),
                WpfResourceSpec.FlagsFilter("ManifestResourceAttributesFilter", typeof(ManifestResourceAttributes)),
                WpfResourceSpec.FlagsFilter("GenericParameterAttributesFilter", typeof(GenericParameterAttributes)),
                WpfResourceSpec.FlagsFilter("PInvokeAttributesFilter", typeof(Mono.Cecil.PInvokeAttributes)),
                WpfResourceSpec.Style(
                    "DataGridCellStyle",
                    typeof(DataGridCell),
                    WpfStyleFactory.Set(
                        Microsoft.UI.Xaml.Controls.Control.BorderThicknessProperty,
                        new Microsoft.UI.Xaml.Thickness(0)),
                    WpfStyleFactory.Set(
                        Microsoft.UI.Xaml.Controls.Control.PaddingProperty,
                        new Microsoft.UI.Xaml.Thickness(2)),
                    WpfStyleFactory.Set(
                        Microsoft.UI.Xaml.Controls.Control.VerticalContentAlignmentProperty,
                        Microsoft.UI.Xaml.VerticalAlignment.Center)),
                WpfResourceSpec.Style(
                    "ItemContainerStyle",
                    typeof(ListViewItem),
                    WpfStyleFactory.Set(
                        Microsoft.UI.Xaml.Controls.Control.HorizontalContentAlignmentProperty,
                        Microsoft.UI.Xaml.HorizontalAlignment.Stretch))
            ];
        }

        private static Type? ResolveMetadataXamlType(string name)
        {
            return name switch
            {
                "DataGridCell" => typeof(DataGridCell),
                "ListViewItem" => typeof(ListViewItem),
                "ByteWidthConverter" or "local:ByteWidthConverter" => typeof(ByteWidthConverter),
                "srm:AssemblyFlags" => typeof(AssemblyFlags),
                "srm:AssemblyHashAlgorithm" => typeof(AssemblyHashAlgorithm),
                "reflection:MethodAttributes" => typeof(MethodAttributes),
                "reflection:MethodImplAttributes" => typeof(MethodImplAttributes),
                "srm:MethodSemanticsAttributes" => typeof(MethodSemanticsAttributes),
                "reflection:TypeAttributes" => typeof(TypeAttributes),
                "reflection:PropertyAttributes" => typeof(PropertyAttributes),
                "reflection:EventAttributes" => typeof(EventAttributes),
                "reflection:FieldAttributes" => typeof(FieldAttributes),
                "srm:ManifestResourceAttributes" => typeof(ManifestResourceAttributes),
                "reflection:GenericParameterAttributes" => typeof(GenericParameterAttributes),
                "cecil:PInvokeAttributes" => typeof(Mono.Cecil.PInvokeAttributes),
                _ => null
            };
        }
    }
}
