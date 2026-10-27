#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 <osx-arm64|osx-x64> <version> <outdir>" >&2
  exit 2
fi

rid=$1
version=${2#v}
outdir=$(mkdir -p "$3" && cd "$3" && pwd)
case "$rid" in
  osx-arm64) architecture=arm64 ;;
  osx-x64) architecture=x64 ;;
  *) echo "unsupported runtime identifier: $rid" >&2; exit 2 ;;
esac

repo=$(cd "$(dirname "$0")/../.." && pwd)
publish="$repo/src/MdView.Mac/bin/Release/net8.0/$rid/publish"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
app="$work/mdview.app"
stage="$work/dmg"

dotnet publish "$repo/src/MdView.Mac/MdView.Mac.csproj" -c Release -r "$rid" -p:Version="$version"
mkdir -p "$app/Contents/MacOS" "$stage"
sed "s/@VERSION@/$version/g" "$repo/packaging/macos/Info.plist" > "$app/Contents/Info.plist"
cp "$publish/mdview" "$app/Contents/MacOS/mdview"
chmod 755 "$app/Contents/MacOS/mdview"
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"
cp -R "$app" "$stage/mdview.app"
ln -s /Applications "$stage/Applications"
hdiutil create -volname mdview -srcfolder "$stage" -ov -format UDZO \
  "$outdir/mdview-$version-macos-$architecture.dmg"
