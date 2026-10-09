#!/bin/bash
# Builds the two release zips into dist/ (see docs/BUILDING.md):
#   Spotify-Radio-<version>-mod.zip     gamedata/ at the root: MO2 "Install a new mod from archive"
#   Spotify-Radio-<version>-daemon.zip  Spotify-Radio-daemon/: unpack anywhere outside MO2
# usage: tools/package.sh <version> <path to librespot.exe>
set -euo pipefail
VERSION=${1:?version}; LIBRESPOT=${2:?path to librespot.exe}
ROOT=$(cd "$(dirname "$0")/.." && pwd); DIST=$ROOT/dist; WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$DIST"

(cd "$ROOT/mod/Spotify Radio" && rm -f "$DIST/Spotify-Radio-$VERSION-mod.zip" && zip -qr "$DIST/Spotify-Radio-$VERSION-mod.zip" gamedata)

D=$WORK/Spotify-Radio-daemon; mkdir -p "$D/librespot"
(cd "$ROOT/daemon" && dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$WORK/pub" >/dev/null)
cp "$WORK/pub/SpotifyRadio.exe" "$ROOT/daemon/config.example.json" "$ROOT/README.md" "$ROOT/LICENSE" "$D/"
cp "$LIBRESPOT" "$ROOT/daemon/librespot/LICENSE-librespot.txt" "$D/librespot/"
printf '@echo off\r\ncd /d "%%~dp0"\r\nSpotifyRadio.exe\r\npause\r\n' > "$D/run.bat"
(cd "$WORK" && rm -f "$DIST/Spotify-Radio-$VERSION-daemon.zip" && zip -qr "$DIST/Spotify-Radio-$VERSION-daemon.zip" Spotify-Radio-daemon)
ls -l "$DIST"/Spotify-Radio-"$VERSION"-*.zip
