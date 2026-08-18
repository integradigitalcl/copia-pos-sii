# Prompt para analizar el proyecto Grunflex POS

> Copia desde aquí hacia abajo y pégalo a ChatGPT (preferentemente GPT-5 o GPT-4o con contexto largo).

---

# Contexto del proyecto: Grunflex POS

Necesito que analices un sistema de Punto de Venta (POS) que estoy desarrollando, identifiques riesgos arquitectónicos, mejoras de seguridad/UX, y propongas un roadmap. Te describo todo el ecosistema.

## 1. Visión general del producto

**Producto comercial**: Grunflex POS, un Punto de Venta para PYMEs en Chile (modelo similar a Eleventa). Se vende **on-premise** (no SaaS), con instalador propio y licencia firmada por mí. Soporta operación **standalone** (1 caja) y **multicaja en red local** (1 caja principal + N cajas adicionales conectadas vía SMB y HTTP a la caja principal).

**Modelo de negocio**:
- POS básico: instalación gratuita o pago único.
- Funciones **PAGADAS** gobernadas por licencia firmada RSA:
  - **Multicaja** (conectar más de 1 caja en red).
  - **OnlineSupport** (botones web/WhatsApp integrados).
  - **CloudBackup** (respaldo en la nube).
  - **PrioritySupport**.

**Stack**:
- WPF (.NET 8 Windows) para todas las apps de escritorio.
- ASP.NET Core 8 para la API (que se distribuye junto al POS, no como servicio remoto).
- SQLite como única base de datos (en cada PC local, o compartida vía SMB para multicaja).
- PostgreSQL: **eliminado** del producto final (era de versiones previas; no se instala).
- Inno Setup para instaladores Windows.
- PowerShell para automatización (build/staging y scripts post-instalación).

## 2. Componentes del repositorio

```
GrunflexPOS2/                          (repo raíz)
├── GrunflexPOS2/                      ← POS principal (WPF)
│   ├── App.xaml.cs                    ← bootstrap, validaciones de arranque, auto-registro de caja
│   ├── Data/                          ← AppConfig, LocalDatabasePaths, DbContext
│   ├── Models/Entities/               ← Producto, Venta, Caja, Empresa, Usuario, Configuracion...
│   ├── Services/                      ← VentaService, ConfiguracionService, etc.
│   ├── Licensing/                     ← LicenseService, ProductEntitlements, LicenseStateProvider
│   ├── Infrastructure/Setup/          ← TerminalConfigBootstrap (auto-aplicar plantilla cliente)
│   ├── Migrations/                    ← migraciones EF Core SQLite
│   ├── Views/                         ← XAML (login, caja, ventas, configuración, activación licencia, conectar más cajas...)
│   └── ViewModels/, Themes/, Assets/
│
├── GrunflexPOS.API/                   ← API ASP.NET Core (se instala junto al POS en caja principal)
│   ├── Controllers/                   ← Auth, Licensing, LicenseIssuer (uso interno mio), Pago, Productos, Usuarios, Support, Backups, IssuerActivations, IssuerClients
│   ├── Services/                      ← LicensingIssueService (firma RSA), PagoService, etc.
│   ├── Security/                      ← JwtOptions, TokenService, RefreshTokenService, SensitiveDataEncryption, SecurityHeadersMiddleware
│   ├── Configuration/                 ← ApiLocalSecretsBootstrap (genera api.secrets.json al primer arranque)
│   ├── Data/                          ← ApiDbContext (SQLite)
│   └── Models/                        ← Producto, PagoTransaccion, Usuario, RefreshTokenEntity
│
├── Grunflex.LicenseIssuer/            ← App WPF SOLO PARA MI (interna, no se vende)
│   ├── MainWindow.xaml.cs             ← UI para emitir licencias
│   ├── ViewModels/MainViewModel.cs    ← lógica: firma RSA, guarda .lic, sincroniza con API local
│   ├── Persistence/                   ← settings, audit log
│   └── Dialogs/                       ← editar cliente, prompt
│
├── GrunflexPOS.LicenseManager/        ← App WPF SOLO PARA MI (admin local de licencias en un PC)
│   ├── MainWindow.xaml.cs             ← valida admin, aplica licencia manual u online por ActivationID
│
├── Grunflex.Licensing.Abstractions/   ← biblioteca compartida: GrunflexLicensePayload, helpers
│
├── Grunflex.Backoffice/               ← (estructura preparada, no en uso activo)
│
├── GrunflexPOS.API.Tests/             ← tests xUnit (HealthEndpoint, TokenService)
│
├── ops/installer/                     ← Instalador COMERCIAL del POS (lo que se VENDE)
│   ├── GrunflexPOS.iss                ← Inno Setup script (versión 1.0.9 actual)
│   ├── build-staging.ps1              ← publica POS + API, hornea clave pública en appsettings.json
│   ├── compile-installer.ps1          ← orquesta build + ISCC
│   ├── LICENCIA.txt
│   ├── ServerExtras/
│   │   ├── habilitar-recurso-grunflexpos.ps1  ← script SMB + Firewall (netsh) en caja principal
│   │   └── CONEXION-CAJAS.txt
│   └── out/  GrunflexPOS_Setup_1.0.9.exe       (~101 MB)
│
├── ops/installer-internal/            ← Instalador INTERNO (solo para mi PC; NO se vende)
│   ├── Grunflex.LicenseIssuer.iss     ← incluye LicenseIssuer + LicenseManager
│   ├── build-staging.ps1
│   ├── compile.ps1
│   └── out/  Grunflex_LicenseIssuer_Setup_1.0.1.exe   (~108 MB)
│
└── tools/SalesSeeder/                 ← utilidad CLI para sembrar ventas de prueba
```

**Conteo de archivos fuente** (sin bin/obj):
- POS: 151 archivos .cs/.xaml
- API: 54 .cs
- LicenseIssuer: 17 .cs/.xaml
- LicenseManager: 5 .cs/.xaml

## 3. Flujos clave

### 3.1 Licenciamiento (clave del modelo de negocio)

- Yo (Grunflex) tengo una **clave RSA privada** generada al primer arranque de la API en mi PC. Se almacena en `%LocalAppData%\GrunflexPOS\data\api.secrets.json`.
- La **clave pública** se exporta a `%LocalAppData%\GrunflexPOS\data\licensing-public.pem` y se **hornea automáticamente** en `appsettings.json` durante el build del instalador comercial (script `build-staging.ps1` lee el PEM y lo inyecta).
- Para cada cliente, abro mi `Grunflex.LicenseIssuer.exe` → pongo nombre/RUT → "Generar" → produce un token firmado `GFv2.<base64>.<sigBase64>` (payload con módulos habilitados + fecha de expiración) y lo guarda como archivo `.lic`.
- El cliente recibe el `.lic`, lo pega en POS → Configuración → Activar licencia. El POS valida con la clave pública horneada en su `appsettings.json`.
- Los entitlements (Multicaja, OnlineSupport, CloudBackup, PrioritySupport) **solo** se aplican vía token firmado válido. **Cerré el backdoor** que antes permitía activarlos editando `appsettings.json`.

### 3.2 Multicaja plug-and-play (versión 1.0.9)

**Caja Principal (servidor)**:
1. Cliente ejecuta `GrunflexPOS_Setup_1.0.9.exe` → elige "Caja Principal".
2. El instalador automáticamente al final ejecuta `habilitar-recurso-grunflexpos.ps1` que:
   - Crea carpeta `%LocalAppData%\GrunflexPOS\data`.
   - Crea/actualiza SMB share `\\<PC>\GrunflexPOS` con `Everyone = Change`.
   - Aplica ACL NTFS `BUILTIN\Users` (SID `S-1-5-32-545`) = Modify.
   - Crea regla de firewall **TCP 7279 entrante** via `netsh advfirewall`.
3. Tiempo: ~4 segundos. Funciona en Windows español/inglés (uso SIDs, no nombres localizados).
4. Cliente activa su licencia con Multicaja.
5. Lanza la API desde menú Inicio.

**Caja Adicional (cliente)**:
1. Ejecuta el mismo `GrunflexPOS_Setup_1.0.9.exe` → elige "Caja Adicional".
2. Nuevo asistente pide **IP del servidor** (la del `ipconfig` del PC principal en la LAN).
3. Inno Setup escribe automáticamente `%LocalAppData%\GrunflexPOS\config\appsettings.local.json` con:
   ```json
   {
     "ConnectionStrings": { "Default": "Data Source=\\\\IP\\GrunflexPOS\\data\\grunflex.db;Cache=Shared" },
     "Api": { "BaseUrl": "http://IP:7279/", "PagoBaseUrl": "http://IP:7279/api/pago" },
     "CajaId": ""
   }
   ```
4. Abre el firewall saliente TCP 7279.
5. Al abrir el POS:
   - `App.xaml.cs` valida acceso al share SMB y a `/health/live` de la API.
   - Si `CajaId` está vacío y la conexión es UNC → **auto-registra** una nueva Caja con nombre `<MachineName> - Caja` en la base central, guarda el GUID en `appsettings.local.json` de usuario.
   - Pasa a login normal.

### 3.3 Persistencia y configuración

- **`appsettings.json`**: solo lectura, distribuido con el instalador. Contiene la clave pública RSA horneada.
- **`appsettings.local.json`**: **escritura siempre** en `%LocalAppData%\GrunflexPOS\config\` (siempre escribible, no necesita admin). Por compatibilidad lee también el legacy junto al .exe (en Program Files), pero la versión de usuario prevalece.
- **DB SQLite POS**: `%LocalAppData%\GrunflexPOS\data\grunflex.db` (local) o `\\servidor\GrunflexPOS\data\grunflex.db` (terminal multicaja).
- **DB SQLite API**: `%LocalAppData%\GrunflexPOS\data\grunflex_api.db`.
- **Secretos API**: `%LocalAppData%\GrunflexPOS\data\api.secrets.json` (JWT key, admin password autogenerada, RSA privada en mi PC).
- **Logs POS**: `%LocalAppData%\GrunflexPOS\logs\pos.log` + `setup-multicaja.log`.

### 3.4 Robustez de arranque del POS

`App.xaml.cs` ahora:
- Maneja `DispatcherUnhandledException` y `AppDomain.UnhandledException` → muestra MessageBox y loguea. Nunca cierra silencioso.
- Si la cadena de conexión apunta a una UNC inalcanzable → diálogo **"¿Restablecer terminal a base local?"** → escribe un override vacío en `%LocalAppData%\GrunflexPOS\config\appsettings.local.json` y vuelve a operar como caja standalone.

## 4. Instaladores actuales

| Instalador | Versión | Tamaño | Para quién |
|---|---|---|---|
| `GrunflexPOS_Setup_1.0.9.exe` | 1.0.9 | ~101 MB | Clientes (se vende) |
| `Grunflex_LicenseIssuer_Setup_1.0.1.exe` | 1.0.1 | ~108 MB | Solo yo (interno) |

El instalador comercial **NO incluye** LicenseIssuer ni LicenseManager. Solo POS + API + scripts SMB/firewall + WebView2 bootstrapper.

El instalador interno **NO incluye** el POS. Solo LicenseIssuer + LicenseManager. Versión separada por seguridad: la clave privada nunca debe llegar a un PC de cliente.

## 5. Lo que está LISTO (funcional y probado)

### POS
- [x] Punto de venta con cobro en efectivo y MP (Servipag vía WebView2).
- [x] Catálogo de productos con búsqueda y código de barras.
- [x] Gestión de inventario (entrada/salida, movimientos).
- [x] Ventas con ticket impreso/PDF (QuestPDF + PdfSharp).
- [x] Reportes locales (ventas, productos, caja).
- [x] Multi-usuario con roles (admin, cajero).
- [x] Cierre de caja con arqueo.
- [x] Lector de código de barras serial.
- [x] Configuración persistida en DB (`ConfiguracionService`).
- [x] Migraciones EF Core (SQLite).
- [x] Auto-aplicación de plantilla terminal desde Escritorio/Descargas (TerminalConfigBootstrap).
- [x] Validación de conectividad multicaja al arranque (SMB + API).
- [x] Auto-registro de Caja en base central al primer arranque del cliente.
- [x] Handler global de excepciones (no más cierres silenciosos).
- [x] Auto-reparación: detecta conexión UNC inalcanzable y ofrece restablecer.
- [x] Configuración de usuario en `%LocalAppData%` (no escribe en `Program Files`).

### API
- [x] Autenticación JWT con refresh token (`AuthController`, `RefreshTokenService`).
- [x] Bootstrap automático de secretos al primer arranque (`ApiLocalSecretsBootstrap`).
- [x] Endpoint `/api/licensing/activate` (activación online por ActivationID).
- [x] Endpoint `/api/licensing/public-key` (expone la clave pública RSA).
- [x] Endpoint `/api/licensing/issue` (uso interno desde LicenseIssuer).
- [x] Endpoints de pago (Servipag), productos, usuarios, soporte, respaldos.
- [x] Endpoints de gestión de licencias e issuer (clientes, activaciones).
- [x] Health checks (`/health/live`, `/health/ready`).
- [x] Rate limiting (AspNetCoreRateLimit).
- [x] Logging con Serilog (consola + archivo + Seq opcional).
- [x] Métricas Prometheus.
- [x] Middleware de cabeceras de seguridad.

### Licenciamiento
- [x] Firma RSA 2048 con SHA-256 (token `GFv2.<payload>.<sig>`).
- [x] Validación local con clave pública horneada en `appsettings.json`.
- [x] Activación manual (.lic) y online (ActivationID + nombre máquina).
- [x] Sincronización con servidor (mi PC) opcional para refresh.
- [x] Período de gracia offline configurable (`Licensing:OfflineGraceDays`, default 14 días).
- [x] Multicaja, OnlineSupport, CloudBackup, PrioritySupport como flags individuales.
- [x] **Cerrado el backdoor de `appsettings.json`** (antes podías editar a mano).

### LicenseIssuer (interno)
- [x] UI completa con dashboard, clientes, licencias, activaciones, reportes, configuración.
- [x] Defaults pro-multicaja: `NumberOfBoxes=5`, `OnlineSupport=true`, `CloudBackup=true`, `PrioritySupport=true`.
- [x] Persistencia en la API (sincroniza licencias emitidas con mi servidor central).
- [x] Audit log de operaciones.
- [x] Guarda automáticamente `.lic` en disco para entregar al cliente.

### LicenseManager (interno)
- [x] Validación de admin (usuario + contraseña).
- [x] Aplicar licencia manual (token pegado).
- [x] Activación online por ActivationID.
- [x] Cargar licencia actualmente almacenada y mostrar info.

### Instalación / despliegue
- [x] Inno Setup con dos tipos: "Caja Principal" y "Caja Adicional".
- [x] Página de asistente para IP del servidor en caja adicional.
- [x] Script PowerShell post-instalación: SMB share + ACL NTFS + firewall TCP 7279 (vía netsh, idiom-independent).
- [x] WebView2 bootstrapper incluido y se instala silencioso.
- [x] Instalador limpia configuraciones de terminal previas al reinstalar (`InstallDelete`).
- [x] Inyección automática de clave pública RSA en `appsettings.json` durante build.

## 6. Lo que FALTA o está PARCIAL

### Arquitectura multicaja
- [ ] **SQLite sobre SMB** funciona OK para 2-3 cajas con baja concurrencia, pero **no escala bien**. EF Core + SQLite tienen problemas con locking en escrituras concurrentes desde múltiples PCs. Para 5+ cajas o ventas simultáneas se necesita un modelo cliente-servidor real vía API (POS escribiendo a la API en vez de tocar SQLite directo).
- [ ] **Sin offline en cliente**: actualmente el cliente NO opera si el servidor está apagado (decisión del usuario, pero podría ser un punto débil comercial). Falta cache local + sincronización diferida.
- [ ] **Detección de servidor en LAN**: el usuario aún debe ingresar la IP del servidor manualmente. No hay mDNS/Bonjour discovery ni ping broadcast.
- [ ] **API como servicio de Windows**: actualmente la API es un ejecutable consola que el usuario lanza desde Inicio. Si cierra la ventana, las cajas adicionales pierden conexión. Falta convertirla en servicio (sc.exe / Worker Service / nssm).

### Seguridad
- [ ] **Constante temporal de firma HMAC**: en `Grunflex.LicenseIssuer/ViewModels/MainViewModel.cs` línea 24 hay un `PosSigningSecret` hardcoded ("GRUNFLEX_POS_LICENSE_SIGNING_SECRET_V1_CHANGE_ME") como compatibilidad con el camino legacy HMAC. Debería removerse y solo aceptar RSA GFv2.
- [ ] **Sin certificado de firma del .exe**: el instalador `GrunflexPOS_Setup_1.0.9.exe` no está firmado con certificado Authenticode → Windows SmartScreen marca alerta al usuario final. Necesito comprar un cert EV o sectigo.
- [ ] **SMB con `Everyone`**: actualmente el share usa autenticación abierta. En LAN doméstica/oficina chica está OK, pero en redes con políticas estrictas falla. Falta opción de share por usuario/contraseña.
- [ ] **Sin TLS en la API**: la API expone HTTP plano en 7279. Para multicaja en LAN privada es aceptable, pero credenciales JWT viajan en claro. Falta certificado autofirmado para HTTPS.
- [ ] **Logs no rotados**: archivos en `%LocalAppData%\GrunflexPOS\logs\` crecen indefinidamente.

### Producto/UX
- [ ] **No hay módulo de clientes en el POS** (CRM básico, ventas asociadas a un cliente con RUT).
- [ ] **Sin facturación electrónica SII (Chile)**. Se vende como Boleta no tributaria; falta integración con DTE.
- [ ] **Sin sincronización con e-commerce** (WooCommerce, Shopify).
- [ ] **Sin app móvil** para revisar ventas remoto.
- [ ] **Backup en la nube** (`CloudBackup`) está cableado en la UI/licencia pero la implementación del subir a S3/Azure es básica (`CloudBackupCoordinator`).
- [ ] **Tickets con logo del cliente**: la plantilla actual es genérica.

### LicenseIssuer (mi pipeline)
- [ ] **Sin export de respaldo de claves privadas**: si pierdo `api.secrets.json` pierdo TODAS las licencias. Falta export cifrado de la clave RSA privada con frase de paso.
- [ ] **Sin renovación automática de licencias**: si un cliente está cerca de vencer, no se le notifica. Falta un job que mande email N días antes.
- [ ] **Sin facturación/cobro integrado**: emito licencia pero no genero invoice ni link de pago. Falta integrar Transbank/MercadoPago/Stripe.

### Testing
- [ ] Tests integrales solo cubren API (Health + TokenService). El POS no tiene tests automatizados.
- [ ] Sin tests de integración multicaja (simular 2 PCs + share + carga concurrente).

### Documentación
- [ ] Sin manual de usuario en PDF (solo `CONEXION-CAJAS.txt`).
- [ ] Sin diagrama de arquitectura visible para terceros.
- [ ] Sin docs de la API (Swagger sirve, pero no hay README orientado al integrador).

## 7. Cosas peculiares / decisiones técnicas que vale la pena cuestionar

1. **API embebida en cada instalación**: en una venta multicaja, la API corre en el PC servidor del cliente, no en mi infraestructura. Esto elimina dependencia de internet pero implica que cada cliente tiene su propia llave JWT, su propia base, etc. Ventaja: privacidad y operación offline. Desventaja: no puedo introspeccionar/soportar remotamente sin acceso al PC.

2. **SQLite compartido por SMB**: pragmáticamente funciona para tiendas pequeñas, pero técnicamente es un anti-patrón conocido. La alternativa "correcta" sería que las cajas adicionales hablen HTTP a la API del servidor y nunca toquen SQLite directo.

3. **WPF + .NET 8 Windows**: ata el producto a Windows. Cuestionable a futuro si quisiera tablets o Linux POS.

4. **Auto-registro de Caja sin validación de licencia previa**: actualmente cualquier PC en la LAN que acceda al share puede llamar a `App.xaml.cs` → registrar una caja nueva. La gate de licencia (Multicaja) está SOLO en la UI "Conectar más cajas" del servidor. Si una caja adicional escribe directo en la base central por SMB, no hay enforcement de licencia en el lado del cliente. Tal vez aceptable porque la licencia está atada al servidor (sin servidor no hay base), pero conceptualmente débil.

5. **No uso ASP.NET Identity** en la API: el `Usuario` propio con BCrypt está hecho a mano. Funciona, pero pierdo features (lockout, 2FA, password reset).

6. **No hay un único ConfigurationService global**: el POS lee config de `AppConfig` (archivo) y `ConfiguracionService` (DB), dependiendo del lugar. Causa confusión.

7. **`GrunflexPOS.LicenseManager` se solapa con la pantalla de activación dentro del POS**: ambos hacen casi lo mismo. ¿Vale la pena mantener LicenseManager?

## 8. Lo que necesito que analices y me devuelvas

Por favor revisa todo lo anterior y devuélveme:

1. **Riesgos críticos** (top 5) que podrían quemarme con un cliente real, ordenados por probabilidad × impacto.
2. **Mejoras de seguridad** concretas, en orden de costo/beneficio.
3. **Roadmap sugerido** para los próximos 3 meses (sprints semanales) si mi objetivo es vender 20 licencias y sobrevivir el primer soporte.
4. **Recomendaciones arquitectónicas** sobre:
   - SQLite vs PostgreSQL local (¿vale la pena el cambio?).
   - SMB compartido vs API-centric multicaja.
   - Convertir API en servicio de Windows.
   - Estrategia de actualizaciones automáticas (¿Velopack? ¿Squirrel.Windows? ¿WiX?).
5. **Trampas legales/comerciales** que debería anticipar (LPC, Ley 19.628, factura electrónica obligatoria SII, etc.).
6. **Métricas críticas** que debería instrumentar antes de salir a producción.
7. Cualquier **anti-patrón evidente** que veas en lo descrito.

Sé directo. No me ahorres críticas. Quiero saber qué se rompe primero cuando un cliente real lo use.
