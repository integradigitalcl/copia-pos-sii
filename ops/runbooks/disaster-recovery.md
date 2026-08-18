# Disaster Recovery Runbook

## Objectives
- RPO: 15 minutes
- RTO: 60 minutes

## Inputs required
- PostgreSQL connection string
- Latest full backup `.dump`
- WAL archive location (if available)

## Procedure
1. Announce incident and freeze write traffic to API.
2. Provision/validate healthy PostgreSQL instance.
3. Restore latest valid backup:
   - `pwsh ./ops/backups/restore-postgres.ps1 -ConnectionString "<conn>" -BackupFile "<file.dump>"`
4. Apply migrations:
   - `dotnet ef database update --project GrunflexPOS.API --startup-project GrunflexPOS.API`
5. Validate:
   - API health endpoint `/health` returns healthy.
   - smoke tests for login, products, pagos.
6. Re-enable traffic progressively (canary 10%, 50%, 100%).
7. Post-incident review and timeline.

## Validation checklist
- [ ] Backup integrity validated
- [ ] DB constraints intact
- [ ] API auth working
- [ ] Critical endpoints responding < 1s
- [ ] Logs and metrics normal
