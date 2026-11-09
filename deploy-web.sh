#!/usr/bin/env bash
# Builds the web app and deploys it to the server, then restarts it.
# Usage: ./deploy-web.sh root@SERVER_IP     (or set CYCLECALC_SERVER=root@SERVER_IP)
# Expects the one-time server setup to be done: cyclecalc user, systemd service
# "cyclecalculator" running /opt/cyclecalculator/app, and Caddy in front.
set -euo pipefail

SERVER="${1:-${CYCLECALC_SERVER:-}}"
if [[ -z "$SERVER" ]]; then
    echo "usage: $0 root@SERVER_IP   (or set CYCLECALC_SERVER)" >&2
    exit 1
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$ROOT/publish/web"
APP_DIR="/opt/cyclecalculator/app"

for tool in dotnet rsync ssh; do
    command -v "$tool" >/dev/null || { echo "error: $tool not found" >&2; exit 1; }
done

echo "==> Building"
rm -rf "$OUT"
dotnet publish "$ROOT/CycleCalculatorUi.Web" \
    -c Release -r linux-x64 --self-contained \
    -p:DebugType=none -p:AllowMissingPrunePackageData=true \
    -o "$OUT"

if [[ ! -f "$OUT/libCoolPropCsharp.so" ]]; then
    echo "error: libCoolPropCsharp.so missing from $OUT; the solver would not work" >&2
    exit 1
fi

echo "==> Uploading to $SERVER"
rsync -az --delete "$OUT/" "$SERVER:$APP_DIR/"

echo "==> Restarting"
ssh "$SERVER" bash -s <<EOF
set -e
chown -R cyclecalc:cyclecalc /opt/cyclecalculator
chmod +x $APP_DIR/CycleCalculatorUi.Web
systemctl restart cyclecalculator
# Give the app a few seconds to start, then check it answers.
for i in \$(seq 1 20); do
    if curl -sf -o /dev/null http://127.0.0.1:5000/; then
        echo "App is up."
        exit 0
    fi
    sleep 1
done
echo "App did not respond after restart. Last log lines:" >&2
journalctl -u cyclecalculator -n 30 --no-pager >&2
exit 1
EOF

echo
echo "Deployed."
