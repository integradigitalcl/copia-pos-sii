# Migración de contraseñas a BCrypt

Documentación técnica del cambio de almacenamiento de contraseñas de usuarios POS de **texto plano** a **BCrypt** (work factor 12).

## Objetivo

- Eliminar contraseñas en claro en base de datos, APIs y sincronización multicaja.
- Mantener compatibilidad con instalaciones existentes hasta que cada usuario inicie sesión o se ejecute la migración al arranque.
- Unificar verificación en POS local, API multicaja y herramientas auxiliares.

## Componentes

| Componente | Ubicación |
|------------|-----------|
| `PasswordHasher` | `Grunflex.Licensing.Abstractions/Security/PasswordHasher.cs` |
| `UserPasswordMigration` | `Grunflex.Licensing.Abstractions/Security/UserPasswordMigration.cs` |
| Login POS local | `GrunflexPOS2/Services/UsuarioService.cs` |
| Login API multicaja | `GrunflexPOS.API/Controllers/MulticajaOperacionesController.cs` |
| Migración al arranque POS | `GrunflexPOS2/App.xaml.cs` |
| Migración al arranque API | `GrunflexPOS.API/Hosting/DatabaseSchemaInitializer.cs` |

## Formato almacenado

- **Nuevo:** hash BCrypt (`$2a$`, `$2b$` o `$2y$` + salt + hash).
- **Legacy:** cadena en texto plano (solo durante transición).

Detección: `PasswordHasher.IsBcryptHash(stored)`.

## Flujos de migración

### 1. Al primer login exitoso (lazy)

1. Buscar usuario por `Username` (sin comparar contraseña en SQL).
2. `PasswordHasher.TryVerifyAndUpgrade(plain, stored, out upgraded)`.
3. Si `upgraded != null`, persistir el hash y continuar sesión.
4. La sesión en memoria (`App.UsuarioActual`) **no** incluye contraseña ni hash.

### 2. Al arranque (batch)

POS y API recorren `Usuarios` y hashean filas cuyo `Password` no sea BCrypt. Útil para cuentas que no inician sesión frecuentemente (p. ej. cajeros solo en servidor).

## Creación y edición

- **Crear usuario:** siempre `PasswordHasher.Hash(plain)` antes de `SaveChanges`.
- **Editar usuario:** si el campo contraseña en UI está vacío, no modificar columna `Password`.
- **CajerosView:** al seleccionar un usuario existente el campo contraseña queda vacío (no se muestra el hash).

## API multicaja — sin exposición

| Endpoint | Contraseña |
|----------|------------|
| `POST /api/multicaja/login` | Entrada: texto plano (HTTPS/LAN). Salida: sin password. |
| `GET /api/multicaja/usuarios` | **No** devuelve password. |
| `POST/PUT /api/multicaja/usuarios` | Entrada opcional/obligatoria (upsert). Respuesta sin password. |

DTO de respuesta `MulticajaUsuarioDto`: sin propiedad `Password`.

## Sincronización caja adicional (sombra)

`MulticajaShadowCatalogSync.PullUsuariosAsync` sincroniza `Id`, `Username`, `Nombre`, `Rol` únicamente. **No** copia hashes ni contraseñas desde el servidor. El login en terminal API-only usa `POST /api/multicaja/login`.

## Compatibilidad

| Escenario | Comportamiento |
|-----------|----------------|
| BD con passwords en claro | Login OK; migración lazy + batch al arranque |
| BD ya migrada | Solo verificación BCrypt |
| Cliente POS antiguo + API nueva | Listado usuarios sin password; edición con campo vacío = sin cambio |
| Respaldo/restauración | Se restauran hashes BCrypt tal cual |

## Seguridad operativa

- Work factor BCrypt: **12** (ajustable en `PasswordHasher.WorkFactor` interno).
- Comparación legacy en texto plano usa ordinal exacto (comportamiento previo).
- Endpoints legacy `UsuariosController` (`GET {id}/password`, reset en claro): deshabilitados / sin devolver secretos.

## Verificación manual

1. Usuario legacy con password `1234` inicia sesión → fila en BD empieza con `$2`.
2. `GET /api/multicaja/usuarios` → JSON sin campo `password`.
3. Crear cajero con contraseña nueva → BD solo hash BCrypt.
4. Caja adicional: sync usuarios → BD sombra sin hashes replicados; login remoto OK.

## Dependencia

- Paquete NuGet: `BCrypt.Net-Next` (referenciado en `Grunflex.Licensing.Abstractions`, consumido por POS y API).
