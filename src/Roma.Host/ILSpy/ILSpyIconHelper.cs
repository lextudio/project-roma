// Shared URI helper for ILSpyIcons SVG assets.
//
// ms-appx:/// resolution on macOS inside a .app bundle resolves from Contents/Resources/
// (NSBundle convention) rather than Contents/MacOS/ where the SVGs are published.
// This helper constructs file:// URIs via AppContext.BaseDirectory instead, which always
// points to the directory containing the executable (Contents/MacOS/ in a .app bundle).

using System;
using System.IO;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Roma.Host;

internal static class ILSpyIconHelper
{
    private const string IconFolder = "ILSpyIcons";

    internal static Uri GetUri(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, IconFolder, file);
        if (File.Exists(path))
            return new Uri(path);
        return new Uri($"ms-appx:///{IconFolder}/{file}");
    }

    internal static SvgImageSource GetSource(string file) => new(GetUri(file));
}

[MarkupExtensionReturnType(ReturnType = typeof(ImageSource))]
public sealed class ILSpyIconExtension : MarkupExtension
{
    public string File { get; set; } = "";

    protected override object ProvideValue() => ILSpyIconHelper.GetSource(File);
}
