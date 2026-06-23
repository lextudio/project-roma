// macOS-only native file-drop support for the Uno Skia desktop (.UseMacOS) host.
//
// Uno 6.5.x's AppKit-based Skia macOS host does not yet implement NSDraggingDestination, so
// OS file drops from Finder never reach the XAML layer (no DragEnter/DragOver/Drop fire). This
// helper bridges that gap entirely from managed code using the Objective-C runtime:
//
//   1. Walk NSApplication → its window → contentView (the Uno Metal NSView).
//   2. registerForDraggedTypes: so AppKit offers file drags to the view.
//   3. Add the NSDraggingDestination callbacks (draggingEntered:/performDragOperation:/…) to the
//      view's ObjC class via class_addMethod, backed by [UnmanagedCallersOnly] function pointers.
//   4. On drop, read file URLs off the NSPasteboard and raise FilesDropped with their paths.
//
// No Uno internals or native fork required — only libobjc + AppKit, which exist on the macOS head.
// Every entry point is guarded by OperatingSystem.IsMacOS(); on Windows/Linux nothing is P/Invoked.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Roma.Host;

internal static unsafe class MacOSFileDrop
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    // NSDragOperationCopy — the operation we report for an acceptable file drag.
    private const nuint NSDragOperationCopy = 1;

    // Raised on the AppKit main thread with the dropped file paths. Set by the host before Install().
    internal static Action<IReadOnlyList<string>>? FilesDropped;

    private static bool _installed;

    // Installs the drop handler on the main window's content view. Safe to call repeatedly and on
    // non-macOS platforms (no-op). Call once the window exists (e.g. from Page.Loaded).
    internal static bool Install()
    {
        if (!OperatingSystem.IsMacOS() || _installed)
            return _installed;

        var view = GetMainContentView();
        if (view == IntPtr.Zero)
            return false;

        // Tell AppKit this view accepts file-URL drags (modern UTI + legacy filenames type).
        var types = objc_msgSend(Cls("NSMutableArray"), Sel("array"));
        objc_msgSend(types, Sel("addObject:"), NSString("public.file-url"));
        objc_msgSend(types, Sel("addObject:"), NSString("NSFilenamesPboardType"));
        objc_msgSend(view, Sel("registerForDraggedTypes:"), types);

        // Graft the NSDraggingDestination methods onto the view's (dynamic) ObjC class. One window =
        // one instance, so replacing on the class is fine. class_addMethod returns false if the
        // selector already exists, in which case replace the IMP.
        var cls = object_getClass(view);
        AddMethod(cls, "draggingEntered:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, nuint>)&DraggingEntered, "Q@:@");
        AddMethod(cls, "draggingUpdated:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, nuint>)&DraggingEntered, "Q@:@");
        AddMethod(cls, "prepareForDragOperation:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)&PrepareForDragOperation, "B@:@");
        AddMethod(cls, "performDragOperation:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)&PerformDragOperation, "B@:@");

        _installed = true;
        return true;
    }

    // NSApplication.sharedApplication → first window → contentView.
    private static IntPtr GetMainContentView()
    {
        var app = objc_msgSend(Cls("NSApplication"), Sel("sharedApplication"));
        if (app == IntPtr.Zero)
            return IntPtr.Zero;

        // Prefer the key/main window; fall back to windows[0].
        var window = objc_msgSend(app, Sel("mainWindow"));
        if (window == IntPtr.Zero)
            window = objc_msgSend(app, Sel("keyWindow"));
        if (window == IntPtr.Zero)
        {
            var windows = objc_msgSend(app, Sel("windows"));
            if (windows != IntPtr.Zero && (nuint)objc_msgSend(windows, Sel("count")) > 0)
                window = objc_msgSend_idx(windows, Sel("objectAtIndex:"), 0);
        }
        return window == IntPtr.Zero ? IntPtr.Zero : objc_msgSend(window, Sel("contentView"));
    }

    // ── NSDraggingDestination callbacks (run on the AppKit main thread) ──────────────────────

    [UnmanagedCallersOnly]
    private static nuint DraggingEntered(IntPtr self, IntPtr cmd, IntPtr sender) => NSDragOperationCopy;

    [UnmanagedCallersOnly]
    private static byte PrepareForDragOperation(IntPtr self, IntPtr cmd, IntPtr sender) => 1; // YES

    [UnmanagedCallersOnly]
    private static byte PerformDragOperation(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try
        {
            var paths = ReadFilePaths(sender);
            if (paths.Count > 0)
                FilesDropped?.Invoke(paths);
            return 1; // YES
        }
        catch
        {
            return 0; // NO — let AppKit treat the drop as unhandled rather than crash the host
        }
    }

    // Reads file-system paths from the drag's NSPasteboard via readObjectsForClasses:[NSURL]:.
    private static List<string> ReadFilePaths(IntPtr sender)
    {
        var result = new List<string>();
        var pasteboard = objc_msgSend(sender, Sel("draggingPasteboard"));
        if (pasteboard == IntPtr.Zero)
            return result;

        var classes = objc_msgSend(Cls("NSMutableArray"), Sel("array"));
        objc_msgSend(classes, Sel("addObject:"), Cls("NSURL"));

        var urls = objc_msgSend_2(pasteboard, Sel("readObjectsForClasses:options:"), classes, IntPtr.Zero);
        if (urls == IntPtr.Zero)
            return result;

        var count = (nuint)objc_msgSend(urls, Sel("count"));
        for (nuint i = 0; i < count; i++)
        {
            var url = objc_msgSend_idx(urls, Sel("objectAtIndex:"), i);
            if (url == IntPtr.Zero)
                continue;
            // Only accept file URLs (skip web links etc.).
            if (((long)objc_msgSend(url, Sel("isFileURL")) & 0xFF) == 0)
                continue;
            var pathObj = objc_msgSend(url, Sel("path"));
            if (pathObj == IntPtr.Zero)
                continue;
            var utf8 = objc_msgSend(pathObj, Sel("UTF8String"));
            var path = Marshal.PtrToStringUTF8(utf8);
            if (!string.IsNullOrEmpty(path))
                result.Add(path);
        }
        return result;
    }

    // ── ObjC runtime helpers ─────────────────────────────────────────────────────────────────

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

    private static void AddMethod(IntPtr cls, string selector, IntPtr imp, string typeEncoding)
    {
        var sel = Sel(selector);
        if (!class_addMethod(cls, sel, imp, typeEncoding))
            class_replaceMethod(cls, sel, imp, typeEncoding);
    }

    // libobjc imports. objc_msgSend is re-declared per argument shape because the calling convention
    // requires the managed signature to match the native one (args passed in registers on arm64).

    [DllImport(LibObjC)] private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(LibObjC)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(LibObjC)] private static extern IntPtr object_getClass(IntPtr obj);

    [DllImport(LibObjC)]
    private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, [MarshalAs(UnmanagedType.LPStr)] string types);
    [DllImport(LibObjC)]
    private static extern bool class_replaceMethod(IntPtr cls, IntPtr sel, IntPtr imp, [MarshalAs(UnmanagedType.LPStr)] string types);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_2(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_idx(IntPtr receiver, IntPtr selector, nuint index);
}
