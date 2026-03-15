#!/usr/bin/env bash
# AppImageBuilder.sh
# Packages the published linux output into a self-contained AppImage.
#
# Prerequisites (installed automatically by the CI job, or manually for local builds):
#   - appimagetool  (downloaded automatically from GitHub Releases)
#   - libmpv2 / libmpv.so.2  (apt install libmpv2)
#
# Usage:
#   bash linux/AppImageBuilder.sh <publish_dir> <output_dir> <version> [arch]
#
# arch defaults to x86_64. Use aarch64 for arm64 builds.
#
# Examples:
#   bash linux/AppImageBuilder.sh artifacts/publish/linux-x64   artifacts v1.0.0
#   bash linux/AppImageBuilder.sh artifacts/publish/linux-arm64 artifacts v1.0.0 aarch64

set -euo pipefail

PUBLISH_DIR="${1:?Usage: $0 <publish_dir> <output_dir> <version> [arch]}"
OUTPUT_DIR="${2:?Usage: $0 <publish_dir> <output_dir> <version> [arch]}"
VERSION="${3:?Usage: $0 <publish_dir> <output_dir> <version> [arch]}"
ARCH="${4:-x86_64}"   # x86_64 or aarch64

# Map arch to dotnet runtime suffix for output filename
if [ "$ARCH" = "aarch64" ]; then
    RUNTIME_SUFFIX="linux-arm64"
else
    RUNTIME_SUFFIX="linux-x64"
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

APPDIR="$(mktemp -d)/BabelPlayer.AppDir"
mkdir -p "$APPDIR"

echo "[AppImage] Building AppDir at $APPDIR (arch=$ARCH, runtime=$RUNTIME_SUFFIX) ..."

# ── 1. Copy published output ─────────────────────────────────────────────────
mkdir -p "$APPDIR/usr/bin"
mkdir -p "$APPDIR/usr/lib"
mkdir -p "$APPDIR/usr/share/applications"
mkdir -p "$APPDIR/usr/share/icons/hicolor/256x256/apps"

cp -r "$PUBLISH_DIR/"* "$APPDIR/usr/bin/"

# ── 2. Bundle libmpv ─────────────────────────────────────────────────────────
if [ "$ARCH" = "aarch64" ]; then
    LIB_SEARCH_PATHS=(
        /usr/lib/aarch64-linux-gnu/libmpv.so.2
        /usr/lib/libmpv.so.2
        /usr/local/lib/libmpv.so.2
    )
else
    LIB_SEARCH_PATHS=(
        /usr/lib/x86_64-linux-gnu/libmpv.so.2
        /usr/lib/libmpv.so.2
        /usr/local/lib/libmpv.so.2
    )
fi

LIBMPV_PATH=""
for candidate in "${LIB_SEARCH_PATHS[@]}"; do
    if [ -f "$candidate" ]; then
        LIBMPV_PATH="$candidate"
        break
    fi
done

if [ -z "$LIBMPV_PATH" ]; then
    LIBMPV_PATH=$(ldconfig -p 2>/dev/null | grep 'libmpv.so.2' | awk '{print $NF}' | head -1 || true)
fi

if [ -z "$LIBMPV_PATH" ] || [ ! -f "$LIBMPV_PATH" ]; then
    echo "[AppImage] ERROR: libmpv.so.2 not found. Install with: sudo apt install libmpv2" >&2
    exit 1
fi

echo "[AppImage] Bundling libmpv from $LIBMPV_PATH"
cp "$LIBMPV_PATH" "$APPDIR/usr/lib/libmpv.so.2"
REAL_LIBMPV="$(readlink -f "$LIBMPV_PATH")"
if [ "$REAL_LIBMPV" != "$LIBMPV_PATH" ]; then
    cp "$REAL_LIBMPV" "$APPDIR/usr/lib/$(basename "$REAL_LIBMPV")"
fi

# ── 3. Desktop file and icon ─────────────────────────────────────────────────
cp "$SCRIPT_DIR/BabelPlayer.desktop" "$APPDIR/BabelPlayer.desktop"
cp "$SCRIPT_DIR/BabelPlayer.desktop" "$APPDIR/usr/share/applications/BabelPlayer.desktop"

ICON_SRC="$REPO_ROOT/src/BabelPlayer.Assets/BabelPlayer.png"
if [ ! -f "$ICON_SRC" ]; then
    ICO_SRC="$REPO_ROOT/src/BabelPlayer.Assets/BabelPlayer.ico"
    if command -v convert &>/dev/null && [ -f "$ICO_SRC" ]; then
        convert "${ICO_SRC}[0]" "$REPO_ROOT/src/BabelPlayer.Assets/BabelPlayer.png"
        ICON_SRC="$REPO_ROOT/src/BabelPlayer.Assets/BabelPlayer.png"
    else
        python3 -c "
import struct, zlib
def png_chunk(tag, data):
    return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)
w, h = 256, 256
ihdr = struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)
raw = b''.join(b'\\x00' + bytes([30, 100, 170] * w) for _ in range(h))
idat = zlib.compress(raw)
data = b'\\x89PNG\\r\\n\\x1a\\n' + png_chunk(b'IHDR', ihdr) + png_chunk(b'IDAT', idat) + png_chunk(b'IEND', b'')
open('$REPO_ROOT/src/BabelPlayer.Assets/BabelPlayer.png', 'wb').write(data)
"
        ICON_SRC="$REPO_ROOT/src/BabelPlayer.Assets/BabelPlayer.png"
    fi
fi

cp "$ICON_SRC" "$APPDIR/babel-player.png"
cp "$ICON_SRC" "$APPDIR/usr/share/icons/hicolor/256x256/apps/babel-player.png"

# ── 4. AppRun entrypoint ─────────────────────────────────────────────────────
cat > "$APPDIR/AppRun" << 'EOF'
#!/usr/bin/env bash
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export LD_LIBRARY_PATH="$HERE/usr/lib:${LD_LIBRARY_PATH:-}"
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/babel-player"
exec "$HERE/usr/bin/BabelPlayer.Avalonia" "$@"
EOF
chmod +x "$APPDIR/AppRun"

# ── 5. Download appimagetool for the correct arch if not present ────────────────
APPIMAGETOOL="$(command -v appimagetool || true)"
if [ -z "$APPIMAGETOOL" ]; then
    echo "[AppImage] Downloading appimagetool for $ARCH..."
    APPIMAGETOOL="/tmp/appimagetool-${ARCH}"
    curl -fsSL -o "$APPIMAGETOOL" \
        "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-${ARCH}.AppImage"
    chmod +x "$APPIMAGETOOL"
fi

# ── 6. Build AppImage ────────────────────────────────────────────────────────
mkdir -p "$OUTPUT_DIR"
OUTPUT_FILE="$OUTPUT_DIR/BabelPlayer-${VERSION}-${RUNTIME_SUFFIX}.AppImage"

echo "[AppImage] Running appimagetool (ARCH=$ARCH)..."
ARCH=$ARCH "$APPIMAGETOOL" "$APPDIR" "$OUTPUT_FILE"
chmod +x "$OUTPUT_FILE"

echo "[AppImage] Done: $OUTPUT_FILE"
echo "  Size: $(du -sh "$OUTPUT_FILE" | cut -f1)"
