## Troubleshooting básico — 2 PCs

### No conecta PC2 → PC1

- **Síntoma**: `connectivity-check.ps1` falla en TCP o en `/health/*`.
- **Acciones**:
  - Verificar IP del servidor (PC1) y que ambos estén en la misma red.
  - En PC1 abrir firewall inbound para **TCP 5071**.
  - Confirmar API en PC1 está corriendo (proceso `PosEdge.Api.exe`).
  - Confirmar Kestrel escucha en `http://0.0.0.0:5071` (Production appsettings).

### `/health/ready` devuelve 503

- **Causa típica**: Postgres no disponible / connection string incorrecto / schema no aplicado.
- **Acciones**:
  - Revisar `ConnectionStrings:PosEdge` en `deploy/server/appsettings.Production.json`.
  - Confirmar Postgres activo.
  - Confirmar schema aplicado (`db/schema/schema.sql`).

### Terminal quedó “offline” pero no drena

- **Acciones mínimas (sin tocar arquitectura)**:
  - Reiniciar terminal (cerrar y abrir).
  - Si querés reset completo: `ops/2pc/terminal/reset-terminal-cache.bat` (borra SQLite e IDs persistidos).
  - Verificar conectividad otra vez (health/live y ready).

### Quiero confirmar “no dead-letter / no inflight pegados”

- En terminal:
  - Usar comando `status` en consola del terminal.
- En server:
  - Revisar `/metrics` (si lo estás scrapeando) y logs.
  - (Opcional) usar endpoints ops existentes (requieren `Edge:OpsKey`).

