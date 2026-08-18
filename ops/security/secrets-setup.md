# Secrets Setup (Development and Production)

## Development (User Secrets)
Run in `GrunflexPOS.API`:

```powershell
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=grunflexpos2;Username=postgres;Password=CHANGE_ME"
dotnet user-secrets set "Jwt:SigningKey" "CHANGE_ME_STRONG_64_PLUS_CHARS"
dotnet user-secrets set "Jwt:PreviousSigningKeys:0" "OLD_ROTATED_KEY_IF_APPLIES"
dotnet user-secrets set "Security:AdminPassword" "CHANGE_ME_ADMIN_PASSWORD"
dotnet user-secrets set "Serilog:WriteTo:2:Args:apiKey" "SEQ_API_KEY_OPTIONAL"
```

## Production (Vault / Secret Manager)
Use one of:
- HashiCorp Vault
- Azure Key Vault
- AWS Secrets Manager

Minimum secrets:
- `ConnectionStrings__Default`
- `Jwt__SigningKey`
- `Jwt__PreviousSigningKeys__0` (cuando hagas rotación)
- `Security__AdminPassword`
- `Serilog__WriteTo__2__Args__apiKey` (si Seq requiere API key)

Expose to app as environment variables with `GRUNFLEX_` prefix:
- `GRUNFLEX_ConnectionStrings__Default`
- `GRUNFLEX_Jwt__SigningKey`
- `GRUNFLEX_Jwt__PreviousSigningKeys__0`
- `GRUNFLEX_Security__AdminPassword`
- `GRUNFLEX_Serilog__WriteTo__2__Args__apiKey`

## Security rules
- Never commit secrets to git.
- Rotate JWT signing keys every 90 days.
- Keep previous signing key for at least refresh-token TTL during rotation.
- Rotate admin credentials every 30 days.
