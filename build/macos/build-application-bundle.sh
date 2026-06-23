#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <rid|osx-universal>"
  exit 1
fi

rid="$1"
base_dir="src/Roma.Host/bin/Release/net10.0-desktop"
bundle_root="Roma.app"
bundle_macos="$bundle_root/Contents/MacOS"
script_dir="$(cd "$(dirname "$0")" && pwd)"

rm -rf "$bundle_root"
mkdir -p "$bundle_root/Contents/Resources" "$bundle_macos"
cp "$script_dir/Info.plist" "$bundle_root/Contents"
if [[ -f "$script_dir/roma.icns" ]]; then
  cp "$script_dir/roma.icns" "$bundle_root/Contents/Resources"
fi

is_macho() {
  local path="$1"
  file -b "$path" 2>/dev/null | grep -q "Mach-O"
}

# Uno's macOS Skia runtime, when it detects it is running inside a .app bundle, resolves
# ms-appx:/// assets from "{executable dir}/Resources" (Package.Current.InstalledPath +
# "Resources"). Since our payload (executable + assets) lives in Contents/MacOS, that means
# the runtime looks under Contents/MacOS/Resources. Without this, embedded fonts (the Fluent
# symbol font behind tree/combo/menu glyphs) and other ms-appx assets fail to load and render
# as missing-glyph "[?]" boxes. Mirror the asset content into Contents/MacOS/Resources.
# (Code/debug artifacts are flat at the payload root; every top-level directory is an asset dir.)
populate_resources() {
  local macos="$1"
  local res="$macos/Resources"
  mkdir -p "$res"

  local d name
  for d in "$macos"/*/; do
    [[ -d "$d" ]] || continue
    d="${d%/}"  # strip trailing slash so cp copies the directory, not its contents
    name="$(basename "$d")"
    [[ "$name" == "Resources" ]] && continue
    cp -Rp "$d" "$res/"
  done

  # Loose asset files at the payload root (splash screens, font markers, images).
  find "$macos" -maxdepth 1 -type f \( \
    -iname "*.png" -o -iname "*.jpg" -o -iname "*.jpeg" -o -iname "*.gif" \
    -o -iname "*.svg" -o -iname "*.ttf" -o -iname "*.otf" -o -iname "*.ico" \
    -o -iname "*.webp" -o -iname "*.uprimarker" \) \
    -exec cp -p {} "$res/" \;
}

if [[ "$rid" != "osx-universal" ]]; then
  src="$base_dir/$rid/publish"
  if [[ ! -d "$src" ]]; then
    echo "Publish directory not found: $src"
    exit 1
  fi
  cp -Rp "$src"/. "$bundle_macos/"
  populate_resources "$bundle_macos"
  exit 0
fi

arm_src="$base_dir/osx-arm64/publish"
x64_src="$base_dir/osx-x64/publish"

if [[ ! -d "$arm_src" ]]; then
  echo "Publish directory not found: $arm_src"
  exit 1
fi
if [[ ! -d "$x64_src" ]]; then
  echo "Publish directory not found: $x64_src"
  exit 1
fi

# Use arm64 publish as base payload for the .app, then merge native binaries with x64.
cp -Rp "$arm_src"/. "$bundle_macos/"

while IFS= read -r -d '' arm_file; do
  rel="${arm_file#$arm_src/}"
  x64_file="$x64_src/$rel"
  dest_file="$bundle_macos/$rel"

  [[ -f "$x64_file" ]] || continue
  if ! is_macho "$arm_file"; then
    continue
  fi
  if ! is_macho "$x64_file"; then
    continue
  fi

  arm_archs="$(lipo -archs "$arm_file" 2>/dev/null || true)"
  x64_archs="$(lipo -archs "$x64_file" 2>/dev/null || true)"
  if [[ -n "$arm_archs" && "$arm_archs" == "$x64_archs" ]]; then
    # Already universal (or same arch set), keep arm64 copy.
    continue
  fi

  lipo -create "$x64_file" "$arm_file" -output "$dest_file"
  chmod +x "$dest_file" || true
done < <(find "$arm_src" -type f -print0)

populate_resources "$bundle_macos"
