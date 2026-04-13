#!/bin/bash
set -euo pipefail

# Only run in Claude Code remote (web) sessions
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

# ── Install .NET SDK ───────────────────────────────────────────────────────────
if ! command -v dotnet &>/dev/null || ! dotnet --list-sdks | grep -q "^10\."; then
  echo "[session-start] Installing .NET 10 SDK..."
  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"
else
  echo "[session-start] .NET 10 SDK already present: $(dotnet --version)"
fi

# Persist PATH for all subsequent commands in this session
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  echo 'export PATH="$PATH:$HOME/.dotnet"' >> "$CLAUDE_ENV_FILE"
fi

export PATH="$PATH:$HOME/.dotnet"

# ── Install ffmpeg ─────────────────────────────────────────────────────────────
if ! command -v ffmpeg &>/dev/null; then
  echo "[session-start] Installing ffmpeg..."
  apt-get update -q
  apt-get install -y -q ffmpeg
fi

# ── Install Python dependencies ────────────────────────────────────────────────
echo "[session-start] Installing Python dependencies..."
pip install -q faster-whisper edge-tts googletrans

# ── Restore NuGet packages ─────────────────────────────────────────────────────
echo "[session-start] Restoring NuGet packages..."
dotnet restore "$CLAUDE_PROJECT_DIR/BabelPlayer.csproj"
dotnet restore "$CLAUDE_PROJECT_DIR/BabelPlayer.Tests/BabelPlayer.Tests.csproj"

echo "[session-start] Done."
