#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
rid="${1:-}"
case "$rid" in linux-x64|linux-arm64|osx-x64|osx-arm64) ;; *) echo '用法：bash scripts/build-portable.sh linux-x64|linux-arm64|osx-x64|osx-arm64' >&2; exit 2;; esac
if [[ "$rid" == osx-* && "$(uname -s)" != Darwin ]]; then
  echo 'macOS 完整包必须在 macOS 构建，以编译 AppKit 菜单栏组件' >&2; exit 2
fi
npm --prefix src/PersonalMediaManager.Frontend ci
out="$PWD/artifacts/publish/$rid"
dotnet publish src/PersonalMediaManager.Server -c Release -r "$rid" --self-contained true -m:1 -o "$out"
if [[ "$rid" == osx-* ]]; then
  bash scripts/build-macos-tray.sh "$out" "${rid#osx-}"
fi
printf '已构建 %s\n' "$out"
