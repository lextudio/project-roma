# Project Roma

## Goal

Run ILSpy on Uno Platform, starting with the Skia desktop target, while
reusing as much upstream ILSpy code as possible.

## Product direction

Roma is not a rewrite of ILSpy. The bias is:

- keep ILSpy as the behavioral owner where possible
- keep ILSpy as the view-model and view owner where possible
- adapt host and UI contracts at the edges
- prefer linked upstream source over copy-pasted forks
- isolate Uno-specific bridges in Roma and shared infrastructure projects

The first target is a desktop-quality Skia application. Web, mobile, and
packaging concerns are follow-on work, not part of the bootstrap.

## Architecture principles

1. Upstream-first reuse
   - Add ILSpy as a submodule pinned to a known release, initially `v10.1`.
   - Link upstream `.cs` files into Roma projects where the code can compile
     with narrow shims.
   - Fork or patch only when there is a concrete platform incompatibility.

2. Thin platform layer
   - Keep Uno-specific bootstrapping, resource wiring, shell composition, and
     dispatcher integration in Roma.
   - Reuse existing local infrastructure where it already solves a problem:
     `WindowsShims`, `UnoDock`, `UnoEdit`, and related repos/tools.
   - Prefer hosting upstream ILSpy views and view models over recreating them
     in Roma when the required control surface is already available.

3. Session-based delivery
   - Each work session should end with:
     - a concrete runnable or measurable milestone
     - updated docs
     - an explicit next blocker

## System shape

At a high level, Roma will be split into these layers:

1. `Roma.Host`
   - Uno App bootstrap
   - application lifetime
   - window creation
   - theme and shell startup

2. `Roma.App`
   - ILSpy-driven application composition
   - document/workspace orchestration
   - service registration
   - top-level commands and menus

3. `Roma.Compatibility`
   - glue code for WPF-oriented ILSpy contracts
   - adapter types for dialogs, threading, clipboard, input, resources, and
     WPF control assumptions

4. Upstream-linked ILSpy projects
   - linked source from ILSpy where compile/runtime behavior is still owned by
     upstream code

5. Shared UI dependencies
   - `UnoDock` for docking/workspace layout
   - `UnoEdit` as the default upstream text/editor host target
   - `WindowsShims` for WPF contract coverage where relevant
   - `DataGrid` reuse for metadata and table-oriented upstream views

## UI reuse rule

When an upstream ILSpy view or view model can be hosted with:

- `UnoDock` for layout
- `UnoEdit` for editor/text surface
- `WindowsShims` for WPF API coverage
- existing `DataGrid` compatibility for tabular views

Roma should host that upstream piece directly.

Roma should only introduce new local UI when one of these is true:

- the upstream view depends on WPF-specific rendering primitives that are still
  missing
- the upstream composition assumes shell infrastructure that must first be
  bridged
- a temporary local surface is needed to validate an integration boundary

## First implementation target

The first meaningful milestone is not “all of ILSpy on Uno”. It is:

> A Uno Skia desktop app that starts, hosts an ILSpy-derived shell, and proves
> that upstream ILSpy code can be compiled and invoked inside the Roma host.

That milestone is enough to validate the repo structure and the reuse model.

## Known hard areas

- WPF-specific UI assumptions in ILSpy
- docking/layout translation
- text editor integration
- command routing and input gestures
- resource dictionaries, themes, and icons
- dispatcher/thread affinity assumptions
- file dialogs, drag/drop, clipboard, and shell integration

These should be attacked incrementally, not all at once.

## Delivery order

1. Bootstrap repository structure and submodule.
2. Create Uno Skia host projects.
3. Prove a minimal runnable app.
4. Link the smallest useful ILSpy project slice.
5. Establish compatibility shims only where the compile or runtime path fails.
6. Grow shell fidelity after the host can boot and render.

Current progress:

- sessions 1-2 completed steps 1-3
- session 3 completed the first pass of step 4 using
  `ICSharpCode.Decompiler`
- session 4 extended step 4 into `ICSharpCode.ILSpyX`
- session 5 reused `AssemblyListManager`, `AssemblyList`, and
  `LoadedAssembly` to prove upstream assembly loading and metadata access
- session 6 proved targeted C# type decompilation from a `LoadedAssembly`
  through upstream decompiler APIs
- session 7 added the first metadata-driven navigation state by enumerating
  top-level types and selecting the first user type for decompilation
- session 8 grouped discovered types by namespace and exposed a small
  candidate list for richer assembly-browser state
- session 9 introduced the first desktop shell skeleton backed by structured
  namespace and type data from the compatibility layer
- session 10 replaced the temporary center layout with a real `UnoDock`
  `DockingManager` project reference and live docking model
- session 12 linked the first upstream ILSpy app-state slice by reusing
  `SettingsService`, `SessionSettings`, and `LanguageSettings`
- session 13 extended that into upstream `LanguageService` and
  `MainWindowViewModel` creation with thin Roma host bridges
- session 14 reused the upstream cross-platform `ICSharpCode.ILSpyX.TreeView.
  SharpTreeNode` model to build a real assembly → namespace → type tree, hosted
  in a WinUI `TreeView` via an API-parity adapter (replacing the placeholder
  namespace list) — the first reuse of upstream UI-model code without WindowsShims
- session 15 wired tree selection to live decompilation: clicking a type node
  now calls the upstream `CSharpDecompiler` (held in `RomaAssemblyContext`,
  built once from `MetadataFile + IAssemblyResolver`) and updates the document
  pane `TextBlock` synchronously — full upstream decompiled C# on demand

Next direction:

- stop building Roma-owned placeholder panes
- start reusing upstream ILSpy view models and views directly
- make Roma primarily the Uno host and compatibility bridge for those views

Near-term target order:

1. host upstream `MainWindowViewModel` or the narrowest reusable slice of it
2. reuse upstream tree / metadata views where current compatibility already
   covers their controls
3. move decompiled text into an upstream editor-oriented view path backed by
   `UnoEdit`
4. only create Roma-local wrapper views where upstream XAML cannot yet load

## Non-goals for the first wave

- mobile targets
- browser target
- full docking parity
- full extension/plugin parity
- installer/distribution polish
- pixel-perfect WPF visual parity

## Working rule

When choosing between “port more behavior” and “reuse more upstream code,”
prefer the upstream path unless the adaptation cost is clearly higher than a
small local bridge.
