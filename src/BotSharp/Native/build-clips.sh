#!/bin/bash
# Build libclips shared library from CLIPS 6.4.2 source
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
CORE_DIR="$SCRIPT_DIR/clips/clips_core_source_642/core"

if [ ! -d "$CORE_DIR" ]; then
    echo "CLIPS source not found at $CORE_DIR"
    echo "Download: curl -L 'https://sourceforge.net/projects/clipsrules/files/CLIPS/6.4.2/clips_core_source_642.tar.gz/download' -o clips_source.tar.gz"
    exit 1
fi

# Detect platform
case "$(uname -s)-$(uname -m)" in
    Darwin-arm64)
        RID="osx-arm64"
        EXT="dylib"
        ;;
    Darwin-x86_64)
        RID="osx-x64"
        EXT="dylib"
        ;;
    Linux-x86_64)
        RID="linux-x64"
        EXT="so"
        ;;
    Linux-aarch64)
        RID="linux-arm64"
        EXT="so"
        ;;
    *)
        echo "Unsupported platform: $(uname -s)-$(uname -m)"
        exit 1
        ;;
esac

OUT_DIR="$SCRIPT_DIR/../runtimes/$RID/native"
mkdir -p "$OUT_DIR"

echo "Building libclips.$EXT for $RID..."
cd "$CORE_DIR"
cc -shared -fPIC -O2 -DCLIPS_STOCK_FUNCTION_SET=1 -o "$OUT_DIR/libclips.$EXT" *.c

echo "Built: $OUT_DIR/libclips.$EXT ($(du -h "$OUT_DIR/libclips.$EXT" | cut -f1))"
