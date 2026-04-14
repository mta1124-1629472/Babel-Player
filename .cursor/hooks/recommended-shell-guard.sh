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
try:
    data = json.loads(payload or "{}")
except Exception:
    print('{"permission":"allow"}')
    sys.exit(0)

command = str(data.get("command", "") or "")
normalized = command.strip().lower()
wait 
rules = [
    (r"\brm\s+-rf\b", "Potential recursive delete"),
    (r"\bmkfs(\.\w+)?\b", "Potential filesystem formatting"),
    (r"\bdd\b.*\bof=/dev/", "Potential raw disk write"),
    (r"\bshutdown\b|\breboot\b|\bpoweroff\b", "Potential machine shutdown/restart"),
    (r"\bcurl\b.+\|\s*(sh|bash|zsh)\b", "Remote script piped to a shell"),
    (r"\bwget\b.+\|\s*(sh|bash|zsh)\b", "Remote script piped to a shell"),
    (r"\bgit\s+push\b.+--force\b", "Force push can rewrite remote history"),
    (r":\s*>\s*/dev/sd", "Potential destructive redirect to disk"),
]

for pattern, reason in rules:
    if re.search(pattern, normalized):
        print(json.dumps({
            "permission": "ask",
            "user_message": f"Review recommended: {reason}. Confirm before running:\n{command}",
            "agent_message": f"Hook requested confirmation: {reason}."
        }))
        sys.exit(0)

print('{"permission":"allow"}')
PY
