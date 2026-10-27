#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 3 || $# -gt 4 || ${4:-} != "" && ${4:-} != "--smoke" ]]; then
  echo "usage: $0 <osx-arm64|osx-x64> <version> <outdir> [--smoke]" >&2
  exit 2
fi

rid=$1
version=${2#v}
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "version must be numeric major.minor.patch" >&2; exit 2; }
outdir=$(mkdir -p "$3" && cd "$3" && pwd)
smoke=${4:-}
case "$rid" in
  osx-arm64) architecture=arm64 ;;
  osx-x64) architecture=x64 ;;
  *) echo "unsupported runtime identifier: $rid" >&2; exit 2 ;;
esac

repo=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
app="$work/mdview.app"
stage="$work/dmg"
publish="$work/publish"

publish_args=(dotnet publish "$repo/src/MdView.Mac/MdView.Mac.csproj" -c Release -r "$rid" -p:Version="$version" -o "$publish")
if [[ "$smoke" == "--smoke" ]]; then publish_args+=(-p:MdViewSmoke=true); fi
"${publish_args[@]}"
unexpected=$(find "$publish" -mindepth 1 -maxdepth 1 ! -name mdview ! -name mdview.pdb -print)
[[ -z "$unexpected" ]] || { echo "unexpected publish output:" >&2; echo "$unexpected" >&2; exit 1; }
actual_arch=$(lipo -archs "$publish/mdview")
expected_arch=$([[ "$rid" == osx-arm64 ]] && echo arm64 || echo x86_64)
[[ "$actual_arch" == "$expected_arch" ]] || { echo "expected $expected_arch binary, got $actual_arch" >&2; exit 1; }
mkdir -p "$app/Contents/MacOS" "$stage"
sed "s/@VERSION@/$version/g" "$repo/packaging/macos/Info.plist" > "$app/Contents/Info.plist"
cp "$publish/mdview" "$app/Contents/MacOS/mdview"
chmod 755 "$app/Contents/MacOS/mdview"
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"
cp -R "$app" "$stage/mdview.app"
ln -s /Applications "$stage/Applications"
# hdiutil's own size estimate for -srcfolder intermittently comes up short on CI runners
# ("No space left on device"), so the image is sized explicitly and retried.
size_mb=$(( $(du -sm "$stage" | cut -f1) + 32 ))
dmg="$outdir/mdview-$version-macos-$architecture.dmg"
for attempt in 1 2 3; do
  if hdiutil create -volname mdview -srcfolder "$stage" -size "${size_mb}m" -ov -format UDZO "$dmg"; then break; fi
  [[ $attempt -lt 3 ]] || { echo "hdiutil create failed after $attempt attempts" >&2; exit 1; }
  sleep 5
done
hdiutil verify "$dmg"
