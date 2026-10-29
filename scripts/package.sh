#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIST="$ROOT/dist"
OUT="$ROOT/release"
ZIP="$OUT/VidSort-win-x64.zip"

if command -v dotnet >/dev/null 2>&1; then
    DOTNET_BIN="$(command -v dotnet)"
elif [[ -x "$HOME/.dotnet/dotnet" ]]; then
    DOTNET_BIN="$HOME/.dotnet/dotnet"
else
    echo "未找到 dotnet，请先安装 .NET SDK。" >&2
    exit 1
fi

export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

rm -rf -- "$DIST"
cd "$ROOT"
"$DOTNET_BIN" publish src/VidSort.App -c Release -r win-x64 --self-contained -p:EnableWindowsTargeting=true -o "$DIST"

echo "裁剪前体积: $(du -sh "$DIST" | awk '{print $1}')"

rm -rf -- "$DIST/libvlc/win-x86" "$DIST/libvlc/win-arm64"
rm -rf -- "$DIST/libvlc/win-x64/lua" "$DIST/libvlc/win-x64/hrtfs"
if [[ -d "$DIST/libvlc" ]]; then
    find "$DIST/libvlc" -type f -name '*.lib' -exec rm -rf -- {} +
fi

rm -rf -- \
    "$DIST/libvlc/win-x64/plugins/access_output" \
    "$DIST/libvlc/win-x64/plugins/stream_out" \
    "$DIST/libvlc/win-x64/plugins/visualization" \
    "$DIST/libvlc/win-x64/plugins/visualizer" \
    "$DIST/libvlc/win-x64/plugins/services_discovery" \
    "$DIST/libvlc/win-x64/plugins/gui"

shopt -s nullglob
for dir in "$DIST"/*/; do
    name="${dir%/}"
    name="${name##*/}"
    case "$name" in
        en|zh-Hans|zh-Hant|libvlc)
            continue
            ;;
    esac
    if [[ "$name" =~ ^[a-z]{2,3}(-[A-Za-z0-9]+)*$ ]]; then
        rm -rf -- "$dir"
    fi
done
shopt -u nullglob

echo "裁剪后体积: $(du -sh "$DIST" | awk '{print $1}')"
echo "剩余插件 DLL 数量: $(find "$DIST/libvlc/win-x64/plugins" -type f -name '*.dll' 2>/dev/null | wc -l | tr -d '[:space:]')"

for required in "$DIST/VidSort.exe" "$DIST/libvlc/win-x64/libvlc.dll"; do
    if [[ ! -f "$required" ]]; then
        echo "缺少必要文件: $required" >&2
        exit 1
    fi
done

mkdir -p "$OUT"
rm -rf -- "$ZIP" "$ZIP.sha256"

if command -v zip >/dev/null 2>&1; then
    (cd "$DIST" && zip -qr "$ZIP" .)
else
    if ! command -v python3 >/dev/null 2>&1; then
        echo "未找到 zip 或 python3，无法打包。" >&2
        exit 1
    fi
    (cd "$DIST" && python3 -m zipfile -c "$ZIP" .)
fi

(
    cd "$OUT"
    sha256sum "${ZIP##*/}" > "${ZIP##*/}.sha256"
    cat "${ZIP##*/}.sha256"
)

echo "ZIP: $ZIP"
echo "ZIP 体积: $(du -h "$ZIP" | awk '{print $1}')"
echo "打包文件数: $(find "$DIST" -type f | wc -l | tr -d '[:space:]')"
echo "打包完成"
