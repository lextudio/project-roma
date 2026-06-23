using System;
using System.Linq;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using UnoEdit.Skia.Desktop.Controls;

namespace ICSharpCode.ILSpy.TextView
{
    // Code-built replacement for DecompilerTextView.xaml's InitializeComponent. Roma has no
    // linked Uno XAML; the real control's ctor/body only reach the named elements (textEditor,
    // waitAdorner, progressBar, progressTitle, progressText), so we declare those as fields and
    // assemble an equivalent visual tree here. The WPF ZoomScrollViewer chrome / styles are
    // dropped (the UnoEdit editor brings its own scrolling).
    partial class DecompilerTextView
    {
        private DecompilerTextEditor textEditor = null!;
        private Border waitAdorner = null!;
        private ProgressBar progressBar = null!;
        private TextBlock progressTitle = null!;
        private TextBlock progressText = null!;

        // Pointer position captured at PointerPressed (relative to textEditor.TextArea.TextView).
        // Used by GetPositionFromMousePosition() at release/click time, mirroring WPF Mouse.GetPosition.
        internal Windows.Foundation.Point _lastPointerPos;

        // Document offset under the pointer, computed from the live event in content space via
        // UnoEdit's folding-aware hit-test. -1 when off any text. Consumed by
        // GetReferenceSegmentAtMousePosition on the ROMA_UNO path.
        internal int _lastHoverOffset = -1;
        private Windows.Foundation.Point _pointerDownPos;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hoverTimer;
        private FlowDocumentTooltip? _hoverPopup;
        private ReferenceSegment? _hoverReferenceSegment;

        private void InitializeComponent()
        {
            textEditor = new DecompilerTextEditor
            {
                IsReadOnly = true,
                Theme = TextEditorTheme.Light,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            progressTitle = new TextBlock
            {
                FontSize = 18,
                Margin = new Thickness(3),
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            progressBar = new ProgressBar { Height = 16, Width = 400 };
            progressText = new TextBlock { Margin = new Thickness(3), Visibility = Visibility.Collapsed };

            var panel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { progressTitle, progressBar, progressText },
            };
            waitAdorner = new Border
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Visibility = Visibility.Collapsed,
                Child = panel,
            };

            var grid = new Grid();
            grid.Children.Add(textEditor);
            grid.Children.Add(waitAdorner);

            // DecompilerTextView derives from the System.Windows.Controls.UserControl shim;
            // its Content hosts the WinUI visual tree.
            this.Content = grid;

            // Reference-navigation pointer handlers (ROMA_UNO equivalent of WPF PreviewMouseDown/Up).
            // All handlers target TextView (the control that actually renders text and receives pointer
            // events) — they do not bubble to TextArea. TextView's own OnRootPointerPressed/Released
            // set e.Handled=true for a click (it starts/ends a selection drag), so a plain `+=`
            // subscription (handledEventsToo=false) would be skipped. Use AddHandler with
            // handledEventsToo:true so reference navigation still runs after the selection handler.
            var tv = textEditor.TextArea.TextView;
            tv.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnTextAreaPointerPressed), handledEventsToo: true);
            tv.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnTextAreaPointerReleased), handledEventsToo: true);
            tv.PointerMoved += OnTextViewPointerMoved;
            tv.PointerExited += OnTextViewPointerExited;

            _hoverTimer = DispatcherQueue.CreateTimer();
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(450);
            _hoverTimer.IsRepeating = false;
            _hoverTimer.Tick += OnHoverTimerTick;
        }

        // Applies the light/dark editor theme to this view's text editor (called by the Theme menu).
        internal void SetEditorTheme(bool dark)
        {
            if (textEditor is not null)
                textEditor.Theme = dark ? TextEditorTheme.Dark : TextEditorTheme.Light;
        }

        // Overload used by the host theme switcher to supply an editor theme whose background was
        // already resolved from the active ILSpy theme (e.g. the Light theme's pale-yellow surface).
        internal void SetEditorTheme(TextEditorTheme theme)
        {
            if (textEditor is not null)
                textEditor.Theme = theme;
        }

        private void OnTextAreaPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _pointerDownPos = e.GetCurrentPoint(textEditor.TextArea.TextView).Position;
        }

        private void OnTextAreaPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _lastPointerPos = e.GetCurrentPoint(textEditor.TextArea.TextView).Position;
            // Also update the hover offset from the release position — now safe because the event
            // comes from TextView (same element as PointerMoved), so coordinate spaces match.
            _lastHoverOffset = textEditor.TextArea.TextView.GetOffsetFromPointerEvent(e);

            var dx = _lastPointerPos.X - _pointerDownPos.X;
            var dy = _lastPointerPos.Y - _pointerDownPos.Y;
            if (Math.Abs(dx) >= System.Windows.SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(dy) >= System.Windows.SystemParameters.MinimumVerticalDragDistance)
                return;

            var props = e.GetCurrentPoint(textEditor.TextArea.TextView).Properties;
            bool isMiddle = props.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased;
            bool isLeft = props.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased;
            if (!isLeft && !isMiddle)
                return;

            var referenceSegment = GetReferenceSegmentAtMousePosition();
            if (referenceSegment == null)
            {
                ClearLocalReferenceMarks();
            }
            else if (referenceSegment.IsLocal || !referenceSegment.IsDefinition)
            {
                textEditor.TextArea.ClearSelection();
                JumpToReference(referenceSegment, isMiddle);
            }
        }

        private void OnTextViewPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            _lastPointerPos = e.GetCurrentPoint(textEditor.TextArea.TextView).Position;
            _lastHoverOffset = textEditor.TextArea.TextView.GetOffsetFromPointerEvent(e);

            // Keep the tooltip stable while the pointer stays over the same reference. Closing and
            // re-arming the timer on every micro-move makes the tooltip flash as the mouse settles.
            var segment = GetReferenceSegmentAtMousePosition();
            if (_hoverPopup is { IsOpen: true } && ReferenceEquals(segment, _hoverReferenceSegment))
                return;

            // Mirror VisualLineReferenceText.wpf.cs: show hand cursor over clickable references.
            bool isLink = segment is not null && (segment.IsLocal || !segment.IsDefinition);
            ProtectedCursor = isLink
                ? Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand)
                : null;

            CloseHoverToolTip();
            if (segment != null)
            {
                _hoverTimer?.Stop();
                _hoverTimer?.Start();
            }
        }

        private void OnTextViewPointerExited(object sender, PointerRoutedEventArgs e)
        {
            _hoverTimer?.Stop();
            CloseHoverToolTip();
            ProtectedCursor = null;
        }

        private void OnHoverTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            sender.Stop();

            var referenceSegment = GetReferenceSegmentAtMousePosition();
            if (referenceSegment == null || ReferenceEquals(referenceSegment, _hoverReferenceSegment))
                return;

            // Reuse the shared (WPF) tooltip builder: it produces a FlowDocumentTooltip whose rich
            // documentation renders through the FlowDocument shim. A WinUI popup positions itself
            // relative to its XamlRoot's origin, so project the hover point into that space and
            // drop the tooltip just below the hovered line.
            if (GenerateTooltip(referenceSegment) is not FlowDocumentTooltip popup)
                return;

            _hoverReferenceSegment = referenceSegment;
            _hoverPopup = popup;

            var origin = textEditor.TextArea.TextView
                .TransformToVisual(null)
                .TransformPoint(_lastPointerPos);
            double lineHeight = settingsService.DisplaySettings.SelectedFontSize * 1.4;
            popup.XamlRoot = XamlRoot;
            popup.HorizontalOffset = origin.X - 4;
            popup.VerticalOffset = origin.Y + lineHeight;
            popup.IsOpen = true;
        }

        private void CloseHoverToolTip()
        {
            if (_hoverPopup != null)
            {
                _hoverPopup.IsOpen = false;
                _hoverPopup = null;
                _hoverReferenceSegment = null;
            }
        }

    }
}
