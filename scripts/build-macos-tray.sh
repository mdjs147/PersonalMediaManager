#!/usr/bin/env bash
set -euo pipefail

usage() {
    printf '%s\n' '用法：bash scripts/build-macos-tray.sh <服务输出目录> [arm64|x64]' \
        '必须在 macOS 上安装 Xcode Command Line Tools；默认使用当前机器架构。' \
        '产物 PersonalMediaManager.MacTray 应与 PersonalMediaManager.Server 位于同一目录。'
}

if [[ $# -eq 1 && ( "$1" == '--help' || "$1" == '-h' ) ]]; then
    usage
    exit 0
fi
if [[ $# -lt 1 || $# -gt 2 || -z "$1" ]]; then
    usage >&2
    exit 64
fi

architecture="${2:-$(uname -m)}"
case "$architecture" in
    arm64) ;;
    x64|x86_64) architecture='x86_64' ;;
    *) printf '%s\n' '不支持的架构，请指定 arm64 或 x64。' >&2; exit 64 ;;
esac

if [[ "$(uname -s)" != 'Darwin' ]]; then
    printf '%s\n' 'macOS 菜单栏辅助程序只能在 macOS 上使用 AppKit SDK 编译。' >&2
    exit 1
fi
if ! command -v xcrun >/dev/null 2>&1 || ! xcrun --sdk macosx --find swiftc >/dev/null 2>&1; then
    printf '%s\n' '找不到 Swift 编译器；请先安装并选中 Xcode Command Line Tools。' >&2
    exit 1
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_dir="$repo_root/src/PersonalMediaManager.MacTray"
mkdir -p "$1"
output_dir="$(cd "$1" && pwd)"
temporary_output="$(mktemp "$output_dir/.pmm-tray.XXXXXX")"
trap 'rm -f "$temporary_output"' EXIT

xcrun --sdk macosx swiftc \
    -swift-version 5 -parse-as-library \
    -O -whole-module-optimization \
    -target "$architecture-apple-macos13.0" \
    -framework AppKit \
    "$source_dir/TrayOptions.swift" "$source_dir/main.swift" \
    -o "$temporary_output"

chmod +x "$temporary_output"
mv -f "$temporary_output" "$output_dir/PersonalMediaManager.MacTray"
printf '已生成：%s/PersonalMediaManager.MacTray\n' "$output_dir"
