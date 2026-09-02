#!/bin/bash
# Copia Grunflex POS Web a /Applications (compatible Catalina; no requiere DMG).
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
APP_SRC=""
for candidate in \
  "$HERE/Grunflex POS Web.app" \
  "$HERE/../Grunflex POS Web.app"
do
  if [[ -d "$candidate" ]]; then
    APP_SRC="$candidate"
    break
  fi
done

if [[ -z "$APP_SRC" ]]; then
  osascript -e 'display dialog "No se encontro Grunflex POS Web.app junto a este instalador." with title "Grunflex POS Web" buttons {"OK"} default button 1' >/dev/null 2>&1 || true
  exit 1
fi

if ! osascript <<'EOF'
display dialog "Se instalara Grunflex POS Web en Aplicaciones." with title "Grunflex POS Web" buttons {"Cancelar", "Instalar"} default button 2
EOF
then
  exit 0
fi

DEST="/Applications/Grunflex POS Web.app"
rm -rf "$DEST"
cp -R "$APP_SRC" "$DEST"
chmod +x "$DEST/Contents/MacOS/GrunflexPOSWeb" 2>/dev/null || true
chmod +x "$DEST/Contents/Resources"/*.sh 2>/dev/null || true
chmod +x "$DEST/Contents/Resources/web/GrunflexPOS.Web" "$DEST/Contents/Resources/API/GrunflexPOS.API" 2>/dev/null || true
xattr -dr com.apple.quarantine "$DEST" 2>/dev/null || true

osascript <<'EOF' >/dev/null 2>&1 || true
display dialog "Instalacion lista. A continuacion se abrira Grunflex POS Web (use Firefox ESR)." with title "Grunflex POS Web" buttons {"OK"} default button 1
EOF

open "$DEST"
