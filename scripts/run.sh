#!/usr/bin/env bash
# Builds the mod and launches Stardew Valley directly through SMAPI, bypassing Steam's
# library UI -- you don't need to click through it each time, just rerun this script.
#
# Usage: scripts/run.sh [extra `dotnet build` args...]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
GAME_DIR="/Users/benjaminconnick/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS"

echo "==> Building mod..."
dotnet build "$PROJECT_ROOT/LanguageStudyStardewValleyMod.csproj" "$@"

echo "==> Launching SMAPI (Ctrl-C to quit)..."
cd "$GAME_DIR"
exec ./StardewModdingAPI
