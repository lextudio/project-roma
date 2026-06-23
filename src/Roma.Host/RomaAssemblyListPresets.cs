using System;
using System.IO;

using ICSharpCode.ILSpyX;

namespace Roma.Host;

// Roma-specific assembly-list presets. The ".NET 4 (WPF)" / ".NET 3.5" / "ASP.NET (MVC3)" presets are
// handled by AssemblyListManager.CreateDefaultList (GAC-based, Windows-only). The "Uno Platform" preset
// is Roma's own cross-platform preset: a *curated* set of the most typical Uno core assemblies,
// resolved from the running app's directory (Roma is itself an Uno app, so they're in its bin) — not
// the whole runtime, which would be ~170 DLLs.
internal static class RomaAssemblyListPresets
{
    public const string UnoPlatform = "Uno Platform";

    // The typical Uno Platform core assemblies a user would expect to browse. Deliberately small;
    // platform-specific (Runtime.Skia.*), tooling (HotDesign/RemoteControl), fonts and themes are
    // excluded. Only those present in the app directory are added.
    static readonly string[] UnoCore =
    {
        "Uno",
        "Uno.Foundation",
        "Uno.Foundation.Logging",
        "Uno.UI",
        "Uno.UI.Dispatching",
        "Uno.UI.Composition",
        "Uno.UI.Toolkit",
        "Uno.Xaml",
    };

    public static bool IsUnoPlatform(string key)
        => string.Equals(key, UnoPlatform, StringComparison.Ordinal);

    public static AssemblyList CreateUnoPlatformList(AssemblyListManager manager)
    {
        var list = manager.CreateList(UnoPlatform);
        var baseDir = AppContext.BaseDirectory;
        foreach (var name in UnoCore)
        {
            var path = Path.Combine(baseDir, name + ".dll");
            if (File.Exists(path))
                list.OpenAssembly(path);
        }
        return list;
    }
}
