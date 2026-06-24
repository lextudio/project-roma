// macOS native main-menu support for the Uno Skia desktop host.
//
// Builds an AppKit NSMenu from Roma's existing menu commands. SF Symbol template images are used
// for menu icons, so AppKit tints them automatically for the current light/dark appearance.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Roma.Host;

internal static unsafe class MacOSNativeMenu
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    private static bool _installed;
    private static IntPtr _target;
    private static Action<string>? _handler;
    private static NativeMenuState _state = NativeMenuState.Empty;

    internal sealed record NativeMenuChoice(string Title, string Command, bool IsChecked, string SymbolName);

    internal sealed record NativeMenuState(
        IReadOnlyList<NativeMenuChoice> ApiVisibilityItems,
        IReadOnlyList<NativeMenuChoice> ThemeItems,
        IReadOnlyList<NativeMenuChoice> UiLanguageItems,
        IReadOnlyList<NativeMenuChoice> DecompilerLanguageItems,
        IReadOnlyList<NativeMenuChoice> DecompilerLanguageVersionItems)
    {
        public static NativeMenuState Empty { get; } = new([], [], [], [], []);
    }

    internal static bool Install(Action<string> handler, NativeMenuState state)
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        _handler = handler;
        _state = state;
        EnsureTarget();
        Rebuild();
        _installed = true;
        return true;
    }

    internal static void Update(NativeMenuState state)
    {
        if (!OperatingSystem.IsMacOS() || !_installed)
            return;
        _state = state;
        Rebuild();
    }

    private static void Rebuild()
    {
        var mainMenu = objc_msgSend(objc_msgSend(Cls("NSMenu"), Sel("alloc")), Sel("initWithTitle:"), NSString("MainMenu"));

        AddAppMenu(mainMenu);
        AddFileMenu(mainMenu);
        AddViewMenu(mainMenu);
        AddWindowMenu(mainMenu);
        AddHelpMenu(mainMenu);

        var app = objc_msgSend(Cls("NSApplication"), Sel("sharedApplication"));
        objc_msgSend(app, Sel("setMainMenu:"), mainMenu);
        var firstItem = objc_msgSend_idx(mainMenu, Sel("itemAtIndex:"), 0);
        if (firstItem != IntPtr.Zero)
            objc_msgSend(firstItem, Sel("setTitle:"), NSString("Roma"));
    }

    private static void AddAppMenu(IntPtr mainMenu)
    {
        var appMenu = NewMenu("Roma");
        AddSubmenu(mainMenu, "Roma", appMenu);

        AddAction(appMenu, "About Roma", "help.about", string.Empty, "info.circle");
        AddSeparator(appMenu);
        AddAction(appMenu, "Preferences...", "view.options", ",", "gearshape");
        AddSeparator(appMenu);
        AddSystemItem(appMenu, "Hide Roma", "hide:", "h");
        AddSystemItem(appMenu, "Hide Others", "hideOtherApplications:", "h", nseventModifierFlagOption | nseventModifierFlagCommand);
        AddSystemItem(appMenu, "Show All", "unhideAllApplications:", string.Empty);
        AddSeparator(appMenu);
        AddAction(appMenu, "Quit Roma", "file.exit", "q", "power");
    }

    private static void AddFileMenu(IntPtr mainMenu)
    {
        var menu = NewMenu("File");
        AddSubmenu(mainMenu, "File", menu);
        AddAction(menu, "Open...", "file.open", "o", "folder");
        AddAction(menu, "Open from GAC...", "file.openGac", string.Empty, "shippingbox");
        AddAction(menu, "Manage Assembly Lists...", "file.manageAssemblyLists", string.Empty, "list.bullet.rectangle");
        AddAction(menu, "Reload All", "file.reloadAll", "r", "arrow.clockwise");
        AddSeparator(menu);
        AddAction(menu, "Save Code...", "file.saveCode", "s", "square.and.arrow.down");
        AddSeparator(menu);
        AddAction(menu, "Remove Assemblies with Load Errors", "file.removeErrors", string.Empty, "exclamationmark.triangle");
        AddAction(menu, "Clear Assembly List", "file.clearAssemblyList", string.Empty, "trash");
    }

    private static void AddViewMenu(IntPtr mainMenu)
    {
        var menu = NewMenu("View");
        AddSubmenu(mainMenu, "View", menu);

        AddChoices(menu, _state.ApiVisibilityItems);
        AddSeparator(menu);
        AddChoiceSubmenu(menu, "Theme", _state.ThemeItems);
        AddChoiceSubmenu(menu, "UI Language", _state.UiLanguageItems);
        AddSeparator(menu);
        AddAction(menu, "Sort Assembly List by Name", "view.sortAssemblyList", string.Empty, "arrow.up.arrow.down");
        AddAction(menu, "Collapse Tree Nodes", "view.collapseAll", string.Empty, "rectangle.compress.vertical");
        AddSeparator(menu);
        AddAction(menu, "Options...", "view.options", ",", "gearshape");
    }

    private static void AddWindowMenu(IntPtr mainMenu)
    {
        var menu = NewMenu("Window");
        AddSubmenu(mainMenu, "Window", menu);
        AddAction(menu, "Close All Documents", "window.closeAllDocuments", string.Empty, "xmark.rectangle");
        AddAction(menu, "Reset Layout", "window.resetLayout", string.Empty, "rectangle.3.group");
        AddSeparator(menu);
        AddAction(menu, "Assemblies", "window.assemblies", string.Empty, "sidebar.left");
        AddAction(menu, "Search", "window.search", "f", "magnifyingglass");
        AddAction(menu, "Analyze", "window.analyze", string.Empty, "waveform.path.ecg");
        AddAction(menu, "Debug Steps", "window.debugSteps", string.Empty, "ladybug");
        AddSeparator(menu);
        AddAction(menu, "Code", "window.code", string.Empty, "curlybraces");
    }

    private static void AddHelpMenu(IntPtr mainMenu)
    {
        var menu = NewMenu("Help");
        AddSubmenu(mainMenu, "Help", menu);
        AddAction(menu, "Check for Updates...", "help.checkUpdates", string.Empty, "arrow.down.circle");
    }

    private static IntPtr NewMenu(string title)
        => objc_msgSend(objc_msgSend(Cls("NSMenu"), Sel("alloc")), Sel("initWithTitle:"), NSString(title));

    private static IntPtr AddSubmenu(IntPtr parent, string title, IntPtr submenu)
    {
        var item = objc_msgSend(parent, Sel("addItemWithTitle:action:keyEquivalent:"), NSString(title), IntPtr.Zero, NSString(string.Empty));
        objc_msgSend(parent, Sel("setSubmenu:forItem:"), submenu, item);
        return item;
    }

    private static IntPtr AddAction(IntPtr menu, string title, string command, string key, string symbolName)
    {
        var item = objc_msgSend(menu, Sel("addItemWithTitle:action:keyEquivalent:"), NSString(title), Sel("romaNativeMenuItem:"), NSString(key));
        objc_msgSend(item, Sel("setTarget:"), _target);
        objc_msgSend(item, Sel("setRepresentedObject:"), NSString(command));
        TrySetSymbolImage(item, symbolName, title);
        return item;
    }

    private static void AddChoices(IntPtr menu, IReadOnlyList<NativeMenuChoice> choices)
    {
        foreach (var choice in choices)
            AddChoice(menu, choice);
    }

    private static void AddChoiceSubmenu(IntPtr menu, string title, IReadOnlyList<NativeMenuChoice> choices)
    {
        var submenu = NewMenu(title);
        AddSubmenu(menu, title, submenu);
        AddChoices(submenu, choices);
    }

    private static void AddChoice(IntPtr menu, NativeMenuChoice choice)
    {
        var item = AddAction(menu, choice.Title, choice.Command, string.Empty, choice.SymbolName);
        objc_msgSend(item, Sel("setState:"), choice.IsChecked ? 1 : 0);
    }

    private static void AddSystemItem(IntPtr menu, string title, string selector, string key, nuint modifiers = nseventModifierFlagCommand)
    {
        var app = objc_msgSend(Cls("NSApplication"), Sel("sharedApplication"));
        var item = objc_msgSend(menu, Sel("addItemWithTitle:action:keyEquivalent:"), NSString(title), Sel(selector), NSString(key));
        objc_msgSend(item, Sel("setTarget:"), app);
        if (modifiers != nseventModifierFlagCommand)
            objc_msgSend(item, Sel("setKeyEquivalentModifierMask:"), modifiers);
    }

    private static void AddSeparator(IntPtr menu)
    {
        var separator = objc_msgSend(Cls("NSMenuItem"), Sel("separatorItem"));
        objc_msgSend(menu, Sel("addItem:"), separator);
    }

    private static void TrySetSymbolImage(IntPtr item, string symbolName, string description)
    {
        var image = objc_msgSend(Cls("NSImage"), Sel("imageWithSystemSymbolName:accessibilityDescription:"),
            NSString(symbolName), NSString(description));
        if (image == IntPtr.Zero)
        {
            Log($"SF Symbol image not found: {symbolName}");
            return;
        }
        objc_msgSend(image, Sel("setTemplate:"), true);
        objc_msgSend(image, Sel("setSize:"), new NSSize(16, 16));
        objc_msgSend(item, Sel("setImage:"), image);
    }

    private static void EnsureTarget()
    {
        if (_target != IntPtr.Zero)
            return;

        const string className = "RomaNativeMenuTarget";
        var cls = objc_allocateClassPair(Cls("NSObject"), className, 0);
        if (cls != IntPtr.Zero)
        {
            AddMethod(cls, "romaNativeMenuItem:",
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&HandleMenuItem, "v@:@");
            objc_registerClassPair(cls);
        }
        else
        {
            cls = Cls(className);
        }

        _target = objc_msgSend(objc_msgSend(cls, Sel("alloc")), Sel("init"));
    }

    [UnmanagedCallersOnly]
    private static void HandleMenuItem(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try
        {
            var commandObj = objc_msgSend(sender, Sel("representedObject"));
            var utf8 = objc_msgSend(commandObj, Sel("UTF8String"));
            var command = Marshal.PtrToStringUTF8(utf8);
            if (!string.IsNullOrEmpty(command))
                _handler?.Invoke(command);
        }
        catch (Exception ex)
        {
            Log($"HandleMenuItem threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IntPtr Cls(string name) => objc_getClass(name);
    private static IntPtr Sel(string name) => sel_registerName(name);

    private static IntPtr NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return objc_msgSend(Cls("NSString"), Sel("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private static bool AddMethod(IntPtr cls, string selector, IntPtr imp, string typeEncoding)
    {
        var sel = Sel(selector);
        if (class_addMethod(cls, sel, imp, typeEncoding))
            return true;
        class_replaceMethod(cls, sel, imp, typeEncoding);
        return false;
    }

    private static void Log(string message)
    {
        try
        {
            System.IO.File.AppendAllText("/tmp/roma-debug.log",
                $"[{DateTime.Now:HH:mm:ss.fff}] MacOSNativeMenu: {message}\n");
        }
        catch { }
    }

    private const nuint nseventModifierFlagOption = (nuint)(1UL << 19);
    private const nuint nseventModifierFlagCommand = (nuint)(1UL << 20);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NSSize(double width, double height)
    {
        private readonly double Width = width;
        private readonly double Height = height;
    }

    [DllImport(LibObjC)] private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(LibObjC)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(LibObjC)]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, [MarshalAs(UnmanagedType.LPStr)] string name, nuint extraBytes);
    [DllImport(LibObjC)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(LibObjC)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, [MarshalAs(UnmanagedType.LPStr)] string types);
    [DllImport(LibObjC)] private static extern bool class_replaceMethod(IntPtr cls, IntPtr sel, IntPtr imp, [MarshalAs(UnmanagedType.LPStr)] string types);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2, IntPtr arg3);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_idx(IntPtr receiver, IntPtr selector, nint index);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, bool arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, NSSize arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, int arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, nuint arg1);
}
