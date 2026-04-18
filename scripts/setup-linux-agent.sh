#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "This bootstrap is intended for Linux-based agent environments." >&2
  exit 1
fi

DOTNET_INSTALL_DIR="${DOTNET_INSTALL_DIR:-$HOME/.dotnet}"
DOTNET_CHANNEL="${DOTNET_CHANNEL:-10.0}"

export DOTNET_ROOT="$DOTNET_INSTALL_DIR"
export PATH="$DOTNET_INSTALL_DIR:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

needs_install=1
if command -v dotnet >/dev/null 2>&1; then
  if dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    needs_install=0
  fi
fi

if [[ $needs_install -eq 1 ]]; then
  tmp_script="$(mktemp)"
  trap 'rm -f "$tmp_script"' EXIT
  curl -sSL https://dot.net/v1/dotnet-install.sh -o "$tmp_script"
  chmod +x "$tmp_script"
  "$tmp_script" --channel "$DOTNET_CHANNEL" --install-dir "$DOTNET_INSTALL_DIR"
fi

dotnet --info >/dev/null
python3 --version >/dev/null

cat <<EOF
Linux agent bootstrap is ready.
DOTNET_ROOT=$DOTNET_ROOT
PATH now includes $DOTNET_INSTALL_DIR
EOF
