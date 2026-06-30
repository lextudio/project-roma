namespace ICSharpCode.ILSpy
{
    // Minimal stand-in for the WPF MainWindow. The real one is a full WPF Window with the whole
    // command/docking surface; the linked DecompilerTextView + DocumentationUIBuilder only reach
    // for CommandBindings (SearchPanel.RegisterCommands) and ActualWidth (tooltip page width).
    // Supplied by RomaExportProvider so GetExportedValue<MainWindow>() resolves.
    public sealed class MainWindow
    {
        public System.Windows.Input.CommandBindingCollection CommandBindings { get; } = new();

        // The Uno host window width; default to a sensible value (tooltip max page width).
        public double ActualWidth { get; set; } = 1024;

        // No WPF taskbar item on Uno; stays null so DecompilerTextView's taskbar-progress
        // blocks are skipped at runtime (they only need this to compile).
        public System.Windows.Shell.TaskbarItemInfo? TaskbarItemInfo { get; }

        // Stub for ExitCommand — no WPF Window to close on Uno.
        public void Close() { }
    }
}
