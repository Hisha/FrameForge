#!/usr/bin/env bash
set -euo pipefail

SOURCE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

if [[ -n "${XDG_DATA_HOME:-}" ]]; then
    DATA_DIR="$XDG_DATA_HOME"
elif [[ -n "${HOME:-}" ]]; then
    DATA_DIR="$HOME/.local/share"
else
    echo 'HOME or XDG_DATA_HOME must be set for a per-user installation.' >&2
    exit 1
fi

[[ "$DATA_DIR" == /* ]] || { echo 'XDG_DATA_HOME must be an absolute path.' >&2; exit 1; }
[[ "$DATA_DIR" != *$'\n'* && "$DATA_DIR" != *$'\r'* ]] || {
    echo 'Installation path cannot contain newlines.' >&2
    exit 1
}

DEST="$DATA_DIR/FrameForge-app"
APPS="$DATA_DIR/applications"
DESKTOP_FILE="$APPS/frameforge.desktop"
MARKER=".frameforge-install"

[[ "$SOURCE" != "$DEST" ]] || {
    echo 'Run install.sh from a newly extracted FrameForge ZIP, not from the installed application directory.' >&2
    exit 1
}
[[ "$(uname -m)" == x86_64 ]] || {
    echo 'FrameForge requires Linux x86_64.' >&2
    exit 1
}
[[ -f "$SOURCE/FrameForge" ]] || {
    echo "FrameForge executable is missing beside install.sh: $SOURCE/FrameForge" >&2
    exit 1
}
[[ -f "$SOURCE/frameforge-icon.png" ]] || {
    echo "FrameForge icon is missing beside install.sh: $SOURCE/frameforge-icon.png" >&2
    exit 1
}

chmod +x "$SOURCE/FrameForge" "$SOURCE/install.sh"

if [[ -e "$DEST" && ! -f "$DEST/$MARKER" ]]; then
    echo "Refusing to replace an unmanaged folder: $DEST" >&2
    exit 1
fi

mkdir -p "$DATA_DIR" "$APPS"
STAGE="$(mktemp -d "$DATA_DIR/.frameforge-install.XXXXXX")"
trap 'rm -rf -- "$STAGE"' EXIT
cp -a "$SOURCE/." "$STAGE/"
touch "$STAGE/$MARKER"
chmod +x "$STAGE/FrameForge" "$STAGE/install.sh"

# Stage the complete self-contained payload before replacing an existing install.
# FrameForge settings, extracted WoW assets, and user projects live outside
# FrameForge-app and are deliberately not read, copied, or removed here.
BACKUP=""
if [[ -d "$DEST" ]]; then
    BACKUP="$(mktemp -d "$DATA_DIR/.frameforge-old.XXXXXX")"
    rmdir "$BACKUP"
    mv -- "$DEST" "$BACKUP"
fi
if ! mv -- "$STAGE" "$DEST"; then
    [[ -z "$BACKUP" ]] || mv -- "$BACKUP" "$DEST"
    exit 1
fi

# Desktop Entry value escaping followed by Exec argument escaping. Quoting the
# executable makes installations below paths containing spaces work correctly.
escape_value() { local value="$1"; value="${value//\\/\\\\}"; printf '%s' "$value"; }
exec_path="$DEST/FrameForge"
exec_path="${exec_path//\\/\\\\}"
exec_path="${exec_path//\"/\\\"}"
exec_path="${exec_path//\$/\\\$}"
exec_path="${exec_path//\`/\\\`}"
exec_path="${exec_path//%/%%}"
{
    printf '[Desktop Entry]\nType=Application\nName=FrameForge\n'
    printf 'Comment=Visual layout editor for WoW 3.3.5a user interfaces\n'
    printf 'Exec=%s\n' "$(escape_value "\"$exec_path\"")"
    printf 'Icon=%s\n' "$(escape_value "$DEST/frameforge-icon.png")"
    printf 'Terminal=false\nCategories=Development;GUIDesigner;\nStartupNotify=true\n'
} > "$DESKTOP_FILE"

command -v update-desktop-database >/dev/null &&
    update-desktop-database "$APPS" >/dev/null 2>&1 || true

[[ -z "$BACKUP" ]] || rm -rf -- "$BACKUP"
printf 'Installed FrameForge to %s\n' "$DEST"
printf 'Desktop launcher: %s\n' "$DESKTOP_FILE"
printf 'Open FrameForge from your application menu. Existing settings, caches, assets, and projects were not changed.\n'
