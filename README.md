# Project Roma

Roma is a cross-platform .NET assembly browser and decompiler built on [ILSpy](https://github.com/icsharpcode/ILSpy) and [Uno Platform](https://platform.uno). It runs natively on Windows and macOS using the Skia renderer.

## Features

- Browse and decompile .NET assemblies (C#, IL, metadata)
- Cross-platform desktop app — Windows x64/ARM64 and macOS universal
- Full ILSpy decompiler engine (ICSharpCode.Decompiler + ICSharpCode.ILSpyX)
- Session restore: returns to the last viewed type/member on restart
- Back/Forward navigation history
- Click-to-navigate hyperlinks in decompiled code
- Six editor themes (Light, Dark, VS Code Light+, VS Code Dark+, ReSharper Light, ReSharper Dark)
- Assembly list management with presets

## Building

Prerequisites: .NET 10 SDK, Uno Platform SDK.

```bash
dotnet build Roma/src/Roma.Host/Roma.Host.csproj
```

## Packaging

CI packaging is triggered manually via **Actions → Package** (`.github/workflows/package.yml`). It produces:

- `Roma-windows-x64` — Windows x64 publish directory
- `Roma-windows-arm64` — Windows ARM64 publish directory
- `Roma-macos-universal-dmg` — macOS universal `.dmg`

Local macOS packaging:

```bash
dotnet publish Roma/src/Roma.Host/Roma.Host.csproj -r osx-arm64 -c Release
dotnet publish Roma/src/Roma.Host/Roma.Host.csproj -r osx-x64 -c Release
Roma/build/macos/build-application-bundle.sh osx-universal
Roma/build/macos/build-dmg.sh Roma.app Roma-macos-universal.dmg
```

## Architecture

Roma links ILSpy source files directly (no fork) via `<Compile ... Link="..." />` entries in the csproj. Platform-specific seams are guarded with `#if ROMA_UNO`. The Uno Platform Skia renderer replaces WPF's drawing surface; WPF shims (`LeXtudio.Windows`) bridge the remaining WPF API surface.

## License

MIT — see [LICENSE](LICENSE).

ILSpy is MIT licensed. See [Roma/ext/ilspy/LICENSE](ext/ilspy/LICENSE).
