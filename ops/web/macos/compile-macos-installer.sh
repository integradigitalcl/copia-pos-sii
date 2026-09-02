#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
STAGING="$ROOT/artifacts/macos-installer"
WEB_OUT="$STAGING/web"
API_OUT="$STAGING/API"
OUT_ZIP="$ROOT/ops/web/out/GrunflexPOS-Web-macOS-Catalina.zip"

rm -rf "$STAGING"
mkdir -p "$WEB_OUT" "$API_OUT" "$(dirname "$OUT_ZIP")"

echo "Publicando web (Release, osx-x64, self-contained)..."
dotnet publish "$ROOT/GrunflexPOS.Web/GrunflexPOS.Web.csproj" -c Release -r osx-x64 --self-contained true -o "$WEB_OUT"

echo "Publicando API (Release, osx-x64, self-contained)..."
dotnet publish "$ROOT/GrunflexPOS.API/GrunflexPOS.API.csproj" -c Release -r osx-x64 --self-contained true -o "$API_OUT"

for path in "$API_OUT/appsettings.local.json" "$API_OUT/appsettings.Development.json"; do
  [[ -f "$path" ]] && rm -f "$path"
done

cp "$SCRIPT_DIR"/*.sh "$STAGING/"
cp "$SCRIPT_DIR/README-macOS.md" "$STAGING/"
chmod +x "$STAGING"/*.sh

cd "$STAGING"
rm -f "$OUT_ZIP"
zip -r "$OUT_ZIP" . -x "*.pdb"
echo "Paquete macOS listo: $OUT_ZIP"
