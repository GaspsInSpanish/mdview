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
cd "$work"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$work/Check-Window.ps1")" \
  -Exe "$(wslpath -w "$work/mdview.exe")" -WorkDir "$(wslpath -w "$work")" | tr -d '\r'
