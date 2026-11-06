#!/usr/bin/env bash
# Publishes the desktop app as a self-contained Linux build and packs it as a tarball.
# Usage: ./publish-linux.sh [runtime-id]   (default: linux-x64)
# Output: publish/<rid>/ and publish/CycleCalculator-<rid>.tar.gz
set -euo pipefail

RID="${1:-linux-x64}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$ROOT/publish/$RID"
TARBALL="$ROOT/publish/CycleCalculator-$RID.tar.gz"

rm -rf "$OUT" "$TARBALL"

dotnet publish "$ROOT/CycleCalculatorUi.Desktop" \
    -c Release -r "$RID" --self-contained \
    -p:DebugType=none \
    -o "$OUT"

# CoolProp only ships an x64 native library in the backend submodule.
if [[ ! -f "$OUT/libCoolPropCsharp.so" ]]; then
    echo "warning: libCoolPropCsharp.so missing from $OUT; the solver will not work" >&2
fi

cp "$ROOT/packaging/linux/install.sh" "$OUT/install.sh"
chmod +x "$OUT/install.sh" "$OUT/CycleCalculatorUi.Desktop"

tar -czf "$TARBALL" -C "$ROOT/publish" --transform "s,^$RID,CycleCalculator," "$RID"

echo
echo "Published to: $OUT"
echo "Tarball:      $TARBALL"
echo "Users unpack it and run ./install.sh (needs webkit2gtk-4.1 installed)."
