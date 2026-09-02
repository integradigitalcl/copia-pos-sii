#!/usr/bin/env bash
# Shared paths and helpers for Grunflex POS Web on macOS (10.15 Catalina+).
set -euo pipefail

APP_NAME="Grunflex POS Web"
DEFAULT_INSTALL_DIR="/Applications/Grunflex POS Web"
WEB_PORT="${WEB_PORT:-7379}"
API_PORT="${API_PORT:-7279}"
API_LABEL="com.grunflexpos.api"

grunflex_local_data() {
  echo "${HOME}/Library/Application Support/GrunflexPOS"
}

grunflex_shared_data() {
  local shared="/Library/Application Support/GrunflexPOS"
  if mkdir -p "$shared/config" "$shared/data" 2>/dev/null; then
    echo "$shared"
    return 0
  fi
  # Sin permisos de admin: usar carpeta del usuario.
  echo "$(grunflex_local_data)"
}

grunflex_credentials_paths() {
  echo "$(grunflex_shared_data)/config/initial-admin-credentials.txt"
  echo "$(grunflex_local_data)/initial-admin-credentials.txt"
}

show_credentials_hint() {
  while IFS= read -r path; do
    [[ -f "$path" ]] || continue
    echo ""
    echo "Credenciales iniciales del administrador ($path):"
    cat "$path"
    echo "Elimine el archivo tras anotar la contraseña."
    return 0
  done < <(grunflex_credentials_paths)
  return 1
}

wait_http_ok() {
  local url="$1"
  local seconds="${2:-20}"
  local deadline=$(( $(date +%s) + seconds ))
  while [[ $(date +%s) -lt $deadline ]]; do
    if curl -fsS --max-time 2 "$url" >/dev/null 2>&1; then
      return 0
    fi
    sleep 0.4
  done
  return 1
}

resolve_app_root() {
  local script_dir
  script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  if [[ -f "$script_dir/web/GrunflexPOS.Web" || -f "$script_dir/web/GrunflexPOS.Web.dll" ]]; then
    echo "$script_dir"
    return 0
  fi
  if [[ -f "$script_dir/../Resources/web/GrunflexPOS.Web" ]]; then
    echo "$(cd "$script_dir/../Resources" && pwd)"
    return 0
  fi
  if [[ -d "/Applications/Grunflex POS Web.app/Contents/Resources" ]]; then
    echo "/Applications/Grunflex POS Web.app/Contents/Resources"
    return 0
  fi
  if [[ -d "$DEFAULT_INSTALL_DIR" ]]; then
    echo "$DEFAULT_INSTALL_DIR"
    return 0
  fi
  echo "$script_dir"
}

# Edita Multicaja/Licensing ApiBaseUrl sin depender de python3 (Catalina+).
set_json_multicaja_url() {
  local settings="$1"
  local api_url="$2"
  if [[ ! -f "$settings" ]]; then
    echo "No existe $settings" >&2
    return 1
  fi
  if [[ ! -w "$settings" ]]; then
    echo "No se puede escribir $settings (¿está en un DMG de solo lectura? Arrastre la app a Aplicaciones)." >&2
    return 1
  fi

  if command -v python3 >/dev/null 2>&1; then
    python3 - "$settings" "$api_url" <<'PY'
import json, sys
path, api = sys.argv[1], sys.argv[2]
with open(path, "r", encoding="utf-8") as f:
    data = json.load(f)
data.setdefault("Multicaja", {})["Enabled"] = True
data["Multicaja"]["ApiBaseUrl"] = api
data.setdefault("Licensing", {})["ApiBaseUrl"] = api
with open(path, "w", encoding="utf-8") as f:
    json.dump(data, f, indent=2, ensure_ascii=False)
    f.write("\n")
PY
    return $?
  fi

  if command -v ruby >/dev/null 2>&1; then
    ruby - "$settings" "$api_url" <<'RUBY'
require "json"
path, api = ARGV[0], ARGV[1]
data = JSON.parse(File.read(path))
data["Multicaja"] ||= {}
data["Multicaja"]["Enabled"] = true
data["Multicaja"]["ApiBaseUrl"] = api
data["Licensing"] ||= {}
data["Licensing"]["ApiBaseUrl"] = api
File.write(path, JSON.pretty_generate(data) + "\n")
RUBY
    return $?
  fi

  # Fallback sed: estructura conocida de appsettings.json
  local tmp
  tmp="$(mktemp)"
  # shellcheck disable=SC2002
  cat "$settings" \
    | sed -E 's/("Multicaja"[[:space:]]*:[[:space:]]*\{)/\1\
    "__GF_MARK__": true,/' \
    | sed -E "s/(\"Enabled\"[[:space:]]*:[[:space:]]*)false/\\1true/" \
    | sed -E "s#(\"ApiBaseUrl\"[[:space:]]*:[[:space:]]*\")[^\"]*(\")#\\1${api_url}\\2#g" \
    | sed '/"__GF_MARK__"/d' >"$tmp"
  mv "$tmp" "$settings"
}

alert() {
  local msg="$1"
  local title="${2:-Grunflex POS Web}"
  osascript -e "display dialog \"$(printf '%s' "$msg" | sed 's/\\/\\\\/g; s/"/\\"/g')\" with title \"$title\" buttons {\"OK\"} default button 1" >/dev/null 2>&1 || true
}

# Abre el POS en Firefox ESR (Catalina / Mac antiguos). Evita Chrome reciente incompatible.
open_pos_browser() {
  local url="${1:-http://127.0.0.1:${WEB_PORT}}"
  local app path

  for app in "Firefox ESR" "Firefox"; do
    if [[ -d "/Applications/${app}.app" ]]; then
      if open -a "$app" "$url" 2>/dev/null; then
        echo "Abierto en ${app}: $url"
        return 0
      fi
    fi
  done

  for path in \
    "/Applications/Firefox ESR.app" \
    "/Applications/Firefox.app" \
    "$HOME/Applications/Firefox ESR.app" \
    "$HOME/Applications/Firefox.app"
  do
    if [[ -d "$path" ]]; then
      if open -a "$path" "$url" 2>/dev/null; then
        echo "Abierto en $path: $url"
        return 0
      fi
    fi
  done

  echo "No se encontró Firefox ESR. Instálelo desde https://www.mozilla.org/firefox/enterprise/" >&2
  alert "No se encontró Firefox ESR. Instale Firefox ESR e intente de nuevo. URL: $url"
  return 1
}
