#!/usr/bin/env bash
# Builds tools/ServeProbe self-contained for win-x64 and runs every suite natively on the owner's
# Windows through WSL interop. Linux runs pipes over Unix sockets, does not enforce FileShare.None,
# and decodes file: URIs with Unix rules, so a Linux pass does not prove Windows behaviour (W3b found
# a test expectation that only Windows contradicted). The owner's mdview must be closed: the
# lifecycle suite uses the real single-instance mutex. Exit 0 only if every suite passed.
set -uo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
export PATH="$HOME/.dotnet:$PATH"
if tasklist.exe /FI "IMAGENAME eq mdview.exe" 2>/dev/null | tr -d '\r' | grep -qi '^mdview'; then
  echo "mdview is running on Windows; close it first (the lifecycle suite shares its mutex)." >&2; exit 2
fi
work="$(wslpath -u "$(cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')")/mdview-serveprobe-$$"
trap 'rm -rf "$work"' EXIT
dotnet publish "$repo/tools/ServeProbe" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true \
  -o "$work" -nologo -v quiet >/dev/null || exit 1
printf '# Lifecycle\r\n\r\nx\r\n' > "$work/life.md"
cd "$work"
status=0
for suite in --frame --placement --transport --theme --toggle --security --files --edit --browser --handoff \
  "--lifecycle $(wslpath -w "$work/life.md")"; do
  # shellcheck disable=SC2086
  out="$(./ServeProbe.exe $suite 2>&1 | tr -d '\r')"; code=$?
  printf '%-12s %s passed, %s failed\n' "${suite%% *}" "$(grep -c '=passed' <<<"$out")" "$(grep -c 'failed' <<<"$out")"
  grep -E 'failed|Unhandled' <<<"$out" | head -3
  [ $code -eq 0 ] || status=1
done
exit $status
