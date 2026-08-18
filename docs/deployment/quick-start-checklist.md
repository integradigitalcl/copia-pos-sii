## Quick start checklist — piloto 2 PCs

### Build

- [ ] En PC de build: `ops/2pc/build-release.ps1`
- [ ] Confirmar que existen:
  - [ ] `deploy/server/PosEdge.Api.exe`
  - [ ] `deploy/terminal/PosEdge.Terminal.exe`

### PC1 (Server)

- [ ] PostgreSQL instalado y corriendo
- [ ] DB creada (`posedgedb`) + user configurado
- [ ] `db/schema/schema.sql` aplicado
- [ ] (Opcional demo) `db/seeds/seed.sql` aplicado
- [ ] `deploy/server/appsettings.Production.json` configurado:
  - [ ] `ConnectionStrings:PosEdge` correcto
  - [ ] `Edge:AutoApplySchema=false`
  - [ ] `Edge:AutoSeed=false`
  - [ ] `Edge:OpsKey` cambiado
- [ ] Firewall inbound permite TCP 5071
- [ ] Server arrancado (`ops/2pc/server/start-server.bat`)
- [ ] Health OK desde PC1:
  - [ ] `GET /health/live` => 200
  - [ ] `GET /health/ready` => 200

### PC2 (Terminal)

- [ ] `deploy/terminal/appsettings.Production.json` configurado:
  - [ ] `Api:BaseUrl = http://IP_PC1:5071` (**NO localhost**)
- [ ] Conectividad OK:
  - [ ] `ops/2pc/check/connectivity-check.ps1 -ServerIp IP_PC1 -Port 5071`
- [ ] Terminal arrancado (`ops/2pc/terminal/start-terminal.bat`)
- [ ] Verificar que persiste IDs:
  - [ ] `%LocalAppData%\PosEdge\terminal-config\terminalId.txt` existe

