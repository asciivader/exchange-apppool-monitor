#!/usr/bin/env bash
# Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.
# Builds the Exchange AppPool Monitor installers on Linux or macOS.
# Same steps as build-installer.ps1: for each edition, publish for win-x64 with
# -p:Flavor=<edition>, add the README, LICENSE and docs, run makensis. Output goes to ../artifacts/ (ignored by git):
#   ExchangeAppPoolMonitor-Setup-<version>-standalone.exe   for servers without .NET
#   ExchangeAppPoolMonitor-Setup-<version>-dotnet6.exe      for servers with the ASP.NET Core Runtime 6.0
#
# Needs: the .NET 6 SDK (or newer) and NSIS 3 (Debian/Ubuntu: apt install nsis; macOS: brew install makensis).
#
# Usage: ./build-installer.sh [All|Standalone|DotNet6] [version] [package folder]
#        (edition defaults to All; version defaults to <Version> in the .csproj; "" keeps the default)
#        Give a package folder to restore only from it, for air-gapped build machines
#        (the evaluation package's "packages" folder). See docs/DEPENDENCIES.md.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
project="$(dirname "$here")"
csproj="$project/ExchangeAppPoolMonitor.csproj"
artifacts="$project/artifacts"

flavor="${1:-All}"
pkgsource="${3:-}"
version="${2:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$csproj" | head -n1)}"
[ -n "$version" ] || { echo "No <Version> found in $csproj" >&2; exit 1; }
command -v makensis >/dev/null || { echo "makensis not found. Install NSIS 3." >&2; exit 1; }

case "$flavor" in
  All) flavors="Standalone DotNet6" ;;
  Standalone|DotNet6) flavors="$flavor" ;;
  *) echo "Edition must be All, Standalone or DotNet6 (got $flavor)" >&2; exit 1 ;;
esac

for f in $flavors; do
  suffix="$(echo "$f" | tr '[:upper:]' '[:lower:]')"
  publish="$artifacts/publish-$suffix"
  setup="$artifacts/ExchangeAppPoolMonitor-Setup-$version-$suffix.exe"

  echo "Publishing $f edition, version $version..."
  rm -rf "$publish"
  if [ -n "$pkgsource" ]; then
    # Offline: restore only from the given folder, then build without touching the network.
    dotnet restore "$csproj" "-p:Flavor=$f" --source "$(cd "$pkgsource" && pwd)"
    dotnet publish "$csproj" -c Release "-p:Flavor=$f" --no-restore -o "$publish"
  else
    dotnet publish "$csproj" -c Release "-p:Flavor=$f" -o "$publish"
  fi
  cp "$project/README.md" "$project/LICENSE" "$publish/"
  cp -r "$project/docs" "$publish/docs"

  echo "Building $setup..."
  makensis -V2 "-DVERSION=$version" "-DFLAVOR=$f" "-DPUBLISH_DIR=$publish" "-DOUT_FILE=$setup" "$here/ExchangeAppPoolMonitor.nsi"
  echo "Done: $setup"
done
