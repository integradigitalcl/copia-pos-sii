#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

USER_NAME="${1:-admin}"
PASSWORD="${2:-}"
DISPLAY_NAME="${3:-Administrador}"
DB="${GRUNFLEX_DB:-$(grunflex_shared_data)/data/grunflex.db}"

if [[ ! -f "$DB" ]]; then
  echo "No se encontró la base multicaja: $DB" >&2
  exit 1
fi

if [[ -z "$PASSWORD" ]]; then
  while IFS= read -r path; do
    [[ -f "$path" ]] || continue
    PASSWORD="$(awk -F': ' '/Contrase(n|ñ)a:/ {print $2; exit}' "$path" | tr -d '\r')"
    [[ -n "$PASSWORD" ]] && break
  done < <(grunflex_credentials_paths)
fi

if [[ -z "$PASSWORD" ]]; then
  PASSWORD="$(openssl rand -base64 12 | tr -d '/+=' | head -c 16)"
fi

WEB_DB="$(grunflex_local_data)/grunflex-pos.db"
SEED_ARGS=("$DB" "$USER_NAME" "$PASSWORD" "$DISPLAY_NAME")
[[ -f "$WEB_DB" ]] && SEED_ARGS+=("--web-db" "$WEB_DB")

dotnet run --project "$ROOT/ops/multicaja/SeedAdmin" -- "${SEED_ARGS[@]}"

CONTENT="Grunflex POS — credenciales iniciales
Generado: $(date '+%Y-%m-%d %H:%M')
Equipo: $(hostname)

Usuario: ${USER_NAME}
Contraseña: ${PASSWORD}

Use estas credenciales para ingresar al POS Web con multicaja activa."

mkdir -p "$(grunflex_shared_data)/config" "$(grunflex_local_data)"
printf '%s\n' "$CONTENT" > "$(grunflex_shared_data)/config/initial-admin-credentials.txt"
printf '%s\n' "$CONTENT" > "$(grunflex_local_data)/initial-admin-credentials.txt"
echo "$CONTENT"
