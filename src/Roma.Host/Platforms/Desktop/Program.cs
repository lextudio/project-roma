using Uno.UI.Hosting;

namespace Roma.Host;

internal class Program
{
    [STAThread]
    public static async Task Main(string[] args)
    {
        MainPage.ResetLog();
        App.InitializeLogging();

        // macOS: claim the open-documents Apple Event before the NSApp run loop starts, so a cold
        // "Open With Roma" launch (which delivers the event during AppKit's finishLaunching, before
        // App.OnLaunched) is caught rather than rejected by AppKit's default handler.
        MacOSFileDrop.InstallEarly();

        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseX11()
            .UseLinuxFrameBuffer()
            .UseMacOS()
            .UseWin32()
            .Build();

        await host.RunAsync();
    }
}
