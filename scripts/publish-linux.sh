#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="linux-x64"
DIST="$ROOT/dist"
PROJECT="$ROOT/src/FrameForge.Desktop/FrameForge.Desktop.csproj"
command -v dotnet >/dev/null 2>&1 || {
    echo "ERROR: dotnet was not found in PATH." >&2
    exit 1
}

command -v zip >/dev/null 2>&1 || {
    echo "ERROR: zip was not found. Install it before creating the release archive." >&2
    exit 1
}

VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version)"
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
cp "$ROOT/assets/branding/frameforge-icon.png" "$PACKAGE_DIR/frameforge-icon.png" 2>/dev/null || true

# Remove development-only files
find "$PACKAGE_DIR" -type f \( -name '*.pdb' -o -name '*.Development.json' \) -delete

echo "Packaging $ARCHIVE..."
(cd "$DIST" && zip -r "$(basename "$ARCHIVE")" "$(basename "$PACKAGE_DIR")" >/dev/null)

rm -rf "$PACKAGE_DIR"

echo "Created $ARCHIVE"
ls -lh "$ARCHIVE"
