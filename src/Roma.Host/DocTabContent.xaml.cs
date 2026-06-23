using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.ILSpy.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using UnoEdit.Skia.Desktop.Controls;

namespace Roma.Host;

public sealed partial class DocTabContent : UserControl
{
    internal TextEditor CodeEditor => PART_CodeEditor;
    internal FoldingManager? FoldingManager { get; private set; }
    internal ICSharpCode.ILSpy.TextView.DecompilerTextView? Decompiler { get; private set; }
    internal ContentPresenter NodeContent => PART_NodeContent;
    internal FrameworkElement NodeHost => PART_NodeHost;

    public DocTabContent()
    {
        InitializeComponent();

        if (PART_CodeEditor.Document is null)
            PART_CodeEditor.Document = new TextDocument();

        var csharp = HighlightingManager.Instance.GetDefinitionByExtension(".cs");
        if (csharp is not null)
            PART_CodeEditor.HighlightedLineSource = new XshdHighlightedLineSource(csharp);

        PART_CodeEditor.Theme = TextEditorTheme.Light;
        FoldingManager = new FoldingManager(PART_CodeEditor.Document);
        PART_CodeEditor.FoldingManager = FoldingManager;

        Decompiler = new ICSharpCode.ILSpy.TextView.DecompilerTextView(ICSharpCode.ILSpy.App.ExportProvider)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };
        PART_DecompilerHost.Children.Add(Decompiler);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is TabPageModel model)
                MainPage.RegisterTabContent(model, this);
        };
    }
}
