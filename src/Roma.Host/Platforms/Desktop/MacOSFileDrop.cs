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

    // Apple Event four-char codes (FourCC, big-endian) for the open-documents event we intercept.
    private const uint kCoreEventClass = 0x61657674;  // 'aevt'
    private const uint kAEOpenDocuments = 0x6F646F63; // 'odoc'
    private const uint keyDirectObject = 0x2D2D2D2D;  // '----'
    private const uint typeFileURL = 0x6675726C;      // 'furl'

    // Sink for received file paths (drag-drop and "Open With"). Registered by the host once the
    // assembly context is ready; paths that arrive earlier (e.g. a cold "Open With" launch) are
    // buffered and flushed on registration.
    private static Action<IReadOnlyList<string>>? _handler;
    private static readonly List<string> _pending = new();
    private static readonly object _gate = new();

    private static bool _installed;
    private static bool _openWithInstalled;

    // Registers the file-path sink and flushes anything buffered before now. Call on the UI thread.
    internal static void RegisterHandler(Action<IReadOnlyList<string>> handler)
    {
        List<string>? flush = null;
        lock (_gate)
        {
            _handler = handler;
            if (_pending.Count > 0)
            {
                flush = new List<string>(_pending);
                _pending.Clear();
            }
        }
        if (flush is not null)
            handler(flush);
    }

    // Routes received paths to the handler, or buffers them until one is registered.
    private static void Deliver(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;
        Action<IReadOnlyList<string>>? handler;
        lock (_gate)
        {
            if (_handler is null)
            {
                _pending.AddRange(paths);
                Log($"Deliver: buffered {paths.Count} path(s) (no handler yet)");
                return;
            }
            handler = _handler;
        }
        handler(paths);
    }

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

    // Installs the "Open With Roma" (Finder → open document) handler by adding application:openURLs:
    // to the NSApplication delegate's class. Uno's macOS host doesn't forward this event, so without
    // it AppKit reports "cannot open files in the … format". Install early (App.OnLaunched) so a cold
    // launch's open event is caught; paths buffer until the host registers its handler.
    private static IntPtr _delegate;

    internal static bool InstallOpenWith()
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        var app = objc_msgSend(Cls("NSApplication"), Sel("sharedApplication"));
        if (app == IntPtr.Zero)
        {
            Log("InstallOpenWith: NSApplication is null");
            return false;
        }
        var del = objc_msgSend(app, Sel("delegate"));
        if (del == IntPtr.Zero)
        {
            Log("InstallOpenWith: NSApp.delegate is null (no delegate to hook yet)");
            return false;
        }

        if (!_openWithInstalled)
        {
            var cls = object_getClass(del);
            var clsName = Marshal.PtrToStringUTF8(object_getClassName(del));
            // Graft the handler selector (and, belt-and-suspenders, the delegate open methods) once.
            AddMethod(cls, "romaHandleOpenDocuments:withReplyEvent:",
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&HandleOpenDocsEvent, "v@:@@");
            AddMethod(cls, "application:openURLs:",
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&OpenURLs, "v@:@@");
            AddMethod(cls, "application:openFile:",
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, byte>)&OpenFile, "B@:@@");
            AddMethod(cls, "application:openFiles:",
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&OpenFiles, "v@:@@");
            _delegate = del;
            _openWithInstalled = true;
            Log($"InstallOpenWith: delegate class='{clsName}', handler methods grafted");
        }

        RegisterOpenDocumentsHandler();
        return true;
    }

    // (Re)registers our 'odoc' (kAEOpenDocuments) Apple Event handler on the shared NSAppleEventManager.
    // Must be called AFTER AppKit/Uno finish launching: NSApplication installs its own default odoc
    // handler during finishLaunching (the one that shows "cannot open files in the … format"), which
    // overwrites an earlier registration. Calling this again from MainPage.Loaded wins the race.
    // Intercepting the Apple Event directly also sidesteps the NSApplicationDelegate, which Uno's host
    // doesn't route open-document events through.
    internal static void RegisterOpenDocumentsHandler()
    {
        if (!OperatingSystem.IsMacOS() || _delegate == IntPtr.Zero)
            return;
        SetOdocHandler(_delegate);
        Log("odoc AppleEvent handler registered");
    }

    // Sets our 'odoc' handler on the shared NSAppleEventManager with the given target object. The
    // target's class must implement romaHandleOpenDocuments:withReplyEvent:.
    private static void SetOdocHandler(IntPtr target)
    {
        var manager = objc_msgSend(Cls("NSAppleEventManager"), Sel("sharedAppleEventManager"));
        objc_msgSend_setHandler(manager, Sel("setEventHandler:andSelector:forEventClass:andEventID:"),
            target, Sel("romaHandleOpenDocuments:withReplyEvent:"), kCoreEventClass, kAEOpenDocuments);
    }

    private static bool _earlyInstalled;
    private static IntPtr _earlyTarget;

    // Earliest possible install (call from Program.Main, before the Uno host starts the NSApp run
    // loop). A cold "Open With" launch delivers the odoc event during AppKit's finishLaunching —
    // BEFORE OnLaunched/Loaded run — so registering there is too late (AppKit's default handler shows
    // "cannot open files in the … format" first). Instead, observe NSApplicationWillFinishLaunching,
    // which fires just before AppKit processes the launch event, and claim odoc then. Uses a dedicated
    // NSObject target (the delegate doesn't exist this early). Buffered paths flush once MainPage
    // registers its handler.
    internal static void InstallEarly()
    {
        if (!OperatingSystem.IsMacOS() || _earlyInstalled)
            return;
        _earlyInstalled = true;

        // Define a dedicated target class once (objc_allocateClassPair returns null if it exists).
        const string className = "RomaAppleEventTarget";
        var cls = objc_allocateClassPair(Cls("NSObject"), className, 0);
        if (cls != IntPtr.Zero)
        {
            AddMethod(cls, "romaHandleOpenDocuments:withReplyEvent:",
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&HandleOpenDocsEvent, "v@:@@");
            AddMethod(cls, "romaWillFinishLaunching:",
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&WillFinishLaunching, "v@:@");
            objc_registerClassPair(cls);
        }
        else
        {
            cls = Cls(className);
        }

        _earlyTarget = objc_msgSend(objc_msgSend(cls, Sel("alloc")), Sel("init"));

        // Observe willFinishLaunching. The constant's string value IS its own name, so an NSString
        // literal matches without dlsym'ing the AppKit symbol.
        var center = objc_msgSend(Cls("NSNotificationCenter"), Sel("defaultCenter"));
        objc_msgSend_addObserver(center, Sel("addObserver:selector:name:object:"),
            _earlyTarget, Sel("romaWillFinishLaunching:"),
            NSString("NSApplicationWillFinishLaunchingNotification"), IntPtr.Zero);
        Log("InstallEarly: observing NSApplicationWillFinishLaunching");
    }

    // Fires during AppKit's finishLaunching, before the launch odoc event is processed. self is our
    // dedicated target; claim the odoc handler now so we beat AppKit's default.
    [UnmanagedCallersOnly]
    private static void WillFinishLaunching(IntPtr self, IntPtr cmd, IntPtr notification)
    {
        try
        {
            SetOdocHandler(self);
            Log("willFinishLaunching: odoc handler claimed early");
        }
        catch (Exception ex)
        {
            Log($"willFinishLaunching threw {ex.GetType().Name}: {ex.Message}");
        }
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
            var pasteboard = objc_msgSend(sender, Sel("draggingPasteboard"));
            if (pasteboard != IntPtr.Zero)
            {
                var classes = objc_msgSend(Cls("NSMutableArray"), Sel("array"));
                objc_msgSend(classes, Sel("addObject:"), Cls("NSURL"));
                var urls = objc_msgSend_2(pasteboard, Sel("readObjectsForClasses:options:"), classes, IntPtr.Zero);
                Deliver(PathsFromUrlArray(urls));
            }
            return 1; // YES
        }
        catch
        {
            return 0; // NO — let AppKit treat the drop as unhandled rather than crash the host
        }
    }

    // application:openURLs: — Finder "Open With Roma" (and re-opens while running). urls is an
    // NSArray<NSURL*> of the documents to open.
    [UnmanagedCallersOnly]
    private static void OpenURLs(IntPtr self, IntPtr cmd, IntPtr app, IntPtr urls)
    {
        try
        {
            var paths = PathsFromUrlArray(urls);
            Log($"openURLs: fired, {paths.Count} file path(s): {string.Join(", ", paths)}");
            Deliver(paths);
        }
        catch (Exception ex)
        {
            Log($"openURLs: threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // application:openFile: — legacy single-file open (filename is an NSString path).
    [UnmanagedCallersOnly]
    private static byte OpenFile(IntPtr self, IntPtr cmd, IntPtr sender, IntPtr filename)
    {
        try
        {
            var path = NSStringToManaged(filename);
            Log($"openFile: fired, path='{path}'");
            if (!string.IsNullOrEmpty(path))
                Deliver(new[] { path! });
            return 1; // YES — handled
        }
        catch (Exception ex)
        {
            Log($"openFile: threw {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    // application:openFiles: — legacy multi-file open (filenames is an NSArray<NSString*> of paths).
    [UnmanagedCallersOnly]
    private static void OpenFiles(IntPtr self, IntPtr cmd, IntPtr sender, IntPtr filenames)
    {
        try
        {
            var paths = new List<string>();
            if (filenames != IntPtr.Zero)
            {
                var count = (nuint)objc_msgSend(filenames, Sel("count"));
                for (nuint i = 0; i < count; i++)
                {
                    var path = NSStringToManaged(objc_msgSend_idx(filenames, Sel("objectAtIndex:"), i));
                    if (!string.IsNullOrEmpty(path))
                        paths.Add(path!);
                }
            }
            Log($"openFiles: fired, {paths.Count} path(s): {string.Join(", ", paths)}");
            Deliver(paths);
        }
        catch (Exception ex)
        {
            Log($"openFiles: threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? NSStringToManaged(IntPtr nsString)
        => nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(objc_msgSend(nsString, Sel("UTF8String")));

    // 'odoc' Apple Event handler: -(void)handler:(NSAppleEventDescriptor*)event withReplyEvent:(…).
    // The direct object is an AEDescList of file references; coerce each to a file URL and read it.
    [UnmanagedCallersOnly]
    private static void HandleOpenDocsEvent(IntPtr self, IntPtr cmd, IntPtr evt, IntPtr reply)
    {
        try
        {
            var paths = new List<string>();
            var direct = objc_msgSend_u(evt, Sel("paramDescriptorForKeyword:"), keyDirectObject);
            if (direct != IntPtr.Zero)
            {
                var count = (long)objc_msgSend(direct, Sel("numberOfItems"));
                if (count <= 0)
                {
                    AddPathFromDescriptor(direct, paths); // single descriptor, not a list
                }
                else
                {
                    for (long i = 1; i <= count; i++) // AEDescList is 1-based
                        AddPathFromDescriptor(objc_msgSend_n(direct, Sel("descriptorAtIndex:"), (nint)i), paths);
                }
            }
            Log($"odoc AppleEvent fired, {paths.Count} path(s): {string.Join(", ", paths)}");
            Deliver(paths);
        }
        catch (Exception ex)
        {
            Log($"odoc AppleEvent threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Coerces an NSAppleEventDescriptor to a file URL and appends its local path.
    private static void AddPathFromDescriptor(IntPtr desc, List<string> paths)
    {
        if (desc == IntPtr.Zero)
            return;
        var urlDesc = objc_msgSend_u(desc, Sel("coerceToDescriptorType:"), typeFileURL);
        if (urlDesc == IntPtr.Zero)
            return;
        var data = objc_msgSend(urlDesc, Sel("data"));
        if (data == IntPtr.Zero)
            return;
        var bytes = objc_msgSend(data, Sel("bytes"));
        var len = (long)objc_msgSend(data, Sel("length"));
        if (bytes == IntPtr.Zero || len <= 0)
            return;
        var urlStr = Marshal.PtrToStringUTF8(bytes, (int)len);
        if (string.IsNullOrEmpty(urlStr))
            return;
        try
        {
            var path = new Uri(urlStr).LocalPath;
            if (!string.IsNullOrEmpty(path))
                paths.Add(path);
        }
        catch { /* not a parseable file URL */ }
    }

    // Extracts file-system paths from an NSArray<NSURL*>, skipping non-file URLs.
    private static List<string> PathsFromUrlArray(IntPtr urls)
    {
        var result = new List<string>();
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

    // Returns true if the method was newly added, false if it already existed and was replaced.
    private static bool AddMethod(IntPtr cls, string selector, IntPtr imp, string typeEncoding)
    {
        var sel = Sel(selector);
        if (class_addMethod(cls, sel, imp, typeEncoding))
            return true;
        class_replaceMethod(cls, sel, imp, typeEncoding);
        return false;
    }

    // Lightweight append logger to the shared debug log (same file MainPage.Dbg uses), so the native
    // path is diagnosable. Best-effort; never throws.
    private static void Log(string message)
    {
        try
        {
            System.IO.File.AppendAllText("/tmp/roma-debug.log",
                $"[{DateTime.Now:HH:mm:ss.fff}] MacOSFileDrop: {message}\n");
        }
        catch { /* ignore */ }
    }

    // libobjc imports. objc_msgSend is re-declared per argument shape because the calling convention
    // requires the managed signature to match the native one (args passed in registers on arm64).

    [DllImport(LibObjC)] private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(LibObjC)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(LibObjC)] private static extern IntPtr object_getClass(IntPtr obj);
    [DllImport(LibObjC)] private static extern IntPtr object_getClassName(IntPtr obj);

    [DllImport(LibObjC)]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, [MarshalAs(UnmanagedType.LPStr)] string name, nuint extraBytes);
    [DllImport(LibObjC)]
    private static extern void objc_registerClassPair(IntPtr cls);

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
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_u(IntPtr receiver, IntPtr selector, uint arg);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_n(IntPtr receiver, IntPtr selector, nint arg);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_setHandler(IntPtr receiver, IntPtr selector,
        IntPtr handler, IntPtr handlerSelector, uint eventClass, uint eventId);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_addObserver(IntPtr receiver, IntPtr selector,
        IntPtr observer, IntPtr observerSelector, IntPtr name, IntPtr obj);
}
