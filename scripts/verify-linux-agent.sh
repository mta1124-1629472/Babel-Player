#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

source "$repo_root/scripts/setup-linux-agent.sh"

dotnet restore Babel-Player.sln
dotnet build Babel-Player.sln -c Release --no-restore
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release --no-build
python3 scripts/check-architecture.py
python3 -m py_compile inference/main.py

echo "Linux agent verification completed successfully."
