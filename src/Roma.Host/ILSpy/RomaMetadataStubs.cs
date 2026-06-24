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

        public MetadataTableViews()
        {
            foreach (var (key, value) in BuildResources())
                this[key] = value;
        }

        private static IEnumerable<(string Key, object Value)> BuildResources()
        {
            // ── Filter templates ──────────────────────────────────────────────────────
            yield return ("DefaultFilter",
                new DataGridExtensions.FilterControlTemplate(DataGridExtensions.FilterKind.Text));
            yield return ("HexFilter",
                new DataGridExtensions.FilterControlTemplate(DataGridExtensions.FilterKind.Hex));

            yield return ("AssemblyFlagsFilter",
                Flags(typeof(AssemblyFlags)));
            yield return ("AssemblyHashAlgorithmFilter",
                Flags(typeof(AssemblyHashAlgorithm)));
            yield return ("MethodAttributesFilter",
                Flags(typeof(MethodAttributes)));
            yield return ("MethodImplAttributesFilter",
                Flags(typeof(MethodImplAttributes)));
            yield return ("MethodSemanticsAttributesFilter",
                Flags(typeof(MethodSemanticsAttributes)));
            yield return ("TypeAttributesFilter",
                Flags(typeof(TypeAttributes)));
            yield return ("PropertyAttributesFilter",
                Flags(typeof(PropertyAttributes)));
            yield return ("EventAttributesFilter",
                Flags(typeof(EventAttributes)));
            yield return ("FieldAttributesFilter",
                Flags(typeof(FieldAttributes)));
            yield return ("ManifestResourceAttributesFilter",
                Flags(typeof(ManifestResourceAttributes)));
            yield return ("GenericParameterAttributesFilter",
                Flags(typeof(GenericParameterAttributes)));
            yield return ("PInvokeAttributesFilter",
                Flags(typeof(Mono.Cecil.PInvokeAttributes)));

            // ── DataGridCellStyle ────────────────────────────────────────────────────
            // Mirrors the WPF style: BorderThickness=0, Padding=2, VerticalContentAlignment=Center.
            // The ControlTemplate setter in the WPF original is skipped — the shim cell builds its
            // own visual tree and applying a WinUI ControlTemplate would break it.
            var cellStyle = new Microsoft.UI.Xaml.Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Microsoft.UI.Xaml.Setter(
                Microsoft.UI.Xaml.Controls.Control.BorderThicknessProperty,
                new Microsoft.UI.Xaml.Thickness(0)));
            cellStyle.Setters.Add(new Microsoft.UI.Xaml.Setter(
                Microsoft.UI.Xaml.Controls.Control.PaddingProperty,
                new Microsoft.UI.Xaml.Thickness(2)));
            cellStyle.Setters.Add(new Microsoft.UI.Xaml.Setter(
                Microsoft.UI.Xaml.Controls.Control.VerticalContentAlignmentProperty,
                Microsoft.UI.Xaml.VerticalAlignment.Center));
            yield return ("DataGridCellStyle", cellStyle);

            // ── ItemContainerStyle ───────────────────────────────────────────────────
            var itemStyle = new Microsoft.UI.Xaml.Style(typeof(ListViewItem));
            itemStyle.Setters.Add(new Microsoft.UI.Xaml.Setter(
                Microsoft.UI.Xaml.Controls.Control.HorizontalContentAlignmentProperty,
                Microsoft.UI.Xaml.HorizontalAlignment.Stretch));
            yield return ("ItemContainerStyle", itemStyle);

            // ── Row-details DataTemplates ────────────────────────────────────────────
            // These replace the WPF DataTemplates that used {Binding RowDetails}.
            // ShimDataTemplate carries a C# factory that BuildRowDetails invokes directly,
            // bypassing WinUI's ContentTemplate mechanism (which cannot handle WPF bindings).

            yield return ("CustomDebugInformationDetailsDataGrid",
                new System.Windows.Controls.ShimDataTemplate(BuildCustomDebugInfoDataGrid));

            yield return ("CustomDebugInformationDetailsTextBlob",
                new System.Windows.Controls.ShimDataTemplate(BuildCustomDebugInfoTextBlob));

            yield return ("HeaderFlagsDetailsDataGrid",
                new System.Windows.Controls.ShimDataTemplate(BuildHeaderFlagsDataGrid));
        }

        // Helper for flags filter templates.
        private static DataGridExtensions.FilterControlTemplate Flags(Type t)
            => new(DataGridExtensions.FilterKind.Flags, t);

        // ── Factory: nested DataGrid for structured debug info ────────────────────────
        private static Microsoft.UI.Xaml.FrameworkElement? BuildCustomDebugInfoDataGrid(object? item)
        {
            if (item is not CustomDebugInformationTableTreeNode.CustomDebugInformationEntry entry)
                return null;
            var rowDetails = entry.RowDetails as System.Collections.IEnumerable;
            if (rowDetails == null)
                return null;

            var grid = new DataGrid
            {
                AutoGenerateColumns = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserReorderColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                IsReadOnly = true,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                MaxHeight = 250,
            };

            grid.AutoGeneratingColumn += Helpers.View_AutoGeneratingColumn;
            grid.AutoGeneratedColumns += Helpers.View_AutoGeneratedColumns;
            grid.ItemsSource = rowDetails;
            return grid;
        }

        // ── Factory: read-only TextBox for blob debug info ────────────────────────────
        private static Microsoft.UI.Xaml.FrameworkElement? BuildCustomDebugInfoTextBlob(object? item)
        {
            if (item is not CustomDebugInformationTableTreeNode.CustomDebugInformationEntry entry)
                return null;
            var text = entry.RowDetails?.ToString() ?? string.Empty;

            return new Microsoft.UI.Xaml.Controls.TextBox
            {
                IsReadOnly = true,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                Text = text,
                MinWidth = 800,
                MaxWidth = 800,
            };
        }

        // ── Factory: flags DataGrid for PE-header fields ──────────────────────────────
        private static Microsoft.UI.Xaml.FrameworkElement? BuildHeaderFlagsDataGrid(object? item)
        {
            // item is a MetadataTreeNode.Entry; RowDetails is IList<MetadataTreeNode.BitEntry>.
            // Both are internal, so access via reflection is needed only if we can't use them
            // directly — but since this file is in the same assembly, we can use them directly.
            var entry = item as Entry;
            var rowDetails = entry?.RowDetails;
            if (rowDetails == null)
                return null;

            var grid = new DataGrid
            {
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserReorderColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.None,
                IsReadOnly = true,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                AutoGenerateColumns = false,
            };

            grid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "Value",
                Binding = new Binding("Value"),
                IsReadOnly = true,
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Meaning",
                Binding = new Binding("Meaning"),
                IsReadOnly = true,
            });

            grid.ItemsSource = rowDetails;
            return grid;
        }
    }
}
