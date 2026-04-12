#!/bin/bash
set -euo pipefail

# Only run in Claude Code remote (web) sessions
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

# ── Install .NET SDK ───────────────────────────────────────────────────────────
if ! command -v dotnet &>/dev/null || ! dotnet --list-sdks | grep -q "^10\."; then
  echo "[session-start] Installing .NET 10 SDK..."
  # Download installer to temp file and verify before execution
  DOTNET_INSTALLER=$(mktemp)
  trap 'rm -f "$DOTNET_INSTALLER"' EXIT

  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$DOTNET_INSTALLER"

  # Fetch checksum from Microsoft's official source
  DOTNET_INSTALLER_SHA=$(mktemp)
  if curl -fsSL https://dot.net/v1/dotnet-install.sh.sha512 -o "$DOTNET_INSTALLER_SHA" 2>/dev/null; then
    # Verify checksum if available
    if command -v sha512sum &>/dev/null; then
      echo "$(cat "$DOTNET_INSTALLER_SHA")  $DOTNET_INSTALLER" | sha512sum -c - || {
        echo "[session-start] ERROR: dotnet-install.sh checksum verification failed" >&2
        exit 1
      }
    else
      echo "[session-start] WARNING: sha512sum not available, skipping checksum verification"
    fi
    rm -f "$DOTNET_INSTALLER_SHA"
  else
    echo "[session-start] WARNING: Could not fetch installer checksum, proceeding without verification"
  fi

  bash "$DOTNET_INSTALLER" --channel 10.0 --install-dir "$HOME/.dotnet"
  rm -f "$DOTNET_INSTALLER"
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
pip install -q faster-whisper==1.2.1 edge-tts==7.2.8 googletrans==4.0.0-rc1

# ── Restore NuGet packages ─────────────────────────────────────────────────────
echo "[session-start] Restoring NuGet packages..."
dotnet restore "$CLAUDE_PROJECT_DIR/BabelPlayer.csproj"
dotnet restore "$CLAUDE_PROJECT_DIR/BabelPlayer.Tests/BabelPlayer.Tests.csproj"

echo "[session-start] Done."