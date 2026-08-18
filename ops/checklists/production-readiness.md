# Production Readiness Checklist

## Security
- [x] `GRUNFLEX_ConnectionStrings__Default` soportado por configuración
- [x] `GRUNFLEX_Jwt__SigningKey` soportado y rotación disponible (`rotate-jwt-key.ps1`)
- [x] `GRUNFLEX_Security__AdminPassword` soportado por configuración
- [x] HTTPS enforced and HSTS enabled
- [x] JWT auth validado end-to-end (`smoke-auth.ps1`)
- [x] Rate limiting activo
- [x] Security headers middleware activo

## Data
- [x] Scheduled backup scripts listos (`ops/backups/backup-postgres.ps1`)
- [x] Restore script listo (`ops/backups/restore-postgres.ps1`)
- [x] DR runbook documentado (`ops/runbooks/disaster-recovery.md`)
- [ ] Backup retention policy applied (7/30/90 days) en infraestructura real

## Operations
- [x] Serilog con soporte Seq/File/Console
- [x] `/health` expuesto
- [x] `/metrics` expuesto y stack observabilidad de referencia (`ops/observability`)
- [ ] Alerts configuradas en plataforma de monitoreo real

## Quality
- [x] Unit tests passing
- [x] Integration tests passing
- [x] CI/CD pipeline definido (`ci-cd.yml`)
- [x] Security scans pipeline (`security.yml`)
