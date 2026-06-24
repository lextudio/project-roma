using System.ComponentModel;
using System.Xml.Linq;

using ICSharpCode.ILSpyX.Settings;

using TomsToolbox.Wpf;

namespace Roma.Host;

public sealed class RomaHostSettings : ObservableObjectBase, ISettingsSection
{
    private bool hideMacOSAppMenu;

    public bool HideMacOSAppMenu
    {
        get => hideMacOSAppMenu;
        set => SetProperty(ref hideMacOSAppMenu, value);
    }

    public XName SectionName => "RomaHostSettings";

    public void LoadFromXml(XElement section)
    {
        HideMacOSAppMenu = (bool?)section.Attribute(nameof(HideMacOSAppMenu)) ?? false;
    }

    public XElement SaveToXml()
    {
        var section = new XElement(SectionName);
        section.SetAttributeValue(nameof(HideMacOSAppMenu), HideMacOSAppMenu);
        return section;
    }
}
