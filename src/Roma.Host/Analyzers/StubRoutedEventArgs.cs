using ICSharpCode.ILSpyX.TreeView.PlatformAbstractions;

namespace Roma.Host.Analyzers;

internal sealed class StubRoutedEventArgs : IPlatformRoutedEventArgs
{
    public static readonly StubRoutedEventArgs Instance = new();
    public bool Handled { get; set; }
}
