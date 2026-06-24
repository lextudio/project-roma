# macOS Native Menu Plan

## Context

Roma currently renders its ILSpy-style menu with WinUI `MenuBar` inside the Uno Skia window. On
macOS this works, but it does not populate the system menu bar. Uno's Skia desktop host does not
currently expose a native menu wrapper for this scenario, so Roma needs a small AppKit bridge similar
to the existing `MacOSFileDrop` integration.

## Goals

- Install a macOS `NSMenu` when running under the Uno Skia `.UseMacOS()` host.
- Mirror the existing Roma File, View, Window, and Help menu commands.
- Use SF Symbol template images for native menu item icons so AppKit tints them correctly for the
  active light or dark system appearance.
- Add a persisted Roma setting that hides the in-window WinUI menu bar on macOS when users prefer the
  native menu only.
- Keep all native calls behind `OperatingSystem.IsMacOS()` guards so Windows and Linux behavior is
  unchanged.

## Implementation

1. Add a Roma-local `RomaHostSettings` settings section persisted through the existing ILSpy settings
   snapshot mechanism.
2. Add a macOS-only checkbox to Options -> Misc:
   `Hide in-app main menu on macOS`.
3. Add `MacOSNativeMenu`, an AppKit bridge that:
   - creates an `NSMenu` tree,
   - assigns `NSMenuItem` actions to a managed Objective-C target,
   - stores a string command id in each item's `representedObject`,
   - uses `NSImage imageWithSystemSymbolName:accessibilityDescription:` and `setTemplate:true` for
     appearance-aware icons.
4. Dispatch native command ids back onto `MainPage`'s UI dispatcher and reuse the same methods as the
   existing XAML menu.
5. Apply the hide-menu setting at startup and immediately after Options is saved.

## Notes

- The native menu intentionally mirrors the existing XAML menu instead of trying to introspect WinUI
  `MenuBar` items. That keeps AppKit interop simple and avoids depending on Uno internals.
- Theme matching for native menu icons is delegated to AppKit template images rather than manually
  switching light/dark assets.
