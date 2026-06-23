// Bridge stubs for the ICSharpCode.ILSpy.Metadata leaf node family and support types.
//
// MetadataTableViews: the real class is a WPF ResourceDictionary loaded from
// MetadataTableViews.xaml (ControlTemplate resources for the auto-filter column headers).
// The DataGridExtensions shim stores but ignores null templates, so a null-returning stub
// is sufficient until the Uno XAML resource dictionary is ported.
//
// Leaf nodes (PE-header, table, heap) route View() through Helpers.PrepareDataGrid; they remain
// inert ILSpyTreeNodes until the XAML resource dictionary is ported.

using ICSharpCode.ILSpy.ViewModels;

namespace ICSharpCode.ILSpy.Metadata
{
    // MetadataTableViews stub: returns null for all resource keys.
    // Real class is a XAML ResourceDictionary; DataGridExtensions shim ignores null templates.
    public partial class MetadataTableViews : System.Windows.ResourceDictionary
    {
        private static MetadataTableViews? instance;
        public static MetadataTableViews Instance => instance ??= new MetadataTableViews();
    }
}

