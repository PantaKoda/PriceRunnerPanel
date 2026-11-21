#!/usr/bin/env bash
# Builds the Linux release: the self-contained linux-x64 publish (scripts/publish-linux.sh) packed as a single-file
# Flatpak bundle,
#   artifacts/release/PriceRunnerPanel-<version>-x86_64.flatpak  (+ .sha256)
# flatpak-builder only copies the published folder (packaging/flatpak/io.github.PantaKoda.PriceRunnerPanel.yml); it
# never compiles .NET. Installing the bundle fetches the org.freedesktop.Platform runtime from Flathub.
#
#   scripts/package-flatpak.sh                    # publish, then package
#   scripts/package-flatpak.sh --skip-publish     # package the existing artifacts/publish/linux-x64
#   scripts/package-flatpak.sh --version 0.0.1    # a local test build that reports an older version
#
# Needs flatpak and flatpak-builder (on Ubuntu: apt install flatpak flatpak-builder).
set -euo pipefail

version=""
publish=true
while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) version="${2:?--version needs a value}"; shift 2 ;;
        --skip-publish) publish=false; shift ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

step() { printf '\033[36m==> %s\033[0m\n' "$1"; }

app_id=io.github.PantaKoda.PriceRunnerPanel
flathub=https://dl.flathub.org/repo/flathub.flatpakrepo
publish_dir="$root/artifacts/publish/linux-x64"
work="$root/artifacts/flatpak"
out="$root/artifacts/release"

if [[ -z "$version" ]]; then
    # Directory.Build.props holds the one version number; read it without needing the .NET SDK here.
    version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)"
fi
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Unexpected version '$version'." >&2
    exit 1
fi

if $publish; then
    scripts/publish-linux.sh --version "$version"
fi
if [[ ! -x "$publish_dir/PriceRunnerPanel" ]]; then
    echo "No published app in $publish_dir: run scripts/publish-linux.sh first." >&2
    exit 1
fi

step 'Prepare the Flatpak sources'
rm -rf "$work"
mkdir -p "$work/src" "$out"
cp -a packaging/flatpak/. "$work/src/"
cp -a "$publish_dir" "$work/src/app"
# The release date is the commit's, so it does not depend on when the build ran.
date="$(git log -1 --format=%cs HEAD)"
sed -i -e "s/@VERSION@/$version/" -e "s/@DATE@/$date/" "$work/src/$app_id.metainfo.xml"

step 'Build (flatpak-builder)'
flatpak remote-add --user --if-not-exists flathub "$flathub"
# --disable-rofiles-fuse: works in containers and CI runners without FUSE.
flatpak-builder --user --install-deps-from=flathub --force-clean --disable-rofiles-fuse --disable-updates \
    --default-branch=stable --repo="$work/repo" "$work/build" "$work/src/$app_id.yml"

step 'Bundle'
bundle="PriceRunnerPanel-$version-x86_64.flatpak"
rm -f "$out/$bundle" "$out/$bundle.sha256"
flatpak build-bundle --runtime-repo="$flathub" "$work/repo" "$out/$bundle" "$app_id" stable
hash="$(sha256sum "$out/$bundle" | cut -d' ' -f1)"
# Same format as the Windows zip's checksum file: "<hash>  <file name>", no newline.
printf '%s  %s' "$hash" "$bundle" > "$out/$bundle.sha256"

echo
echo "Version : $version ($(git rev-parse --short HEAD))"
echo "Bundle  : $out/$bundle ($(du -h "$out/$bundle" | cut -f1))"
echo "SHA-256 : $hash"
echo "Install : flatpak install --user $bundle"
