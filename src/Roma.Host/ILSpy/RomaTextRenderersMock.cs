// Session 27: MOCK drawing layer for the real DecompilerTextView port.
//
// The upstream TextMarkerService + BracketHighlightRenderer draw with the WPF Media surface
// (StreamGeometry / Pen.Freeze / DashStyles / Rect corner points / BackgroundGeometryBuilder
// returning a WPF Geometry). UnoEdit's geometry builder returns `object` (WPF/WinUI erasure)
// and the Skia draw path differs, so the real draw bodies don't compile yet. Per plan, the
// drawing is MOCKED for now: these renderers expose the exact API DecompilerTextView consumes
// (Create/Remove markers, SetHighlight) and register as background renderers / line
// transformers, but their Draw/Transform are no-ops. Replace with the real linked files once
// the WPF Media drawing parity lands in WindowsShims + UnoEdit (see docs/session27.md).

using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.AvalonEdit.Rendering;

using DrawingContext = System.Windows.Media.DrawingContext;
// The Skia editor TextView has been merged into the canonical ICSharpCode.AvalonEdit.Rendering.TextView
// (the former headless host + the Skia control are now one class). Both the IBackgroundRenderer.Draw
// contract type and the type TextArea.TextView returns are therefore the same — a single alias suffices.
// (`TextView` alone collides with the ICSharpCode.ILSpy.TextView namespace, so we keep aliasing.)
using RenderTextView = ICSharpCode.AvalonEdit.Rendering.TextView;
using EditorTextView = ICSharpCode.AvalonEdit.Rendering.TextView;

namespace ICSharpCode.ILSpy.AvalonEdit
{
    // Tracks markers (real offsets/colors used by reference highlighting bookkeeping) but does
    // not paint them. Implements the same interfaces the real service does so DecompilerTextView
    // can add it to TextView.BackgroundRenderers and TextView.LineTransformers unchanged.
    public sealed class TextMarkerService : ITextMarkerService, IBackgroundRenderer, IVisualLineTransformer
    {
        private readonly EditorTextView textView;
        private readonly List<TextMarker> markers = new();

        public TextMarkerService(EditorTextView textView)
        {
            this.textView = textView ?? throw new ArgumentNullException(nameof(textView));
        }

        // Default local-reference highlight (the WPF FindResource color-set is guarded on Uno).
        private static readonly Windows.UI.Color DefaultMarkerColor = Windows.UI.Color.FromArgb(70, 255, 200, 0);

        public ITextMarker Create(int startOffset, int length)
        {
            var marker = new TextMarker(this, startOffset, length);
            markers.Add(marker);
            Repaint();
            return marker;
        }

        public IEnumerable<ITextMarker> TextMarkers => markers;

        public void Remove(ITextMarker marker)
        {
            if (marker is TextMarker m && markers.Remove(m))
            {
                m.RaiseDeleted();
                Repaint();
            }
        }

        public void RemoveAll(Predicate<ITextMarker> predicate)
        {
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));
            bool changed = false;
            for (int i = markers.Count - 1; i >= 0; i--)
            {
                if (predicate(markers[i]))
                {
                    var m = markers[i];
                    markers.RemoveAt(i);
                    m.RaiseDeleted();
                    changed = true;
                }
            }
            if (changed)
                Repaint();
        }

        // Paint all markers as positioned Rectangles on the overlay's "marker" layer.
        private void Repaint()
        {
            textView.ClearBackgroundHighlights("marker");
            foreach (var m in markers)
            {
                var fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(m.BackgroundColor ?? DefaultMarkerColor);
                textView.AddBackgroundHighlight("marker",
                    new ICSharpCode.AvalonEdit.Document.TextSegment { StartOffset = m.StartOffset, Length = m.Length },
                    fill, null);
            }
        }

        public IEnumerable<ITextMarker> GetMarkersAtOffset(int offset)
            => markers.Where(m => m.StartOffset <= offset && offset <= m.EndOffset);

        // IBackgroundRenderer — mocked (no paint).
        public KnownLayer Layer => KnownLayer.Selection;
        public void Draw(RenderTextView textView, DrawingContext drawingContext) { }

        // IVisualLineTransformer — mocked (no colorization).
        public void Transform(ITextRunConstructionContext context, IList<VisualLineElement> elements) { }

        private sealed class TextMarker : ITextMarker
        {
            private readonly TextMarkerService service;

            public TextMarker(TextMarkerService service, int startOffset, int length)
            {
                this.service = service;
                StartOffset = startOffset;
                Length = length;
            }

            public int StartOffset { get; }
            public int Length { get; }
            public int EndOffset => StartOffset + Length;
            public bool IsDeleted { get; private set; }

            public event EventHandler? Deleted;
            internal void RaiseDeleted() { IsDeleted = true; Deleted?.Invoke(this, EventArgs.Empty); }

            public void Delete() => service.Remove(this);

            public Windows.UI.Color? BackgroundColor { get; set; }
            public Windows.UI.Color? ForegroundColor { get; set; }
            public Windows.UI.Text.FontWeight? FontWeight { get; set; }
            public Windows.UI.Text.FontStyle? FontStyle { get; set; }
            public TextMarkerTypes MarkerTypes { get; set; }
            public Windows.UI.Color MarkerColor { get; set; }
            public object? Tag { get; set; }
            public object? ToolTip { get; set; }
        }
    }
}

namespace ICSharpCode.ILSpy.TextView
{
    // Mocked bracket highlighter: stores the search result and registers as a background
    // renderer, but does not paint the bracket outline (Draw is a no-op).
    public sealed class BracketHighlightRenderer : IBackgroundRenderer
    {
        private readonly EditorTextView textView;

        public BracketHighlightRenderer(EditorTextView textView)
        {
            this.textView = textView ?? throw new ArgumentNullException(nameof(textView));
            this.textView.BackgroundRenderers.Add(this);
        }

        // Session 28: real bracket-match painting via the UnoEdit overlay (UnoRichText-style
        // positioned Rectangles), replacing the no-op mock.
        public void SetHighlight(ICSharpCode.ILSpy.TextView.BracketSearchResult? result)
        {
            textView.ClearBackgroundHighlights("bracket");
            if (result is null)
                return;

            var fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 120, 215));
            var stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(160, 0, 120, 215));

            textView.AddBackgroundHighlight("bracket",
                new ICSharpCode.AvalonEdit.Document.TextSegment { StartOffset = result.OpeningBracketOffset, Length = result.OpeningBracketLength },
                fill, stroke);
            textView.AddBackgroundHighlight("bracket",
                new ICSharpCode.AvalonEdit.Document.TextSegment { StartOffset = result.ClosingBracketOffset, Length = result.ClosingBracketLength },
                fill, stroke);
        }

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(RenderTextView textView, DrawingContext drawingContext) { }
    }
}
