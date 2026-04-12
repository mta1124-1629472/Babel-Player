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
    cd "$CLAUDE_PROJECT_DIR"
    echo "[arch-check] $FILE_PATH changed — running architecture linter..."
    python3 scripts/check-architecture.py
    ;;
esac