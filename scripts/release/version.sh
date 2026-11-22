#!/usr/bin/env bash
# The app version lives in VERSION (x.y.z, what CFBundleShortVersionString and .NET <Version> accept).
#   scripts/release/version.sh check       fail if any project disagrees with VERSION
#   scripts/release/version.sh set 1.0.0   write a new version into VERSION and every project
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

XCODE=(apps/potion-macos/project.yml apps/dropper-macos/project.yml)
DOTNET=(apps/potion-windows/Potion/Potion.csproj apps/dropper-windows/PotionDropper/PotionDropper.csproj core/dotnet/Potion.Core/Potion.Core.csproj)

case "${1:-}" in
  check)
    version="$(tr -d '[:space:]' < VERSION)"
    status=0
    for f in "${XCODE[@]}"; do
      grep -q "MARKETING_VERSION: $version\$" "$f" || { echo "✗ $f: MARKETING_VERSION isn't $version" >&2; status=1; }
    done
    for f in "${DOTNET[@]}"; do
      grep -q "<Version>$version</Version>" "$f" || { echo "✗ $f: <Version> isn't $version" >&2; status=1; }
    done
    [[ $status -eq 0 ]] && echo "✓ every project is at version $version"
    exit $status
    ;;
  set)
    version="${2:?usage: version.sh set x.y.z}"
    [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Version must be x.y.z" >&2; exit 2; }
    echo "$version" > VERSION
    for f in "${XCODE[@]}"; do sed -i.bak -E "s/MARKETING_VERSION: .*/MARKETING_VERSION: $version/" "$f" && rm "$f.bak"; done
    for f in "${DOTNET[@]}"; do sed -i.bak -E "s#<Version>[^<]*</Version>#<Version>$version</Version>#" "$f" && rm "$f.bak"; done
    "$0" check
    ;;
  *)
    sed -n '2,4p' "$0" >&2
    exit 2
    ;;
esac
