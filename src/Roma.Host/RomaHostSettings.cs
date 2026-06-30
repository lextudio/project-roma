using System.ComponentModel;
using System.Xml.Linq;

using ICSharpCode.ILSpyX.Settings;

using TomsToolbox.Wpf;

namespace Roma.Host;

public sealed class RomaHostSettings : ObservableObjectBase, ISettingsSection
{
    private bool hideMacOSAppMenu;
    private string preferredTerminalApp = string.Empty;
    private string customTerminalPath = string.Empty;
    private bool recentFontsEnabled = true;

    public bool HideMacOSAppMenu
    {
        get => hideMacOSAppMenu;
        set => SetProperty(ref hideMacOSAppMenu, value);
    }

    // Preferred terminal for "Open command line here" (interpreted per current OS; empty = OS default).
    // See GlobalUtils.OpenTerminalAt for the recognized values per platform.
    public string PreferredTerminalApp
    {
        get => preferredTerminalApp;
        set => SetProperty(ref preferredTerminalApp, value);
    }

    // Path to a custom terminal executable, used when PreferredTerminalApp is "Custom".
    public string CustomTerminalPath
    {
        get => customTerminalPath;
        set => SetProperty(ref customTerminalPath, value);
    }

    // Whether the recently-used fonts list is maintained (RecentFontsCache).
    public bool RecentFontsEnabled
    {
        get => recentFontsEnabled;
        set => SetProperty(ref recentFontsEnabled, value);
    }

    public XName SectionName => "RomaHostSettings";

    public void LoadFromXml(XElement section)
    {
        HideMacOSAppMenu = (bool?)section.Attribute(nameof(HideMacOSAppMenu)) ?? false;
        PreferredTerminalApp = (string?)section.Attribute(nameof(PreferredTerminalApp)) ?? string.Empty;
        CustomTerminalPath = (string?)section.Attribute(nameof(CustomTerminalPath)) ?? string.Empty;
        RecentFontsEnabled = (bool?)section.Attribute(nameof(RecentFontsEnabled)) ?? true;
    }

    public XElement SaveToXml()
    {
        var section = new XElement(SectionName);
        section.SetAttributeValue(nameof(HideMacOSAppMenu), HideMacOSAppMenu);
        section.SetAttributeValue(nameof(PreferredTerminalApp), PreferredTerminalApp);
        section.SetAttributeValue(nameof(CustomTerminalPath), CustomTerminalPath);
        section.SetAttributeValue(nameof(RecentFontsEnabled), RecentFontsEnabled);
        return section;
    }
}
