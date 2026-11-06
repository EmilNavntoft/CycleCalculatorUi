#!/usr/bin/env bash
# Installs CycleCalculator for the current user (no root needed).
# Run from the unpacked tarball: ./install.sh     Remove again: ./install.sh --uninstall
set -euo pipefail

APP_DIR="$HOME/.local/share/cyclecalculator"
BIN_LINK="$HOME/.local/bin/cyclecalculator"
DESKTOP_FILE="$HOME/.local/share/applications/cyclecalculator.desktop"

if [[ "${1:-}" == "--uninstall" ]]; then
    rm -rf "$APP_DIR" "$BIN_LINK" "$DESKTOP_FILE"
    echo "CycleCalculator removed."
    exit 0
fi

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

rm -rf "$APP_DIR"
mkdir -p "$APP_DIR" "$(dirname "$BIN_LINK")" "$(dirname "$DESKTOP_FILE")"
cp -r "$SRC/." "$APP_DIR/"
rm -f "$APP_DIR/install.sh"
ln -sf "$APP_DIR/CycleCalculatorUi.Desktop" "$BIN_LINK"

cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=CycleCalculator
Comment=Editor and solver for refrigeration and heat-pump cycles
Exec=$APP_DIR/CycleCalculatorUi.Desktop
Icon=$APP_DIR/wwwroot/favicon.png
Terminal=false
Categories=Science;Engineering;
EOF

command -v update-desktop-database >/dev/null && update-desktop-database "$(dirname "$DESKTOP_FILE")" || true

if ! ldconfig -p 2>/dev/null | grep 'libwebkit2gtk-4.1' >/dev/null; then
    echo "warning: webkit2gtk-4.1 not found. Install it first:" >&2
    echo "  Debian/Ubuntu: sudo apt install libwebkit2gtk-4.1-0" >&2
    echo "  Fedora:        sudo dnf install webkit2gtk4.1" >&2
    echo "  Arch:          sudo pacman -S webkit2gtk-4.1" >&2
fi

echo "Installed to $APP_DIR"
echo "Start it from your app menu or run: cyclecalculator"
