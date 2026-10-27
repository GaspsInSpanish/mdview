#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo "usage: $0 <mdview.app> [direct|open|two]" >&2
  exit 2
fi
app=$(cd "$1" && pwd)
mode=${2:-direct}
work=$(mktemp -d)
log="$(mktemp)"
trap 'status=$?; if [[ $status -ne 0 ]]; then echo "--- mdview stderr"; cat "$log"; echo "--- document"; cat "$work/smoke.md" 2>/dev/null; fi; rm -rf "$work"' EXIT
document="$work/smoke.md"
report="$work/report.json"
cat > "$document" <<'MARKDOWN'
# Mac smoke heading

- [ ] smoke checkbox

![unsafe](missing.png){onerror="window.mdviewXss=true"}

```js
const smoke = true;
```
MARKDOWN

if [[ "$mode" == two ]]; then
  # Two windows in one process, closed one after the other, under NSZombieEnabled: an over-released
  # window (or anything else freed too early) becomes a hard "message sent to deallocated instance".
  second="$work/second.md"
  sed 's/Mac smoke heading/Mac smoke second/' "$document" > "$second"
  export NSZombieEnabled=YES
  MDVIEW_SMOKE_REPORT="$report" "$app/Contents/MacOS/mdview" "$document" 2>"$log" &
  pid=$!
  deadline=$((SECONDS + 15))
  until grep -q -- '- \[x\] smoke checkbox' "$document"; do
    (( SECONDS < deadline )) || { echo "first checkbox was not toggled" >&2; kill "$pid" 2>/dev/null || true; exit 1; }
    sleep 0.1
  done
  # A second launch hands its file to the running process over the pipe and exits.
  MDVIEW_SMOKE_REPORT="$report" "$app/Contents/MacOS/mdview" "$second" 2>>"$log" || { echo "hand-off launch failed" >&2; exit 1; }
  deadline=$((SECONDS + 15))
  until grep -q -- '- \[x\] smoke checkbox' "$second"; do
    (( SECONDS < deadline )) || { echo "second checkbox was not toggled" >&2; kill "$pid" 2>/dev/null || true; exit 1; }
    sleep 0.1
  done
  printf '\nSMOKE_RELOAD_MARKER\n' >> "$document"
  sleep 2
  printf '\nSMOKE_RELOAD_MARKER\n' >> "$second"
  deadline=$((SECONDS + 25))
  while kill -0 "$pid" 2>/dev/null; do
    (( SECONDS < deadline )) || { echo "mdview did not exit after both windows" >&2; kill "$pid" 2>/dev/null || true; exit 1; }
    sleep 0.1
  done
  set +e; wait "$pid"; status=$?; set -e
  if grep -q "deallocated instance" "$log"; then echo "zombie object messaged" >&2; exit 1; fi
  [[ $status -eq 0 ]] || { echo "mdview exited with $status" >&2; exit 1; }
  exit 0
elif [[ "$mode" == direct ]]; then
  MDVIEW_SMOKE_REPORT="$report" "$app/Contents/MacOS/mdview" "$document" 2>"$log" &
  pid=$!
elif [[ "$mode" == open ]]; then
  open --env MDVIEW_SMOKE_REPORT="$report" --stderr "$log" -W -a "$app" "$document" &
  pid=$!
else
  echo "unknown mode: $mode" >&2
  exit 2
fi

deadline=$((SECONDS + 15))
while ! grep -q -- '- \[x\] smoke checkbox' "$document"; do
  if (( SECONDS >= deadline )); then echo "checkbox was not toggled" >&2; kill "$pid" 2>/dev/null || true; exit 1; fi
  sleep 0.1
done
printf '\nSMOKE_RELOAD_MARKER\n' >> "$document"

deadline=$((SECONDS + 15))
while [[ ! -s "$report" ]]; do
  if (( SECONDS >= deadline )); then echo "smoke report was not written" >&2; kill "$pid" 2>/dev/null || true; exit 1; fi
  sleep 0.1
done
python3 - "$report" <<'PY'
import json, sys
r=json.load(open(sys.argv[1], encoding='utf-8'))
assert r['heading'].strip() == 'Mac smoke heading', r
assert r['platform'] == 'mac', r
for key in ('noTitleBar','bridge','xss','highlighted','toggle','reloaded'):
    assert r[key] is True, (key, r)
PY

deadline=$((SECONDS + 10))
while kill -0 "$pid" 2>/dev/null; do
  if (( SECONDS >= deadline )); then echo "mdview did not exit" >&2; kill "$pid" 2>/dev/null || true; exit 1; fi
  sleep 0.1
done
wait "$pid"
