using System;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Roma.Host;

// Wraps the classic XSHD/DocumentHighlighter as an IHighlightedLineSource so the
// UnoEdit TextEditor can render AvalonEdit syntax highlighting (e.g. C#), exactly
// as the UnoEdit sample does. The C# definition is loaded from UnoEdit's embedded
// AvalonEdit resources via HighlightingManager.Instance.GetDefinitionByExtension(".cs").
internal sealed class XshdHighlightedLineSource : IHighlightedLineSource
{
    private readonly IHighlightingDefinition _definition;
    private DocumentHighlighter? _highlighter;

    public event EventHandler? HighlightingInvalidated;

    public XshdHighlightedLineSource(IHighlightingDefinition definition)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    public void SetDocument(TextDocument? document)
    {
        _highlighter?.Dispose();
        _highlighter = null;

        if (document != null)
            _highlighter = new DocumentHighlighter(document, _definition);

        HighlightingInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public HighlightedLine HighlightLine(int lineNumber)
    {
        if (_highlighter is null)
            throw new InvalidOperationException("No document attached to highlighter.");
        return _highlighter.HighlightLine(lineNumber);
    }

    public void Dispose()
    {
        _highlighter?.Dispose();
        _highlighter = null;
    }
}
