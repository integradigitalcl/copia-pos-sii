## Deployment guide (2 PCs) — PosEdge multicaja

### Objetivo

Instalación real para pruebas operacionales con:

- **PC1 (Servidor)**: PostgreSQL + `PosEdge.Api` (SignalR + workers)
- **PC2 (Terminal)**: `PosEdge.Terminal` + SQLite local (offline-first activo)

No se desarrollan features ni se cambia arquitectura. Esto es solo build/deploy/pruebas.

### Puertos / endpoints

- **API + SignalR (mismo puerto)**: `http://0.0.0.0:5071`
- **Health**:
  - `GET /health/live`
  - `GET /health/ready`
- **Metrics**:
  - `GET /metrics`

### 1) Build Release limpio (en tu PC de build)

Desde la raíz del repo:

- Ejecutar `ops/2pc/build-release.ps1`
- Se generan:
  - `deploy/server` (API Release publish)
  - `deploy/terminal` (Terminal Release publish)

### 2) Preparar Server (PC1)

#### PostgreSQL

- Instalar PostgreSQL (15+ recomendado).
- Crear DB/usuario (ejemplo):
  - DB: `posedgedb`
  - user: `posedgedb_user`
- Aplicar schema:
  - Ejecutar `db/schema/schema.sql`
- Seed mínimo (solo si estás en modo demo determinístico):
  - Ejecutar `db/seeds/seed.sql`

> Importante: en Production **`Edge:AutoApplySchema=false`** y **`Edge:AutoSeed=false`**. El schema/seed se aplica manualmente.

#### API Release

- Copiar carpeta `deploy/server` al PC1 (mismo path o cualquier carpeta).
- Copiar `ops/2pc/server/appsettings.Production.json` dentro de `deploy/server/` (al lado de `PosEdge.Api.exe`).
- Editar `deploy/server/appsettings.Production.json`:
  - `ConnectionStrings:PosEdge` (host=127.0.0.1 si Postgres está en el mismo PC1)
  - `Edge:OpsKey` (cambiar por una clave real)

#### Arranque

- Ejecutar `ops/2pc/server/start-server.bat` (desde el repo o copiarlo al PC1).

### 3) Preparar Terminal (PC2)

#### Terminal Release

- Copiar carpeta `deploy/terminal` al PC2.
- Copiar `ops/2pc/terminal/appsettings.Production.json` dentro de `deploy/terminal/` (al lado de `PosEdge.Terminal.exe`).
- Editar `deploy/terminal/appsettings.Production.json`:
  - `Api:BaseUrl`: **`http://IP_DEL_PC1:5071`** (NO localhost)

#### Identidad persistente del terminal

Al primer arranque, el terminal genera y persiste:

- `terminalId` en `%LocalAppData%\PosEdge\terminal-config\terminalId.txt`
- `cashSessionId` en `%LocalAppData%\PosEdge\terminal-config\cashSessionId.txt`

Para resetear completamente (SQLite + IDs), usar:

- `ops/2pc/terminal/reset-terminal-cache.bat`

### 4) Red real (PC2 → PC1)

En PC2 ejecutar:

- `ops/2pc/check/connectivity-check.ps1 -ServerIp <IP_PC1> -Port 5071`

Si falla el TCP port o el health:

- Abrir firewall inbound en PC1 para TCP 5071.
- Confirmar que API está escuchando en `0.0.0.0:5071` (Kestrel endpoint en appsettings Production).

### 5) Stop

- API: `ops/2pc/server/stop-server.bat`
- Terminal: cerrar ventana o `taskkill /IM PosEdge.Terminal.exe`

