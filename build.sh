#!/usr/bin/env bash
# build.sh - Linux/macOS NUKE bootstrap (mirrors build.ps1)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_PROJECT_FILE="$SCRIPT_DIR/build/Babel Player.csproj"
TEMP_DIRECTORY="$SCRIPT_DIR/.nuke/temp"
DOTNET_GLOBAL_FILE="$SCRIPT_DIR/global.json"
DOTNET_INSTALL_URL="https://dot.net/v1/dotnet-install.sh"
DOTNET_CHANNEL="Current"

export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_MULTILEVEL_LOOKUP=0

# Use globally installed dotnet if available
if command -v dotnet &>/dev/null && dotnet --version &>/dev/null; then
    DOTNET_EXE="$(command -v dotnet)"
else
    # Download and run the dotnet install script
    DOTNET_INSTALL_FILE="$TEMP_DIRECTORY/dotnet-install.sh"
    mkdir -p "$TEMP_DIRECTORY"
    curl -fsSL "$DOTNET_INSTALL_URL" -o "$DOTNET_INSTALL_FILE"
    chmod +x "$DOTNET_INSTALL_FILE"

    DOTNET_DIRECTORY="$TEMP_DIRECTORY/dotnet-unix"

    # Respect global.json version if present
    if [ -f "$DOTNET_GLOBAL_FILE" ] && command -v python3 &>/dev/null; then
        DOTNET_VERSION=$(python3 -c "
import json, sys
with open('$DOTNET_GLOBAL_FILE') as f:
    d = json.load(f)
print(d.get('sdk', {}).get('version', ''))
" 2>/dev/null || true)
    fi

    if [ -n "${DOTNET_VERSION:-}" ]; then
        bash "$DOTNET_INSTALL_FILE" --install-dir "$DOTNET_DIRECTORY" --version "$DOTNET_VERSION" --no-path
    else
        bash "$DOTNET_INSTALL_FILE" --install-dir "$DOTNET_DIRECTORY" --channel "$DOTNET_CHANNEL" --no-path
    fi

    DOTNET_EXE="$DOTNET_DIRECTORY/dotnet"
fi

echo "Microsoft (R) .NET SDK version $($DOTNET_EXE --version)"

"$DOTNET_EXE" build "$BUILD_PROJECT_FILE" /nodeReuse:false /p:UseSharedCompilation=false -nologo -clp:NoSummary --verbosity quiet
"$DOTNET_EXE" run --project "$BUILD_PROJECT_FILE" --no-build -- "$@"
