#!/usr/bin/env bash
# Instalador Grunflex POS Web para macOS 10.15 Catalina o superior (Intel x64).
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

INSTALL_DIR="${INSTALL_DIR:-$DEFAULT_INSTALL_DIR}"
ROLE="${ROLE:-}"

echo "=============================================="
echo "  Grunflex POS Web — instalador macOS"
echo "  Requiere macOS 10.15 (Catalina) o superior"
echo "=============================================="
echo ""

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Este instalador solo funciona en macOS." >&2
  exit 1
fi

mac_ver="$(sw_vers -productVersion)"
major="${mac_ver%%.*}"
minor="$(echo "$mac_ver" | cut -d. -f2)"
if [[ "$major" -lt 10 ]] || { [[ "$major" -eq 10 ]] && [[ "$minor" -lt 15 ]]; }; then
  echo "Se requiere macOS 10.15 Catalina o superior (detectado: $mac_ver)." >&2
  exit 1
fi

arch="$(uname -m)"
if [[ "$arch" != "x86_64" ]]; then
  echo "Advertencia: este paquete es osx-x64 (Intel). En Apple Silicon use Rosetta 2 o solicite build arm64." >&2
fi

if [[ -z "$ROLE" ]]; then
  echo "Tipo de instalación:"
  echo "  1) Caja principal (POS Web + API multicaja)"
  echo "  2) Caja adicional (POS Web conectado por red)"
  read -r -p "Seleccione [1/2]: " choice
  ROLE="server"
  [[ "$choice" == "2" ]] && ROLE="client"
fi

echo "Instalando en: $INSTALL_DIR"
sudo mkdir -p "$INSTALL_DIR"
sudo rm -rf "$INSTALL_DIR/web" "$INSTALL_DIR/API"
sudo cp -R "$SCRIPT_DIR/web" "$INSTALL_DIR/"
sudo cp -R "$SCRIPT_DIR/API" "$INSTALL_DIR/"
sudo cp "$SCRIPT_DIR"/*.sh "$INSTALL_DIR/" 2>/dev/null || true
sudo chmod +x "$INSTALL_DIR"/*.sh
sudo mkdir -p "$(grunflex_shared_data)/config" "$(grunflex_shared_data)/data" "$(grunflex_local_data)"

# Acceso directo en Aplicaciones (script launcher)
LAUNCHER="$INSTALL_DIR/Iniciar Grunflex POS Web.command"
cat <<EOF | sudo tee "$LAUNCHER" >/dev/null
#!/usr/bin/env bash
cd "$INSTALL_DIR"
./run-web-production.sh
EOF
sudo chmod +x "$LAUNCHER"

echo "Aplicando perfil: $ROLE"
bash "$SCRIPT_DIR/apply-install-profile.sh" "$ROLE" "$INSTALL_DIR"

echo ""
echo "Instalación completada."
echo "  Abrir: $LAUNCHER"
echo "  Web:   http://127.0.0.1:${WEB_PORT}"
show_credentials_hint || echo "  Las credenciales se generan al primer arranque de la API."
echo ""
read -r -p "¿Iniciar Grunflex POS Web ahora? [s/N]: " start_now
if [[ "${start_now,,}" == "s" ]]; then
  bash "$INSTALL_DIR/run-web-production.sh"
fi
