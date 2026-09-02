#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

ROLE="${1:-server}"
APP_ROOT="${2:-$(resolve_app_root)}"
WEB_SETTINGS="$APP_ROOT/web/appsettings.json"
API_URL="http://127.0.0.1:${API_PORT}/"

if [[ ! -f "$WEB_SETTINGS" ]]; then
  echo "No se encontró $WEB_SETTINGS" >&2
  exit 1
fi

mkdir -p "$(grunflex_local_data)/config" "$(grunflex_local_data)/data" 2>/dev/null || true
# Intenta carpeta compartida; si falla, grunflex_shared_data ya cae al usuario.
mkdir -p "$(grunflex_shared_data)/config" "$(grunflex_shared_data)/data" 2>/dev/null || true

if [[ "$ROLE" == "server" ]]; then
  set_json_multicaja_url "$WEB_SETTINGS" "$API_URL"
  echo "Multicaja configurada para caja principal: $API_URL"

  local_plist="$HOME/Library/LaunchAgents/${API_LABEL}.plist"
  mkdir -p "$HOME/Library/LaunchAgents"
  api_bin="$APP_ROOT/API/GrunflexPOS.API"
  cat > "$local_plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>${API_LABEL}</string>
  <key>ProgramArguments</key>
  <array>
    <string>${api_bin}</string>
    <string>--urls</string>
    <string>${API_URL}</string>
  </array>
  <key>WorkingDirectory</key>
  <string>${APP_ROOT}/API</string>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <true/>
  <key>StandardOutPath</key>
  <string>/tmp/grunflex-api.log</string>
  <key>StandardErrorPath</key>
  <string>/tmp/grunflex-api.log</string>
</dict>
</plist>
PLIST
  launchctl bootout "gui/$(id -u)/${API_LABEL}" 2>/dev/null || true
  launchctl bootstrap "gui/$(id -u)" "$local_plist" 2>/dev/null || true
  launchctl enable "gui/$(id -u)/${API_LABEL}" 2>/dev/null || true
  launchctl kickstart -k "gui/$(id -u)/${API_LABEL}" 2>/dev/null || true
  echo "Servicio API registrado en launchd (${API_LABEL})."
  exit 0
fi

# client — no escanear toda la LAN en el primer arranque (tardaría minutos).
discovered=""
if [[ "${FORCE_DISCOVER:-0}" == "1" ]] && [[ -x "$SCRIPT_DIR/discover-multicaja-server.sh" ]]; then
  discovered="$("$SCRIPT_DIR/discover-multicaja-server.sh" || true)"
fi
if [[ -n "$discovered" ]]; then
  set_json_multicaja_url "$WEB_SETTINGS" "$discovered"
  echo "Caja principal detectada: $discovered"
else
  # Deja Enabled=true apuntando a vacío local; el usuario configura en la UI.
  set_json_multicaja_url "$WEB_SETTINGS" "http://127.0.0.1:${API_PORT}/" || true
  echo "Caja adicional lista. Configure la URL de la caja principal en Configuración → Cajas."
fi
