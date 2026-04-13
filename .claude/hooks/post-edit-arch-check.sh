#!/bin/bash
# Runs the architecture linter after any .cs or .csproj file is edited.
# Receives tool context as JSON on stdin.

INPUT=$(cat)
FILE_PATH=$(python3 -c "
import sys, json
d = json.load(sys.stdin)
print(d.get('tool_input', {}).get('file_path', ''))
" <<< "$INPUT" 2>/dev/null)

case "$FILE_PATH" in
  *.cs|*.csproj)
    if ! cd "$CLAUDE_PROJECT_DIR"; then
      echo "[arch-check] ERROR: Failed to cd to CLAUDE_PROJECT_DIR='$CLAUDE_PROJECT_DIR' (FILE_PATH='$FILE_PATH')" >&2
      exit 1
    fi
    echo "[arch-check] $FILE_PATH changed — running architecture linter..."
    python3 scripts/check-architecture.py
    ;;
esac