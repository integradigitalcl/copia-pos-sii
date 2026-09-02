#!/usr/bin/env bash
set -euo pipefail

STAGING="${1:?staging dir}"
OUT_DIR="${2:?output dir}"
OUT_NAME="${3:-GrunflexPOS-Web-macOS-Catalina.dmg}"
APP="$STAGING/Grunflex POS Web.app"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ ! -d "$APP" ]]; then
  echo "No se encontró $APP" >&2
  exit 1
fi

mkdir -p "$OUT_DIR"
OUT="$OUT_DIR/$OUT_NAME"
rm -f "$OUT"

(cd "$SCRIPT_DIR/mkdragdmg" && go run . "$APP" "$OUT")
ls -lh "$OUT"
