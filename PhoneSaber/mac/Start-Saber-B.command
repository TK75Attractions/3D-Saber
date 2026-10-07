#!/bin/bash
# 実際のビルド先に合わせて、次の .app パスを変更してください。
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd -P)" || exit 1
GAME_APP="$SCRIPT_DIR/../../Builds/Mac/3D-Saber.app"
exec /bin/bash "$SCRIPT_DIR/saber-watchdog.sh" B "$GAME_APP"
