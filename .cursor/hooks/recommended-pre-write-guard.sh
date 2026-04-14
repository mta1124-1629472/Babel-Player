#!/usr/bin/env bash
set -euo pipefail

payload="$(cat)"

if ! command -v python3 >/dev/null 2>&1; then
  echo '{"permission":"allow"}'
  exit 0
fi

python3 - <<'PY' "$payload"
import json
import re
import sys

payload = sys.argv[1]

SENSITIVE_PATH_RES = [
    (r"(^|/)\.env(\.|$|$)", ".env files often contain secrets"),
    (r"\.pem$", "PEM files often contain keys or certs"),
    (r"id_rsa(\.pub)?$", "SSH private key material"),
    (r"credentials\.json$", "Credential JSON is often sensitive"),
    (r"\.pfx$", "PFX archives often contain private keys"),
    (r"(^|/)secrets(/|$)", "Path under secrets/"),
    (r"\.cursor/mcp\.json$", "MCP config may contain tokens"),
]

PATH_KEYS = frozenset(
    {"path", "file_path", "filepath", "target_file", "abspath", "uri"}
)


def collect_paths(obj, acc):
    if isinstance(obj, dict):
        for k, v in obj.items():
            lk = str(k).lower()
            if lk in PATH_KEYS and isinstance(v, str):
                acc.append(v)
            else:
                collect_paths(v, acc)
    elif isinstance(obj, list):
        for v in obj:
            collect_paths(v, acc)

try:
    data = json.loads(payload or "{}")
except Exception:
    print('{"permission":"allow"}')
    sys.exit(0)

candidates = []
collect_paths(data, candidates)

for raw in candidates:
    p = raw.replace("\\", "/").strip()
    if not p:
        continue
    low = p.lower()
    for pattern, reason in SENSITIVE_PATH_RES:
        if re.search(pattern, low):
            print(json.dumps({
                "permission": "ask",
                "user_message": f"Review recommended: {reason}.\nTarget path: {p}",
                "agent_message": f"Hook requested confirmation before writing: {reason}."
            }))
            sys.exit(0)

print('{"permission":"allow"}')
PY
