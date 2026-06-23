#!/usr/bin/env bash
# Build a universal macOS .dmg for local testing.
# Mirrors the CI steps in .github/workflows/package.yml.
# Usage: ./dist.macos.sh [--skip-publish]
#   --skip-publish  reuse existing publish output (faster iteration on bundle/dmg)

set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
host_dir="$script_dir/src/Roma.Host"

skip_publish=0
for arg in "$@"; do
  [[ "$arg" == "--skip-publish" ]] && skip_publish=1
done

if [[ "$skip_publish" -eq 0 ]]; then
  echo "==> Restoring and publishing osx-x64…"
  dotnet restore -r osx-x64 --force-evaluate "$host_dir"
  dotnet publish "$host_dir" -r osx-x64 -c Release

  echo "==> Restoring and publishing osx-arm64…"
  dotnet restore -r osx-arm64 --force-evaluate "$host_dir"
  dotnet publish "$host_dir" -r osx-arm64 -c Release
else
  echo "==> Skipping publish (--skip-publish)"
fi

echo "==> Building .app bundle (universal)…"
"$script_dir/build/macos/build-application-bundle.sh" osx-universal

echo "==> Building .dmg…"
"$script_dir/build/macos/build-dmg.sh" Roma.app Roma-macos-universal.dmg

echo ""
echo "Done: $(pwd)/Roma-macos-universal.dmg"
