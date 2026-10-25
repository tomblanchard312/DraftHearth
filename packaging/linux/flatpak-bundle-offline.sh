#!/usr/bin/env bash
# Assemble the Nisaba Flatpak bundle without flatpak-builder (no Flathub access here):
# the same files and finish-args as packaging/linux/flatpak/io.github.tomblanchard312.Nisaba.bundle.yml.
# usage: packaging/linux/flatpak-bundle-offline.sh <nisaba tarball from package.sh> <out.flatpak>
# Needs flatpak and librsvg2-common (icon validation); the runtime is fetched from Flathub when the
# bundle is installed. Use flatpak-bundle.sh where Flathub is reachable: it also smoke-tests.
set -euo pipefail
TARBALL=$(realpath "$1") OUT=$(realpath -m "$2")
APP_ID=io.github.tomblanchard312.Nisaba
MANIFEST="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/flatpak/$APP_ID.bundle.yml"
WORK=$(mktemp -d)
mkdir -p "$WORK/stage" "$WORK/app/files"
tar -xzf "$TARBALL" -C "$WORK/stage" --strip-components=1
cd "$WORK/stage"
F="$WORK/app/files"
install -Dm755 bin/nisaba "$F/bin/nisaba"
install -Dm755 bin/nisaba-cli "$F/bin/nisaba-cli"
if [ -d lib/nisaba ]; then install -Dm755 -t "$F/lib/nisaba" lib/nisaba/sharedai-daemon lib/nisaba/sharedai-worker; fi
install -Dm644 share/applications/$APP_ID.desktop "$F/share/applications/$APP_ID.desktop"
install -Dm644 share/mime/packages/$APP_ID.xml "$F/share/mime/packages/$APP_ID.xml"
install -Dm644 share/metainfo/$APP_ID.metainfo.xml "$F/share/metainfo/$APP_ID.metainfo.xml"
mkdir -p "$F/share/icons" "$F/share/doc"
cp -R share/icons/hicolor "$F/share/icons/"
cp -R share/doc/nisaba "$F/share/doc/"

RT=$(sed -n "s/^runtime-version: '\(.*\)'/\1/p" "$MANIFEST")
cat > "$WORK/app/metadata" <<EOF
[Application]
name=$APP_ID
runtime=org.freedesktop.Platform/x86_64/$RT
sdk=org.freedesktop.Sdk/x86_64/$RT
command=nisaba
EOF
# finish-args straight from the manifest, so the bundle can't drift from it.
mapfile -t ARGS < <(sed -n '/^finish-args:/,/^modules:/p' "$MANIFEST" | sed -n 's/^  - \(--.*\)$/\1/p')
echo "finish-args: ${ARGS[*]}"
flatpak build-finish "$WORK/app" "${ARGS[@]}"
flatpak build-export "$WORK/repo" "$WORK/app" stable
flatpak build-bundle --runtime-repo=https://dl.flathub.org/repo/flathub.flatpakrepo "$WORK/repo" "$OUT" "$APP_ID" stable
echo "--- metadata"; cat "$WORK/app/metadata"
echo "--- exported"; find "$WORK/app/export" -type f | sed "s|$WORK/app/||"
rm -rf "$WORK"
ls -lh "$OUT"
