using System.ComponentModel;
using System.Xml.Linq;

using ICSharpCode.ILSpyX;

namespace System.Windows
{
    // WindowState now lives in WindowsShims (System.Windows.WindowState) so it is a single
    // canonical enum shared with the linked AssemblyTreeModel + Application shim.

    public readonly record struct Rect(double X, double Y, double Width, double Height)
    {
        // WPF Rect.Transform(rect, matrix): apply the affine matrix to the rect's
        // corners and return the axis-aligned bounds. Our DPI matrices are scale +
        // translation, so transforming origin and size is sufficient.
        public static Rect Transform(Rect rect, System.Windows.Media.Matrix matrix)
        {
            var topLeft = matrix.Transform(new global::Windows.Foundation.Point(rect.X, rect.Y));
            var bottomRight = matrix.Transform(new global::Windows.Foundation.Point(rect.X + rect.Width, rect.Y + rect.Height));
            return new Rect(
                Math.Min(topLeft.X, bottomRight.X),
                Math.Min(topLeft.Y, bottomRight.Y),
                Math.Abs(bottomRight.X - topLeft.X),
                Math.Abs(bottomRight.Y - topLeft.Y));
        }
    }
}

// ThemeManager is now the real ICSharpCode.ILSpy.Themes.ThemeManager (linked + conditionally
// compiled in Roma.Host.csproj), which loads per-theme SyntaxColor palettes so all six themes
// render distinct syntax colors. The former local stub (single light palette + HSL dark-adapt)
// was removed.

namespace ICSharpCode.ILSpy.Docking
{
    // DockLayoutSettings is now the real upstream type (linked + ROMA_UNO-adapted for UnoDock's
    // Stream-based XmlLayoutSerializer), so the dock layout actually round-trips via SessionSettings.

    // DockWorkspace: real upstream type linked (Session 26). Roma supplies the four
    // ILayoutUpdateStrategy members (declared on the base in DockWorkspace.cs, implemented
    // only in the unlinked DockWorkspace.wpf.cs) via this partial.
    partial class DockWorkspace
    {
        // Names assigned to the empty target panes in MainPage.CreateDockLayout(). Tool panes are
        // routed to them by ContentId (mirrors ILSpy's DockWorkspace.wpf.cs BeforeInsertAnchorable,
        // which probes by model type — Roma routes by id because the anchorables don't exist yet).
        internal const string AssemblyListPaneName = "assemblyListPane";
        internal const string ToolPaneTopName = "toolPaneTop";
        internal const string ToolPaneBottomName = "toolPaneBottom";

        public bool BeforeInsertAnchorable(global::AvalonDock.Layout.LayoutRoot layout, global::AvalonDock.Layout.LayoutAnchorable anchorableToShow, global::AvalonDock.Layout.ILayoutContainer destinationContainer)
        {
            var paneName = anchorableToShow?.ContentId switch
            {
                "assemblyListPane" => AssemblyListPaneName,
                "searchPane" => ToolPaneTopName,
                "analyzerPane" => ToolPaneBottomName,
                _ => null,
            };
            if (paneName is null)
                return false;

            var target = System.Linq.Enumerable.FirstOrDefault(
                System.Linq.Enumerable.OfType<global::AvalonDock.Layout.LayoutAnchorablePane>(
                    global::AvalonDock.Layout.Extensions.Descendents(layout)),
                p => p.Name == paneName);
            if (target is null)
                return false;

            anchorableToShow.CanDockAsTabbedDocument = false;
            target.Children.Add(anchorableToShow);
            return true;
        }

        public void AfterInsertAnchorable(global::AvalonDock.Layout.LayoutRoot layout, global::AvalonDock.Layout.LayoutAnchorable anchorableShown)
        {
            anchorableShown.IsActive = true;
            anchorableShown.IsSelected = true;
        }

        public bool BeforeInsertDocument(global::AvalonDock.Layout.LayoutRoot layout, global::AvalonDock.Layout.LayoutDocument anchorableToShow, global::AvalonDock.Layout.ILayoutContainer destinationContainer) => false;

        public void AfterInsertDocument(global::AvalonDock.Layout.LayoutRoot layout, global::AvalonDock.Layout.LayoutDocument anchorableShown) { }
    }
}


namespace ICSharpCode.ILSpy
{
    public class SettingsService : Util.SettingsService
    {
    }

    public sealed class RomaCSharpLanguage : CSharpLanguage
    {
    }

    public sealed class RomaILLanguage : Language
    {
        public override string Name => "IL";

        public override string FileExtension => ".il";
    }

}
