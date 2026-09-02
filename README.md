# GrünFlex POS

Sistema de punto de venta (POS) para Windows: ventas, inventario, multicaja, licenciamiento, reportes y módulos de integración (YouTube Music, Servipag, hardware).

---

## Qué es

**GrünFlex POS** es una aplicación de caja registradora para comercios. Incluye:

| Pieza | Descripción |
|--------|-------------|
| **POS (WPF)** | Interfaz de cajero: ventas, cobro, productos, inventario, reportes, corte |
| **API local** | Hub en la red LAN (`:7279`) para multicaja, sync y licencias |
| **Licenciamiento** | Tokens firmados RSA (GFv2) con entitlements (multicaja, soporte, cajas) |
| **Instaladores** | Multicaja y monocaja (Inno Setup), generados desde `ops/posedgesetup/` |

---

## Requisitos

- Windows 10/11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (desarrollo)
- [Inno Setup 6](https://jrsoftware.org/isdl.php) (solo para compilar instaladores)
- Microsoft Edge WebView2 Runtime (incluido en los instaladores)

---

## Cómo funciona

### Flujo diario en caja

1. El cajero inicia sesión (usuarios con roles/permisos BCrypt).
2. Trabaja en **Ventas**: busca productos, arma el carrito, cobra (efectivo/tarjeta/otros).
3. Se imprime ticket (PDF/impresora térmica) y se actualiza stock.
4. Al cierre: **Corte de caja** con resumen de ventas y métodos de pago.
5. **Reportes**: ventas del día, dashboard, top productos, etc.

### Datos

- Base principal (caja principal): SQLite en `%ProgramData%\GrunflexPOS\data\grunflex.db`
- Configuración local: `%LocalAppData%\GrunflexPOS\config\appsettings.local.json`
- Configuración de máquina (instalador): `%ProgramData%\GrunflexPOS\config\appsettings.local.json`

La resolución de config está en `GrunflexPOS2/Data/AppConfig.cs` (prioridad: `appsettings.json` → local usuario → ProgramData → variables `GRUNFLEX_*`).

### Multicaja

```
┌─────────────────────┐         LAN HTTP :7279        ┌─────────────────────┐
│  Caja PRINCIPAL     │◄─────────────────────────────►│  Caja ADICIONAL     │
│  - POS + API        │         sync / ventas         │  - Solo POS         │
│  - SQLite central   │         SignalR hub           │  - Shadow SQLite    │
└─────────────────────┘                               └─────────────────────┘
```

- **Principal** (`TerminalRole = server`): aloja la BD y la API.
- **Adicional** (`TerminalRole = client`): UI de POS; operaciones críticas vía API.
- Modo recomendado: **API-only** (`Multicaja:UseApiOnlyClient`), sin depender de SMB/UNC.
- Si no hay red: cola offline (`Services/Offline/OfflineQueue`) y reintento al reconectar.
- Límite de cajas controlado por licencia (`NumberOfBoxes` / entitlement Multicaja).

Documentación detallada: `docs/MULTICAJA-ENTERPRISE.md`, `docs/MULTICAJA-ELEVENTA-DISENO.md`.

### Licenciamiento

1. Internamente se emite un token con **LicenseIssuer** (clave privada, no se distribuye).
2. El POS valida con la **clave pública** embebida (`Licensing:PublicKeyPem`).
3. Entitlements típicos: Multicaja, OnlineSupport, CloudBackup, PrioritySupport, número de cajas, días de gracia offline.
4. Sin contacto con el servidor de licencia: opera un tiempo de gracia (por defecto 14 días) y luego puede bloquear funciones.

UI: Configuración → Licencia / ventanas de activación.

### Módulos de integración

| Módulo | Comportamiento |
|--------|----------------|
| **YouTube Music** | Widget en sidebar (play/pausa, anterior, siguiente, mute) + pantalla completa tipo Servipag (`music.youtube.com`) |
| **Servipag** | Navegador embebido WebView2 al portal |
| **Web / Soporte** | Funciones premium según licencia |
| **Hardware** | Báscula, lector de código, impresora, cajón de dinero (puertos serie) |

### Tema visual

El color de acento cambia según licencia: **azul** (multicaja) / **verde** (monocaja). Ver `GrunflexPOS2/UI/BrandThemeService.cs`.

---

## Estructura del repositorio

```
GrunflexPOS2/                 # POS WPF (cliente)
GrunflexPOS.Web/              # POS local para Chrome (Blazor Server + SQLite)
GrunflexPOS.HardwareBridge/   # Bridge loopback para impresora, cajón y COM
GrunflexPOS.Web.Tests/        # Tests de persistencia y ventas web
GrunflexPOS.API/              # API ASP.NET Core (multicaja / licensing / pago)
Grunflex.Licensing.Abstractions/
Grunflex.LicenseIssuer/       # Emisión de licencias (interno)
GrunflexPOS.LicenseManager/   # Gestión de licencias (interno)
src/PosEdge.*                 # Plataforma PosEdge (API, Workers, Terminal, …)
tools/PosEdge.*               # Launcher, Guardian, SetupAgent, CLIs
ops/posedgesetup/             # Instaladores oficiales (scripts + .iss)
ops/installer/                # Instalador legacy (solo scripts; no binarios)
ops/installer-internal/       # Empaquetado interno LicenseIssuer/Manager
docs/                         # Diseño y runbooks
tests/                        # Tests PosEdge / sync / offline
```

**No se versionan en GitHub:** carpetas `out/`, `staging/`, `.exe` de instaladores, `deploy/`, parches ZIP ni bases SQLite locales. Se regeneran con los scripts de build.

---

## Desarrollo local

### Compilar y ejecutar

```powershell
# Restaurar y compilar
dotnet restore GrunflexPOS2.slnx
dotnet build GrunflexPOS2.slnx -c Release

# API (Swagger en http://127.0.0.1:7279)
dotnet run --project GrunflexPOS.API --launch-profile http

# POS (otra terminal)
dotnet run --project GrunflexPOS2
```

O abrir `GrunflexPOS2.slnx` en Visual Studio y ejecutar ambos proyectos.

### Configuración de ejemplo

`GrunflexPOS2/appsettings.json` apunta por defecto a `http://127.0.0.1:7279/`.  
Overrides locales: crear `%LocalAppData%\GrunflexPOS\config\appsettings.local.json` (no commitear).

### Tests

```powershell
dotnet test GrunflexPOS.API.Tests
```

---

## POS Web local para Chrome

Esta copia incluye `GrunflexPOS.Web`, una aplicación Blazor Server para usar el POS desde Chrome sin reemplazar el cliente WPF. Escucha únicamente en loopback, guarda el catálogo y las ventas en SQLite y mantiene el flujo de login, ventas, cobro, productos, inventario, reportes, corte, configuración y widget de YouTube Music.

```powershell
# Desarrollo con hot reload y Chrome
powershell -ExecutionPolicy Bypass -File ops/web/run-web-dev.ps1

# Publicar y ejecutar como instalación local
powershell -ExecutionPolicy Bypass -File ops/web/run-web-production.ps1

# Tests de persistencia SQLite
dotnet test GrunflexPOS.Web.Tests
```

Credenciales iniciales de demostración local: `admin/admin` (Administrador) o `cajero/cajero` (Cajero). En una instalación de producción deben reemplazarse por la autenticación central del comercio.

La base web se crea en `%LocalAppData%\GrunflexPOS\grunflex-pos.db` y puede cambiarse con `Data:DatabasePath` en `GrunflexPOS.Web/appsettings.json`. La API de salud está disponible en `http://127.0.0.1:7373/health`.

### Hardware desde Chrome

Chrome no controla directamente puertos COM ni impresoras RAW. `GrunflexPOS.HardwareBridge` escucha en `127.0.0.1:7390` y expone operaciones autenticadas para impresora térmica, cajón y lector serial. Los lectores USB en modo teclado pueden seguir usándose directamente en el campo de búsqueda. El bridge se instala junto al web POS y no debe publicarse en una interfaz de red.

El instalador de esta copia se genera con:

```powershell
powershell -ExecutionPolicy Bypass -File ops/web/compile-web-installer.ps1
```

---

## Instaladores oficiales

Fuente: `ops/posedgesetup/`

| Instalador | Script | Salida (local, no en git) |
|------------|--------|---------------------------|
| **Multicaja** | `single/compile-installer.ps1` | `single/out/PosEdge-Setup.exe` |
| **Monocaja** | `monocaja/compile-installer.ps1` | `monocaja/out/GrunflexPOS-Mono-Setup.exe` |

Compilar ambos:

```powershell
powershell -ExecutionPolicy Bypass -File ops/posedgesetup/compile-all-installers.ps1 -Configuration Release
```

- **Multicaja:** el wizard pregunta caja principal o adicional (+ IP del servidor).
- **Monocaja:** un solo equipo, siempre como caja principal local.

Detalle: `ops/posedgesetup/README.md`.

Parches hotfix (YouTube / UI) se generan con scripts en `ops/posedgesetup/build-youtube-release.ps1` y `ops/patch-ui/`; los `.exe`/ZIP resultantes **no** se suben al repo.

---

## Stack técnico

| Capa | Tecnología |
|------|------------|
| Runtime | .NET 8 (POS: `net8.0-windows`, API: `net8.0`) |
| UI | WPF + WebView2 |
| API | ASP.NET Core, SignalR, JWT, Serilog, health checks |
| Datos | SQLite + EF Core 8 (API también soporta PostgreSQL/Npgsql) |
| Auth usuarios | BCrypt |
| Tickets / reportes | QuestPDF, PDFsharp, ClosedXML |
| Updates | Velopack (opcional vía `Updates:Url`) |
| Empaquetado | Inno Setup 6 |

---

## Seguridad (antes de publicar)

- No incluir clave privada RSA, `.pem` privados, `.lic` de clientes ni `appsettings.local.json` con secretos.
- LicenseIssuer es herramienta **interna**; no va en el instalador de cliente.
- Revisar que `Licensing:PublicKeyPem` en el build de cliente sea solo la **pública**.

---

## Documentación adicional

| Documento | Contenido |
|-----------|-----------|
| `docs/MULTICAJA-ENTERPRISE.md` | Multicaja empresarial |
| `docs/MULTICAJA-ELEVENTA-DISENO.md` | Diseño sync / API-only |
| `docs/USUARIOS-BCRYPT.md` | Usuarios y contraseñas |
| `ops/posedgesetup/README.md` | Cómo generar instaladores |
| `agents/README.md` | Notas de agentes / automatización |

---

## Licencia del producto

Software propietario GrünFlex. El uso en comercios requiere licencia válida emitida por GrünFlex.
