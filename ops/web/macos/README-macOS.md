# Grunflex POS Web — macOS (Catalina 10.15+)

## Instalación (recomendado)

En Macs antiguos el `.dmg` generado en Windows a veces muestra **"imagen no reconocida"**.
Use el **ZIP** o el **ISO**:

1. Instale **Firefox ESR**: https://www.mozilla.org/firefox/enterprise/
2. Abra `GrunflexPOS-Web-macOS-Catalina.zip` (o el `.iso`)
3. Doble clic en **Instalar en Aplicaciones.command**  
   (si macOS bloquea: clic derecho → Abrir)
4. La app queda en Aplicaciones y se abre sola

También puede arrastrar `Grunflex POS Web.app` a Aplicaciones manualmente.

## Arranque

```bash
open -a "Grunflex POS Web"
```

Se abre en Firefox ESR. Web: http://127.0.0.1:7379

## Requisitos

- macOS 10.15 Catalina o superior
- Mac Intel (x64) o Apple Silicon con Rosetta 2
- Firefox ESR en `/Applications`
- Puertos 7379 y 7279 libres

## Compilar (desarrollo)

```powershell
powershell -ExecutionPolicy Bypass -File ops/web/compile-macos-installer.ps1
powershell -ExecutionPolicy Bypass -File ops/web/compile-macos-dmg.ps1
```
