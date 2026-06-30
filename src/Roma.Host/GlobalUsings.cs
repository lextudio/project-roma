global using System.Collections.Immutable;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Logging;
global using ApplicationExecutionState = Windows.ApplicationModel.Activation.ApplicationExecutionState;

// Resolve type ambiguities for linked upstream ILSpy files.
// ImageSource: the icon catalogue (Images.*) must yield real, renderable image handles so
// linked upstream UI (e.g. SmartTextOutputExtensions.AddButton) can show SVG glyphs. The WPF
// shim's System.Windows.Media.ImageSource is an empty placeholder that nothing can render, so
// alias to the WinUI type and feed SvgImageSource through it.
global using ImageSource = Microsoft.UI.Xaml.Media.ImageSource;
// Dispatcher: System.Windows.Dispatcher (WinUI shim) vs System.Windows.Threading.Dispatcher
global using Dispatcher = System.Windows.Threading.Dispatcher;
// Color: System.Windows.Media.Color vs Windows.UI.Color
global using Color = Windows.UI.Color;
// TextBlock: our WPF shim (not WinUI) so Inlines.Add(Bold/Run/LineBreak) type-checks
global using TextBlock = System.Windows.Controls.TextBlock;
// FrameworkPropertyMetadata: disambiguate System.Windows shim vs Microsoft.UI.Xaml
global using FrameworkPropertyMetadata = System.Windows.FrameworkPropertyMetadata;
// Geometry: WPF Point/Size map to Windows.Foundation (matching WindowsShims). Rect stays
// the local System.Windows.Rect record (it carries the WPF static Transform helper).
global using Point = Windows.Foundation.Point;
global using Size = Windows.Foundation.Size;
// DependencyObject/VisualTreeHelper: route the WPF visual-tree helpers in linked ILSpy
// files to the WinUI implementations (matching WindowsShims' own aliasing).
global using DependencyObject = Microsoft.UI.Xaml.DependencyObject;
global using VisualTreeHelper = Microsoft.UI.Xaml.Media.VisualTreeHelper;
// RoutedEventHandler: inline WinUI buttons use the WinUI click delegate.
global using RoutedEventHandler = Microsoft.UI.Xaml.RoutedEventHandler;
// Application: linked ILSpy code (AssemblyTreeModel) uses System.Windows.Application
// (Current.Dispatcher / Current.MainWindow). Bare `Application` therefore resolves to the
// WPF shim, not Microsoft.UI.Xaml.Application — the two Roma host usages qualify explicitly.
global using Application = System.Windows.Application;
// Matrix: WPF affine matrix shim (ExtensionMethods DPI transforms).
global using Matrix = System.Windows.Media.Matrix;
// Font types: linked AvalonEdit renderers (ITextMarker/TextMarkerService) use WPF FontWeight/
// FontStyle; alias to the WinUI equivalents (matching WindowsShims' own GlobalUsings).
global using FontWeight = Windows.UI.Text.FontWeight;
global using FontStyle = Windows.UI.Text.FontStyle;
// TextEditor: DecompilerTextEditor extends ICSharpCode.AvalonEdit.TextEditor. UnoEdit's editor now
// lives in that canonical namespace (Phase 3 migration), so `using ICSharpCode.AvalonEdit;` resolves
// bare `TextEditor` exactly like WPF — no Roma-specific alias needed anymore.
// DecompilerTextView is a hostable WinUI control; disambiguate UserControl + the
// DataContextChanged arg from the System.Windows shims (the WPF base is excluded on Uno).
global using UserControl = Microsoft.UI.Xaml.Controls.UserControl;
global using DependencyPropertyChangedEventArgs = Microsoft.UI.Xaml.DependencyPropertyChangedEventArgs;
// Util namespace: GlobalUtils, ShellHelper, MessageBus<T> are referenced unqualified by
// linked tree nodes (upstream relies on it being in scope).
global using ICSharpCode.ILSpy.Util;
// ContextMenuEntry.cs uses System.Windows.Controls types without the namespace import
// (guarded out by #if !ROMA_UNO to avoid ambiguity with Microsoft.UI.Xaml.Controls):
global using ToolTip = System.Windows.Controls.ToolTip;
global using RoutedEventArgs = System.Windows.RoutedEventArgs;
global using ControlTemplate = System.Windows.Controls.ControlTemplate;
global using Binding = System.Windows.Data.Binding;
global using PropertyPath = System.Windows.Data.PropertyPath;
global using Button = Microsoft.UI.Xaml.Controls.Button;
global using DataGrid = System.Windows.Controls.DataGrid;
global using ListBox = System.Windows.Controls.ListBox;
global using ContextMenu = System.Windows.Controls.ContextMenu;
global using MenuItem = System.Windows.Controls.MenuItem;
global using Separator = System.Windows.Controls.Separator;
global using Image = Microsoft.UI.Xaml.Controls.Image;
global using Orientation = Microsoft.UI.Xaml.Controls.Orientation;
global using Popup = Microsoft.UI.Xaml.Controls.Primitives.Popup;
