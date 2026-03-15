#!/usr/bin/env bash
# AppImageBuilder.sh
# Packages the published linux-x64 output into a self-contained AppImage.
#
# Prerequisites (installed automatically by the CI job, or manually for local builds):
#   - appimagetool  (downloaded from GitHub Releases)
#   - libmpv2 / libmpv.so.2  (apt install libmpv2)
#
# Usage:
#   bash linux/AppImageBuilder.sh <publish_dir> <output_dir> <version>
#
# Example:
#   bash linux/AppImageBuilder.sh artifacts/publish/linux-x64 artifacts v1.0.0

set -euo pipefail

PUBLISH_DIR="${1:?Usage: $0 <publish_dir> <output_dir> <version>}"
OUTPUT_DIR="${2:?Usage: $0 <publish_dir> <output_dir> <version>}"
VERSION="${3:?Usage: $0 <publish_dir> <output_dir> <version>}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

APPDIR="$(mktemp -d)/BabelPlayer.AppDir"
mkdir -p "$APPDIR"

echo "[AppImage] Building AppDir at $APPDIR ..."

# ── 1. Copy published output ─────────────────────────────────────────────────
mkdir -p "$APPDIR/usr/bin"
mkdir -p "$APPDIR/usr/lib"
mkdir -p "$APPDIR/usr/share/applications"
mkdir -p "$APPDIR/usr/share/icons/hicolor/256x256/apps"

cp -r "$PUBLISH_DIR/"* "$APPDIR/usr/bin/"

# ── 2. Bundle libmpv ─────────────────────────────────────────────────────────
# Locate libmpv.so.2 on the build machine and bundle it so the AppImage is
# self-contained (users don't need libmpv installed).
LIBMPV_PATH=""
for candidate in \
    /usr/lib/x86_64-linux-gnu/libmpv.so.2 \
    /usr/lib/libmpv.so.2 \
    /usr/local/lib/libmpv.so.2; do
    if [ -f "$candidate" ]; then
        LIBMPV_PATH="$candidate"
        break
    fi
done

if [ -z "$LIBMPV_PATH" ]; then
    # Try ldconfig as fallback
    LIBMPV_PATH=$(ldconfig -p 2>/dev/null | grep 'libmpv.so.2' | awk '{print $NF}' | head -1 || true)
fi

if [ -z "$LIBMPV_PATH" ] || [ ! -f "$LIBMPV_PATH" ]; then
    echo "[AppImage] ERROR: libmpv.so.2 not found. Install with: sudo apt install libmpv2" >&2
    exit 1
fi

echo "[AppImage] Bundling libmpv from $LIBMPV_PATH"
cp "$LIBMPV_PATH" "$APPDIR/usr/lib/libmpv.so.2"
# Also copy any libmpv.so symlink target if needed
REAL_LIBMPV="$(readlink -f "$LIBMPV_PATH")"
if [ "$REAL_LIBMPV" != "$LIBMPV_PATH" ]; then
    cp "$REAL_LIBMPV" "$APPDIR/usr/lib/$(basename "$REAL_LIBMPV")"
fi

# ── 3. Desktop file and icon ─────────────────────────────────────────────────
cp "$SCRIPT_DIR/BabelPlayer.desktop" "$APPDIR/BabelPlayer.desktop"
cp "$SCRIPT_DIR/BabelPlayer.desktop" "$APPDIR/usr/share/applications/BabelPlayer.desktop"

# Use the .ico converted to PNG, or fall back to a placeholder
ICON_SRC="$REPO_ROOT/src/BabelPlayer.Assets/BabelPlayer.png"
if [ ! -f "$ICON_SRC" ]; then
    # Convert .ico to .png using ImageMagick if available
    ICO_SRC="$REPO_ROOT/src/BabelPlayer.Assets/BabelPlayer.ico"
    if command -v convert &>/dev/null && [ -f "$ICO_SRC" ]; then
        convert "${ICO_SRC}[0]" "$REPO_ROOT/src/BabelPlayer.Assets/BabelPlayer.png"
        ICON_SRC="$REPO_ROOT/src/BabelPlayer.Assets/BabelPlayer.png"
    else
        # Create a minimal 256x256 placeholder PNG using Python
        python3 -c "
import struct, zlib, base64
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

# ── 5. Download appimagetool if not present ───────────────────────────────────
APPIMAGETOOL="$(command -v appimagetool || true)"
if [ -z "$APPIMAGETOOL" ]; then
    echo "[AppImage] Downloading appimagetool..."
    APPIMAGETOOL="/tmp/appimagetool"
    curl -fsSL -o "$APPIMAGETOOL" \
        "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
    chmod +x "$APPIMAGETOOL"
fi

# ── 6. Build AppImage ────────────────────────────────────────────────────────
mkdir -p "$OUTPUT_DIR"
OUTPUT_FILE="$OUTPUT_DIR/BabelPlayer-${VERSION}-linux-x64.AppImage"

echo "[AppImage] Running appimagetool..."
ARCH=x86_64 "$APPIMAGETOOL" "$APPDIR" "$OUTPUT_FILE"
chmod +x "$OUTPUT_FILE"

echo "[AppImage] Done: $OUTPUT_FILE"
echo "  Size: $(du -sh "$OUTPUT_FILE" | cut -f1)"
