#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then echo "usage: $0 <mdview.app>" >&2; exit 2; fi
app=$(cd "$1" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
printf '# Release startup smoke\n' > "$work/start.md"
"$app/Contents/MacOS/mdview" "$work/start.md" &
pid=$!
for _ in {1..50}; do
  kill -0 "$pid" 2>/dev/null || { wait "$pid" || true; echo "release app exited during startup" >&2; exit 1; }
  sleep 0.1
done
kill -TERM "$pid"
deadline=$((SECONDS + 10))
while kill -0 "$pid" 2>/dev/null; do
  (( SECONDS < deadline )) || { kill -KILL "$pid" 2>/dev/null || true; echo "release app ignored SIGTERM" >&2; exit 1; }
  sleep 0.1
done
set +e
wait "$pid"
status=$?
set -e
[[ $status -eq 0 || $status -eq 143 ]] || { echo "release app exited with $status" >&2; exit 1; }
