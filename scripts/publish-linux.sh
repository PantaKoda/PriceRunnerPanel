#!/usr/bin/env bash
# Builds the Linux app: a self-contained linux-x64 publish in artifacts/publish/linux-x64, which the Flatpak packages
# as it is (the Flatpak build never compiles .NET). The .NET runtime is included, so nothing has to be installed; the
# code is precompiled (ReadyToRun, see the csproj), so the app starts fast. Same flags as scripts/publish-windows.ps1.
#
#   scripts/publish-linux.sh                  # locked restore, build, publish
#   scripts/publish-linux.sh --version 0.0.1  # a local test build that reports an older version
#
# Needs the .NET SDK from global.json (on Aurora: inside a toolbox/distrobox, or let the release workflow build it).
set -euo pipefail

version=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) version="${2:?--version needs a value}"; shift 2 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

step() { printf '\033[36m==> %s\033[0m\n' "$1"; }

project=src/PriceRunnerPanel/PriceRunnerPanel.csproj
# The project also builds for Windows (net10.0-windows…); the Linux app is the plain target.
framework=net10.0
runtime=linux-x64
publish_dir="$root/artifacts/publish/$runtime"

commit="$(git rev-parse HEAD)"
if [[ -n "$(git status --porcelain)" ]]; then
    echo "warning: the working tree has uncommitted or untracked files: this build is not reproducible from the commit and must not be released." >&2
fi

if [[ -z "$version" ]]; then
    version="$(dotnet msbuild "$project" -getProperty:Version | tr -d '[:space:]')"
fi
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Unexpected version '$version'." >&2
    exit 1
fi

export DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true

step 'Restore (locked)'
dotnet restore PriceRunnerPanel.slnx --locked-mode

# Start from empty output folders so files from an earlier publish cannot leak into the package.
rm -rf "$publish_dir" "$root/src/PriceRunnerPanel/obj/Release"

step 'Publish'
dotnet publish "$project" -c Release -f "$framework" -r "$runtime" --self-contained -p:RestoreLockedMode=true \
    "-p:Version=$version" -p:ContinuousIntegrationBuild=true -p:DebugType=none -p:DebugSymbols=false \
    -p:SatelliteResourceLanguages=en -o "$publish_dir"

if [[ ! -x "$publish_dir/PriceRunnerPanel" ]]; then
    echo 'The publish did not produce an executable PriceRunnerPanel.' >&2
    exit 1
fi
# The Linux build must not carry the Windows-only parts (WinRT projections, DPAPI).
if compgen -G "$publish_dir/WinRT.Runtime.dll" > /dev/null || compgen -G "$publish_dir/Microsoft.Windows.SDK.NET.dll" > /dev/null \
    || compgen -G "$publish_dir/System.Security.Cryptography.ProtectedData.dll" > /dev/null; then
    echo 'The Linux publish contains Windows-only assemblies.' >&2
    exit 1
fi

echo
echo "Version : $version (${commit:0:7})"
echo "Folder  : $publish_dir ($(du -sh "$publish_dir" | cut -f1), $(find "$publish_dir" -type f | wc -l) files)"
echo "Run     : $publish_dir/PriceRunnerPanel"
