#!/usr/bin/env bash
# Publishes mdview, copies it and Check-Window.ps1 into a fresh folder under the owner's Windows
# %TEMP% (a local path, so Windows does not ask for approval), runs the check on the live desktop
# through WSL interop, prints the JSON result, and removes the folder. Exit code 0 only if every
# check passed. Windows will briefly show two mdview windows; no mouse or keyboard input is sent.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
export PATH="$HOME/.dotnet:$PATH"
publish="$(mktemp -d)"
trap 'rm -rf "$publish"' EXIT
dotnet publish "$repo/src/MdView" -c Release -r win-x64 -o "$publish" -nologo -v quiet >/dev/null
windows_temp="$(cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')"
work="$(wslpath -u "$windows_temp")/mdview-blackbox-$$"
mkdir -p "$work"
trap 'rm -rf "$publish" "$work"' EXIT
cp "$publish/mdview.exe" "$repo/tools/WindowsBlackBox/Check-Window.ps1" "$work/"
# The check writes the owner's real config.json (window placement) and must restore it. Verify that
# here, independently of the script's own restore, and fail the run if their settings changed.
owner_config="$(wslpath -u "$(cmd.exe /c 'echo %APPDATA%' 2>/dev/null | tr -d '\r')")/mdview/config.json"
config_before="$publish/config.before"
[ -f "$owner_config" ] && cp "$owner_config" "$config_before"
cd "$work"
status=0
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$work/Check-Window.ps1")" \
  -Exe "$(wslpath -w "$work/mdview.exe")" -WorkDir "$(wslpath -w "$work")" | tr -d '\r' || status=$?
if [ -f "$config_before" ]; then
  cmp -s "$owner_config" "$config_before" || { echo "OWNER CONFIG CHANGED: $owner_config differs from before the run" >&2; status=1; }
elif [ -f "$owner_config" ]; then
  echo "OWNER CONFIG CREATED: $owner_config did not exist before the run" >&2; status=1
fi
exit $status
