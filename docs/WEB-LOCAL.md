# GrünFlex POS Web local

## Arquitectura

Chrome carga `http://127.0.0.1:7373`. `GrunflexPOS.Web` es un servidor Blazor Server con renderizado interactivo; cada caja mantiene su sesión de UI en un circuito aislado. El servidor usa `LocalPosDbContext` y EF Core SQLite. El esquema inicial crea catálogo, ventas y líneas de venta automáticamente.

`GrunflexPOS.HardwareBridge` es un proceso separado en `http://127.0.0.1:7390`. Solo el bridge habla con COM y con la API RAW de Windows. La aplicación web nunca expone esos dispositivos directamente al navegador.

## Desarrollo

```powershell
dotnet restore GrunflexPOS.slnx
powershell -ExecutionPolicy Bypass -File ops/web/run-web-dev.ps1
```

`dotnet watch` actualiza la UI sin recompilar un instalador. Para una caja de pruebas se puede iniciar el bridge en otra terminal con el script de su carpeta. Verifica `/health` en ambos puertos antes de probar hardware.

## Producción local

```powershell
# Compilar instalador (caja principal + adicional)
powershell -ExecutionPolicy Bypass -File ops/web/compile-web-installer.ps1

# O solo publicar artefactos sin .exe
powershell -ExecutionPolicy Bypass -File ops/web/compile-web-installer.ps1 -PublishOnly
```

El instalador ofrece dos tipos:

| Tipo | Incluye |
|------|---------|
| **Caja principal** | POS Web + Bridge + API multicaja (`:7279`) como servicio Windows + firewall |
| **Caja adicional** | POS Web + Bridge; **detecta la caja principal en la red** al instalar |

Todo va **autocontenido** (incluye .NET Runtime). No hace falta instalar .NET ni componentes extra en el PC.

Tras instalar, el acceso directo ejecuta `run-web-production.ps1` y abre Chrome en `http://127.0.0.1:7373`.

Para desarrollo sin instalador:

```powershell
powershell -ExecutionPolicy Bypass -File ops/web/run-web-production.ps1
```

## Datos y respaldos

- Base por defecto: `%LocalAppData%\GrunflexPOS\grunflex-pos.db`.
- Se puede definir `Data:DatabasePath` en `GrunflexPOS.Web/appsettings.json`.
- Para respaldar, detén el servidor y copia el archivo SQLite junto con sus archivos `-wal`/`-shm` si existen.
- No apuntes la web a la base WPF durante la validación. La migración de datos debe hacerse sobre una copia y con un plan de rollback.

## Flujos implementados

- Login local de prueba con roles Administrador y Cajero.
- Catálogo con búsqueda, categorías y lectura por código.
- Carrito con cantidades, validación de stock y cobro en efectivo/tarjeta.
- Venta transaccional: registra encabezado y líneas, descuenta stock y actualiza dashboard.
- Productos, ajustes de inventario, reportes de ventas y corte de caja.
- Configuración local con estado de SQLite, bridge, respaldos y preferencias multicaja.
- Multicaja online opcional: login, registro de terminal/caja, catálogo, ventas, inventario,
  movimientos de caja y cierre se enrutan a la API central cuando está habilitada.

## Multicaja web

Activa la conexión en `GrunflexPOS.Web/appsettings.json` o en el archivo local de configuración:

```json
"Multicaja": {
  "Enabled": true,
  "ApiBaseUrl": "http://192.168.1.10:7279/",
  "SharedSecret": "secreto-compartido",
  "CajaId": "",
  "TerminalCode": "CAJA-WEB-02"
}
```

Al iniciar, la caja web se auto-registra en la API si `CajaId` está vacío y sincroniza el
catálogo central a su SQLite local. El login usa `/api/multicaja/login`; las ventas,
ajustes de inventario, movimientos y cierres usan `requestId` idempotente y la cabecera
`X-Grunflex-Multicaja-Key`. La API central es la autoridad de stock y operación; SQLite
se conserva como caché y respaldo de la interfaz. Si la API no está disponible, la web
indica el estado offline y no registra silenciosamente la operación como una caja
independiente.

## Hardware

Los lectores USB configurados como teclado pueden escribir en el campo de búsqueda sin bridge. Para lectores seriales, impresión térmica y cajón usa el bridge. Sus endpoints requieren el token local y aceptan únicamente origen loopback. Nunca cambies el bind a `0.0.0.0` ni publiques el puerto en la LAN.

La apertura usa ESC/POS `1B 70 00 19 FA`. Prueba primero el endpoint de salud y después el dispositivo seleccionado. Si un driver de impresora no acepta RAW, configura el puerto COM o instala el driver compatible.

## Seguridad y operación

Las credenciales de demostración (`admin/admin`, `cajero/cajero`) deben sustituirse antes de entregar una instalación a un comercio. El bridge genera/usa un token local y sus logs no deben contener contraseñas, tokens ni datos completos de tarjetas. La autenticación de clientes multicaja y licencias sigue siendo responsabilidad de la API central existente.

Antes de declarar paridad completa, valida con datos del comercio: impuestos, descuentos, devoluciones, impresión, apertura de cajón, corte, licencia, sincronización multicaja, cola offline y recuperación de backups. Ver también [WEB-GOLIVE-CHECKLIST.md](WEB-GOLIVE-CHECKLIST.md).

