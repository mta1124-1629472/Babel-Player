and we#!/usr/bin/env bash
set -euo pipefail

payload="$(cat)"

if ! command -v dotnet >/dev/null 2>&1; then
  exit 0
fi

ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
SLN="$ROOT/Babel-Player.sln"
[[ -f "$SLN" ]] || exit 0

if ! command -v python3 >/dev/null 2>&1; then
  exit 0
fi

file="$(python3 - <<'PY' "$payload"
import json
import sys
payload = sys.argv[1]
try:
    data = json.loads(payload or "{}")
except Exception:
    sys.exit(0)

def find_path(obj):
    if isinstance(obj, str) and obj.endswith(".cs") and ("/" in obj or "\\" in obj):
        return obj
    if isinstance(obj, dict):
        for k, v in obj.items():
            if str(k).lower() in ("path", "file_path", "filepath", "target_file"):
                if isinstance(v, str) and v.endswith(".cs"):
                    return v
            r = find_path(v)
            if r:
                return r
    elif isinstance(obj, list):
        for v in obj:
            r = find_path(v)
            if r:
                return r
    return None

p = find_path(data)
if p:
    print(p)
PY
)"

[[ -n "$file" ]] || exit 0

abs="$file"
if [[ "$file" != /* ]]; then
  abs="$ROOT/${file#./}"
fi
[[ -f "$abs" ]] || exit 0

dotnet format "$SLN" --include "$abs" --verbosity quiet 2>/dev/null || true
exit 0
