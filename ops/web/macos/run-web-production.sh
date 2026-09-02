#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

APP_ROOT="$(resolve_app_root)"
WEB_DIR="$APP_ROOT/web"
API_DIR="$APP_ROOT/API"
WEB_URL="http://127.0.0.1:${WEB_PORT}"
API_URL="http://127.0.0.1:${API_PORT}"

ensure_api() {
  if wait_http_ok "${API_URL}/health/live" 2; then
    echo "API multicaja OK en ${API_URL}"
    return 0
  fi

  local api_bin="$API_DIR/GrunflexPOS.API"
  [[ -x "$api_bin" ]] || api_bin="$API_DIR/GrunflexPOS.API.exe"
  if [[ ! -x "$api_bin" && ! -f "$api_bin" ]]; then
    echo "Advertencia: no se encontró la API en $API_DIR" >&2
    return 1
  fi

  if launchctl list 2>/dev/null | grep -q "$API_LABEL"; then
    launchctl kickstart -k "gui/$(id -u)/$API_LABEL" 2>/dev/null || true
  else
    echo "Iniciando API multicaja en segundo plano..."
    nohup "$api_bin" --urls "${API_URL}/" >/tmp/grunflex-api.log 2>&1 &
  fi

  if wait_http_ok "${API_URL}/health/live" 25; then
    echo "API multicaja OK en ${API_URL}"
  else
    echo "Advertencia: la API no respondió a tiempo. Revise /tmp/grunflex-api.log" >&2
  fi
}

start_web() {
  local web_bin="$WEB_DIR/GrunflexPOS.Web"
  [[ -x "$web_bin" ]] || web_bin="$WEB_DIR/GrunflexPOS.Web.exe"
  if [[ ! -x "$web_bin" && ! -f "$web_bin" ]]; then
    echo "No se encontró GrunflexPOS.Web en $WEB_DIR" >&2
    exit 1
  fi

  echo "Iniciando Grunflex POS Web en ${WEB_URL}"
  if pgrep -f "GrunflexPOS.Web" >/dev/null 2>&1; then
    echo "El POS Web ya está en ejecución."
  else
    chmod +x "$web_bin" 2>/dev/null || true
    nohup "$web_bin" --urls "${WEB_URL}" --environment Production >/tmp/grunflex-web.log 2>&1 &
    echo "PID web $!"
  fi

  if wait_http_ok "${WEB_URL}/health" 25; then
    echo "Web OK en ${WEB_URL}"
  else
    echo "Advertencia: la web no respondió /health a tiempo. Revise /tmp/grunflex-web.log" >&2
  fi
}

ensure_api || true
start_web
show_credentials_hint || true
open_pos_browser "${WEB_URL}" || true
