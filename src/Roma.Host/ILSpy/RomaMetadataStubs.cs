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
//   CustomDebugInformationDetailsDataGrid — ShimDataTemplate: nested DataGrid bound to RowDetails
//   CustomDebugInformationDetailsTextBlob — ShimDataTemplate: read-only TextBox bound to RowDetails
//   HeaderFlagsDetailsDataGrid  — ShimDataTemplate: DataGrid showing BitEntry.Value / Meaning

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
            var fallbackResources = new[]
            {
                WpfResourceSpec.DataTemplate("CustomDebugInformationDetailsDataGrid", BuildCustomDebugInfoDataGrid),
                WpfResourceSpec.DataTemplate("CustomDebugInformationDetailsTextBlob", BuildCustomDebugInfoTextBlob),
                WpfResourceSpec.DataTemplate("HeaderFlagsDetailsDataGrid", BuildHeaderFlagsDataGrid)
            };

            var xamlPath = Path.Combine(AppContext.BaseDirectory, "ILSpy", "Metadata", "MetadataTableViews.xaml");
            if (File.Exists(xamlPath))
            {
                var resources = WpfXamlResourceTranslator.TranslateResourceDictionary(
                    File.ReadAllText(xamlPath),
                    ResolveMetadataXamlType,
                    out var report,
                    fallbackResources);
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
                        Microsoft.UI.Xaml.HorizontalAlignment.Stretch)),
                .. fallbackResources
            ];
        }

        private static Type? ResolveMetadataXamlType(string name)
        {
            return name switch
            {
                "DataGridCell" => typeof(DataGridCell),
                "ListViewItem" => typeof(ListViewItem),
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

        // ── Factory: nested DataGrid for structured debug info ────────────────────────
        private static Microsoft.UI.Xaml.FrameworkElement? BuildCustomDebugInfoDataGrid(
            object? item,
            Microsoft.UI.Xaml.DependencyObject? templatedParent)
        {
            var rowDetails = System.Windows.Data.BindingEvaluator.Evaluate(
                item,
                new Binding("RowDetails")) as System.Collections.IEnumerable;
            if (rowDetails == null)
                return null;

            return WpfTemplateFactory.Create<DataGrid>(
                item,
                grid =>
                {
                    grid.AutoGenerateColumns = true;
                    grid.CanUserAddRows = false;
                    grid.CanUserDeleteRows = false;
                    grid.CanUserReorderColumns = false;
                    grid.HeadersVisibility = DataGridHeadersVisibility.Column;
                    grid.IsReadOnly = true;
                    grid.GridLinesVisibility = DataGridGridLinesVisibility.None;
                    grid.SelectionMode = DataGridSelectionMode.Single;
                    grid.SelectionUnit = DataGridSelectionUnit.FullRow;
                    grid.MaxHeight = 250;
                    grid.AutoGeneratingColumn += Helpers.View_AutoGeneratingColumn;
                    grid.AutoGeneratedColumns += Helpers.View_AutoGeneratedColumns;
                },
                BindingAssignment.To(nameof(DataGrid.ItemsSource), new Binding("RowDetails")));
        }

        // ── Factory: read-only TextBox for blob debug info ────────────────────────────
        private static Microsoft.UI.Xaml.FrameworkElement? BuildCustomDebugInfoTextBlob(
            object? item,
            Microsoft.UI.Xaml.DependencyObject? templatedParent)
        {
            return WpfTemplateFactory.Create<Microsoft.UI.Xaml.Controls.TextBox>(
                item,
                textBox =>
                {
                    textBox.IsReadOnly = true;
                    textBox.TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap;
                    textBox.MinWidth = 800;
                    textBox.MaxWidth = 800;
                },
                BindingAssignment.To(
                    nameof(Microsoft.UI.Xaml.Controls.TextBox.Text),
                    new Binding("RowDetails") { TargetNullValue = string.Empty }));
        }

        // ── Factory: flags DataGrid for PE-header fields ──────────────────────────────
        private static Microsoft.UI.Xaml.FrameworkElement? BuildHeaderFlagsDataGrid(
            object? item,
            Microsoft.UI.Xaml.DependencyObject? templatedParent)
        {
            var rowDetails = System.Windows.Data.BindingEvaluator.Evaluate(
                item,
                new Binding("RowDetails")) as System.Collections.IEnumerable;
            if (rowDetails == null)
                return null;

            return WpfTemplateFactory.Create<DataGrid>(
                item,
                grid =>
                {
                    grid.CanUserAddRows = false;
                    grid.CanUserDeleteRows = false;
                    grid.CanUserReorderColumns = false;
                    grid.HeadersVisibility = DataGridHeadersVisibility.None;
                    grid.IsReadOnly = true;
                    grid.GridLinesVisibility = DataGridGridLinesVisibility.None;
                    grid.SelectionMode = DataGridSelectionMode.Single;
                    grid.SelectionUnit = DataGridSelectionUnit.FullRow;
                    grid.AutoGenerateColumns = false;
                    WpfTemplateFactory.ApplyColumns(
                        grid,
                        DataGridColumnSpec.CheckBox("Value", new Binding("Value")),
                        DataGridColumnSpec.Text("Meaning", new Binding("Meaning")));
                },
                BindingAssignment.To(nameof(DataGrid.ItemsSource), new Binding("RowDetails")));
        }
    }
}
