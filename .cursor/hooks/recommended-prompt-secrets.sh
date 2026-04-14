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

def collect_strings(obj, out):
    if isinstance(obj, str):
        out.append(obj)
    elif isinstance(obj, dict):
        for v in obj.values():
            collect_strings(v, out)
    elif isinstance(obj, list):
        for v in obj:
            collect_strings(v, out)

try:
    data = json.loads(payload or "{}")
except Exception:
    print('{"permission":"allow"}')
    sys.exit(0)

texts = []
collect_strings(data, texts)
blob = "\n".join(texts)

patterns = [
    (r"sk-[a-zA-Z0-9]{20,}", "Possible OpenAI-style API key"),
    (r"sk_(live|test)_[0-9a-zA-Z]{24,}", "Possible Stripe secret key"),
    (r"gh[pousr]_[A-Za-z0-9_]{36,}", "Possible GitHub token"),
    (r"xox[baprs]-[A-Za-z0-9-]{10,}", "Possible Slack token"),
    (r"AKIA[0-9A-Z]{16}", "Possible AWS access key id"),
    (r"-----BEGIN [A-Z ]*PRIVATE KEY-----", "Possible PEM private key"),
    (r"Bearer\s+[A-Za-z0-9._\-]{30,}", "Possible bearer token"),
    (r"api[_-]?key\s*[:=]\s*['\"]?[A-Za-z0-9_\-]{20,}", "Possible api_key assignment"),
]

for pattern, reason in patterns:
    m = re.search(pattern, blob, re.IGNORECASE | re.MULTILINE)
    if m:
        snippet = blob[max(0, m.start() - 40) : m.start() + 40]
        print(json.dumps({
            "permission": "ask",
            "user_message": f"Review recommended: {reason}. Remove or redact secrets before submitting.\n…{snippet}…",
            "agent_message": f"Hook requested confirmation: {reason}."
        }))
        sys.exit(0)

print('{"permission":"allow"}')
PY
