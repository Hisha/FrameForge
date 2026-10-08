#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="linux-x64"
DIST="$ROOT/dist"
PROJECT="$ROOT/src/FrameForge.Desktop/FrameForge.Desktop.csproj"
ICON_PNG="$ROOT/assets/branding/frameforge-icon.png"
command -v dotnet >/dev/null 2>&1 || {
    echo "ERROR: dotnet was not found in PATH." >&2
    exit 1
}

command -v zip >/dev/null 2>&1 || {
    echo "ERROR: zip was not found. Install it before creating the release archive." >&2
    exit 1
}

command -v zipinfo >/dev/null 2>&1 || {
    echo "ERROR: zipinfo was not found. Install the unzip package before creating the release archive." >&2
    exit 1
}

validate_png_icon() {
    local path="$1"
    local raw width height color_type
    [[ -s "$path" ]] || { echo "ERROR: application icon is missing or empty: $path" >&2; exit 1; }
    raw="$(od -An -v -tu1 -N26 "$path" | tr '\n' ' ')"
    local -a bytes
    read -r -a bytes <<< "$raw"
    [[ ${#bytes[@]} -eq 26 &&
       ${bytes[0]} -eq 137 && ${bytes[1]} -eq 80 && ${bytes[2]} -eq 78 && ${bytes[3]} -eq 71 &&
       ${bytes[4]} -eq 13 && ${bytes[5]} -eq 10 && ${bytes[6]} -eq 26 && ${bytes[7]} -eq 10 ]] || {
        echo "ERROR: application icon is not a valid PNG: $path" >&2
        exit 1
    }
    width=$(( (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19] ))
    height=$(( (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23] ))
    color_type=${bytes[25]}
    [[ $width -eq 512 && $height -eq 512 ]] || {
        echo "ERROR: Linux icon must be 512x512; got ${width}x${height}: $path" >&2
        exit 1
    }
    [[ $color_type -eq 4 || $color_type -eq 6 ]] || {
        echo "ERROR: Linux icon PNG must carry an alpha channel; color type is $color_type: $path" >&2
        exit 1
    }
}

validate_png_icon "$ICON_PNG"

VERSION="${FRAMEFORGE_VERSION:-$(dotnet msbuild "$PROJECT" -getProperty:Version)}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.+-][A-Za-z0-9.-]+)?$ ]] || { echo "Invalid project version: $VERSION" >&2; exit 1; }
PACKAGE_DIR="$DIST/FrameForge-$VERSION-$RID"
ARCHIVE="$DIST/FrameForge-$VERSION-$RID.zip"

echo "Publishing FrameForge $VERSION for $RID..."
rm -rf "$PACKAGE_DIR" "$ARCHIVE"
mkdir -p "$PACKAGE_DIR"

dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -o "$PACKAGE_DIR"

# Main executable name matches project (FrameForge.Desktop produces FrameForge.Desktop; rename to FrameForge for usability)
if [[ -f "$PACKAGE_DIR/FrameForge.Desktop" ]]; then
    mv "$PACKAGE_DIR/FrameForge.Desktop" "$PACKAGE_DIR/FrameForge"
elif [[ -f "$PACKAGE_DIR/FrameForge.Desktop.bin" ]]; then
    mv "$PACKAGE_DIR/FrameForge.Desktop.bin" "$PACKAGE_DIR/FrameForge.bin"
    mv "$PACKAGE_DIR/FrameForge.Desktop" "$PACKAGE_DIR/FrameForge" 2>/dev/null || true
fi
chmod +x "$PACKAGE_DIR/FrameForge" "$PACKAGE_DIR/FrameForge.bin" 2>/dev/null || true

cp "$ROOT/README.md" "$PACKAGE_DIR/README.md" 2>/dev/null || true
cp "$ROOT/LICENSE" "$PACKAGE_DIR/LICENSE" 2>/dev/null || true
cp "$ROOT/THIRD_PARTY_NOTICES.md" "$PACKAGE_DIR/THIRD_PARTY_NOTICES.md" 2>/dev/null || true
mkdir -p "$PACKAGE_DIR/third_party"
cp "$ROOT/third_party/Nmpq.Standard-LICENSE.txt" "$PACKAGE_DIR/third_party/Nmpq.Standard-LICENSE.txt"
cp "$ICON_PNG" "$PACKAGE_DIR/frameforge-icon.png"
cp "$ROOT/scripts/install-linux.sh" "$PACKAGE_DIR/install.sh"
chmod +x "$PACKAGE_DIR/FrameForge" "$PACKAGE_DIR/install.sh"

# Remove development-only files
find "$PACKAGE_DIR" -type f \( -name '*.pdb' -o -name '*.Development.json' \) -delete

[[ -f "$PACKAGE_DIR/install.sh" ]] || {
    echo "ERROR: install.sh is missing from the Linux release payload." >&2
    exit 1
}
[[ -x "$PACKAGE_DIR/install.sh" ]] || {
    echo "ERROR: install.sh is not executable in the Linux release payload." >&2
    exit 1
}
[[ -x "$PACKAGE_DIR/FrameForge" ]] || {
    echo "ERROR: FrameForge is not executable in the Linux release payload." >&2
    exit 1
}

echo "Packaging $ARCHIVE..."
(cd "$DIST" && zip -r "$(basename "$ARCHIVE")" "$(basename "$PACKAGE_DIR")" >/dev/null)

INSTALL_ENTRY="$(basename "$PACKAGE_DIR")/install.sh"
ICON_ENTRY="$(basename "$PACKAGE_DIR")/frameforge-icon.png"
INSTALL_MODE="$(zipinfo -l "$ARCHIVE" "$INSTALL_ENTRY" | awk '$1 ~ /^-/ { print $1; exit }')"
[[ -n "$INSTALL_MODE" && "${INSTALL_MODE:3:1}" == x ]] || {
    echo "ERROR: install.sh is missing or not executable in the completed ZIP." >&2
    exit 1
}
zipinfo -1 "$ARCHIVE" | grep -Fqx "$ICON_ENTRY" || {
    echo "ERROR: frameforge-icon.png is missing from the completed ZIP." >&2
    exit 1
}

rm -rf "$PACKAGE_DIR"

echo "Created $ARCHIVE"
ls -lh "$ARCHIVE"
echo "Extract and run ./FrameForge, or bash install.sh for a per-user application-menu installation."
