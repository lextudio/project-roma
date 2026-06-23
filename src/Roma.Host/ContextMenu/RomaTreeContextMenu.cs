// Roma host: metadata-driven context-menu builder.
//
// ILSpy (WPF) builds tree/text context menus in ICSharpCode.ILSpy.ContextMenuProvider by
// MEF-discovering every [ExportContextMenuEntry] implementation and grouping them by
// ParentMenuID/Category/Order (see ContextMenuEntry.cs ShowContextMenu/BuildMenu). On Uno that
// provider is compiled to a no-op (#if ROMA_UNO), because Roma replaces MEF with direct
// construction and there is no export catalog to enumerate.
//
// This builder restores ILSpy's exact menu-shaping semantics for the Uno host: callers hand it
// the concrete IContextMenuEntry instances (constructed with their real DI dependencies), and it
// reads each entry's [ExportContextMenuEntry] metadata via reflection, then reproduces ILSpy's
// Order sort, Category separators, and ParentMenuID submenu nesting — emitting a WinUI MenuFlyout
// instead of a WPF ContextMenu. Adding a new entry is therefore just "register the instance":
// header, icon, ordering and grouping all flow from the upstream attribute.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ICSharpCode.ILSpy;

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

using ILSpyResources = ICSharpCode.ILSpy.Properties.Resources;
using RomaResources = Roma.Host.Properties.Resources;

namespace Roma.Host.ContextMenu;

internal sealed class RomaTreeContextMenu
{
    // Maps the ILSpy icon resource paths (case-insensitive) carried by [ExportContextMenuEntry]
    // to the SVG assets bundled under Roma's ILSpyIcons/. Only entries present here render an
    // icon; ILSpy paths with no Roma asset (e.g. "images/Delete", "images/Copy") fall through to
    // no icon, matching what those entries show in ILSpy when the glyph is unavailable.
    private static readonly Dictionary<string, string> IconAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["images/refresh"] = "refresh.svg",
        ["images/save"] = "save.svg",
        ["images/search"] = "search.svg",
        ["images/searchmsdn"] = "search.svg",
    };

    private readonly IReadOnlyList<Entry> _entries;
    private readonly Action<IContextMenuEntry>? _afterExecute;

    /// <param name="entries">
    /// The concrete context-menu entries, already constructed with their dependencies.
    /// </param>
    /// <param name="afterExecute">
    /// Optional hook invoked after an entry's Execute (e.g. to activate the Analyzer pane).
    /// </param>
    internal RomaTreeContextMenu(IEnumerable<IContextMenuEntry> entries, Action<IContextMenuEntry>? afterExecute = null)
    {
        _entries = entries.Select(Entry.FromInstance).ToList();
        _afterExecute = afterExecute;
    }

    /// <summary>
    /// Builds a flyout for the given context, or null if no entry is visible — mirrors
    /// ContextMenuProvider.ShowContextMenu: order by Order, partition by ParentMenuID, then within
    /// each parent group emit category separators and recurse into MenuID submenus.
    /// </summary>
    public MenuFlyout? Build(TextViewContext context)
    {
        var submenusByParent = new Dictionary<string, List<Entry>>();
        List<Entry> topLevel = new();
        foreach (var group in _entries.OrderBy(e => e.Order).GroupBy(e => e.ParentMenuID))
        {
            if (group.Key is null)
                topLevel = group.ToList();
            else
                submenusByParent[group.Key] = group.ToList();
        }

        var flyout = new MenuFlyout();
        BuildMenu(topLevel, flyout.Items, context, submenusByParent);
        return flyout.Items.Count > 0 ? flyout : null;
    }

    private void BuildMenu(
        List<Entry> group,
        IList<MenuFlyoutItemBase> parent,
        TextViewContext context,
        Dictionary<string, List<Entry>> submenusByParent)
    {
        foreach (var category in group.GroupBy(e => e.Category))
        {
            var needSeparatorForCategory = parent.Count > 0;
            foreach (var entry in category)
            {
                if (!entry.Instance.IsVisible(context))
                    continue;

                if (needSeparatorForCategory)
                {
                    parent.Add(new MenuFlyoutSeparator());
                    needSeparatorForCategory = false;
                }

                if (entry.MenuID is not null && submenusByParent.TryGetValue(entry.MenuID, out var children))
                {
                    var subItem = new MenuFlyoutSubItem { Text = entry.Header };
                    if (entry.IconUri is not null)
                        subItem.Icon = MakeIcon(entry.IconUri);
                    BuildMenu(children, subItem.Items, context, submenusByParent);
                    if (subItem.Items.Count > 0)
                        parent.Add(subItem);
                }
                else
                {
                    var item = new MenuFlyoutItem
                    {
                        Text = entry.Header,
                        IsEnabled = entry.Instance.IsEnabled(context),
                    };
                    if (entry.IconUri is not null)
                        item.Icon = MakeIcon(entry.IconUri);

                    var capturedEntry = entry.Instance;
                    var capturedContext = context;
                    item.Click += (_, _) =>
                    {
                        // A faulty entry must not bring down the menu; ILSpy entries are expected to
                        // be resilient, so swallow and continue.
                        try
                        {
                            capturedEntry.Execute(capturedContext);
                            _afterExecute?.Invoke(capturedEntry);
                        }
                        catch
                        {
                        }
                    };
                    parent.Add(item);
                }
            }
        }
    }

    private static IconElement MakeIcon(string uri)
        => new ImageIcon { Source = new SvgImageSource(new Uri(uri)) };

    // A registered entry plus the [ExportContextMenuEntry] metadata read off its type.
    private sealed class Entry
    {
        public required IContextMenuEntry Instance { get; init; }
        public required string Header { get; init; }
        public string? IconUri { get; init; }
        public string? Category { get; init; }
        public double Order { get; init; }
        public string? MenuID { get; init; }
        public string? ParentMenuID { get; init; }

        public static Entry FromInstance(IContextMenuEntry instance)
        {
            var meta = instance.GetType().GetCustomAttribute<ExportContextMenuEntryAttribute>();
            if (meta is null)
            {
                // No attribute (should not happen for registered entries) — show the type name so
                // the omission is visible rather than silently dropping the entry.
                return new Entry { Instance = instance, Header = instance.GetType().Name, Order = double.MaxValue };
            }

            return new Entry
            {
                Instance = instance,
                Header = ResolveHeader(meta.Header),
                IconUri = ResolveIcon(meta.Icon),
                Category = meta.Category,
                Order = meta.Order,
                MenuID = meta.MenuID,
                ParentMenuID = meta.ParentMenuID,
            };
        }

        // Header metadata is a resource key (e.g. nameof(Resources._Remove)); resolve it, then
        // drop the leading WPF access-key mnemonic ('_Remove' -> 'Remove', '_Save Code...' ->
        // 'Save Code...') so the WinUI label reads cleanly.
        private static string ResolveHeader(string? key)
        {
            if (string.IsNullOrEmpty(key))
                return string.Empty;
            var text = ResolveResourceString(key) ?? key;
            var underscore = text.IndexOf('_');
            return underscore >= 0 ? text.Remove(underscore, 1) : text;
        }

        private static string? ResolveResourceString(string key)
            => GetResourceString(RomaResources.ResourceManager, key)
            ?? GetResourceString(ILSpyResources.ResourceManager, key);

        private static string? GetResourceString(System.Resources.ResourceManager resourceManager, string key)
        {
            var value = resourceManager.GetString(key);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static string? ResolveIcon(string? iconPath)
        {
            if (string.IsNullOrEmpty(iconPath))
                return null;
            return IconAssets.TryGetValue(iconPath, out var asset)
                ? $"ms-appx:///ILSpyIcons/{asset}"
                : null;
        }
    }
}
