# GrunflexPOS Hardware Bridge

Local-only .NET 8 bridge for hardware that cannot be reached from a browser.
It binds exclusively to `http://127.0.0.1:7390`; it does not listen on LAN
interfaces.

## Security

Every endpoint requires:

```http
Authorization: Bearer <bridge-token>
```

On a normal Windows install the token is generated once and protected with
Windows DPAPI (`CurrentUser`) at:

`%LOCALAPPDATA%\GrunflexPOS\HardwareBridge\token.dpapi`

The token file is deliberately not a plaintext configuration secret. The POS
host that starts this bridge should read it through the same Windows user
context, or provision a client-specific token integration. In Development,
`HardwareBridge:DevelopmentToken` may be configured in
`appsettings.Development.json`; it is ignored outside the Development
environment and must be at least 16 characters.

CORS and requests carrying an `Origin` header are restricted to HTTP
`localhost`, `127.0.0.1`, and `::1`. Requests without an Origin are allowed for
native desktop clients. Binding to loopback remains the network boundary.

## Protocol

All JSON uses camel-case property names. Error responses contain an `error`
property. Examples use PowerShell:

```powershell
$token = "your-token"
$headers = @{ Authorization = "Bearer $token" }
Invoke-RestMethod http://127.0.0.1:7390/health -Headers $headers
```

| Method | Path | Request | Result |
| --- | --- | --- | --- |
| GET | `/health` | none | bridge status |
| GET | `/api/printers` | none | installed Windows printer names |
| GET | `/api/serial-ports` | none | available COM ports |
| POST | `/api/print/raw` | `{ "printer": "Receipt", "dataBase64": "..." }` | raw job accepted |
| POST | `/api/drawer/open` | `{ "mode": "com", "device": "COM3" }` | drawer opened |
| GET | `/api/scanner/state` | none | current scanner state |
| POST | `/api/scanner/connect` | scanner serial settings | scanner connected |
| POST | `/api/scanner/disconnect` | none | scanner disconnected |
| GET | `/api/scanner/events` | none | authenticated Server-Sent Events stream |

`/api/drawer/open` accepts `mode` `com` (a present COM port) or `printer`
(an installed Windows printer receiving a RAW spooler job). The exact drawer
command is the existing WPF command:

`1B 70 00 19 FA`

Scanner defaults intentionally match `LectorCodigoService`: 9600 baud, 8 data
bits, no parity, one stop bit, and no handshake. Scanner lines are ASCII and
emit a `code` event when terminated by CR or LF:

```text
event: scanner
data: {"type":"code","code":"7501234567890","port":"COM3","error":null,"at":"..."}
```

The scanner endpoint supports one active serial connection. Multiple event
clients may subscribe; each receives the broadcast events. Disconnect the
stream by cancelling the HTTP request.

## Build and run

From this directory:

```powershell
dotnet build -c Release
dotnet run --environment Development
```

For a local publish/run or Windows installation, see
`scripts\install-bridge.ps1`. The script publishes a self-contained
single-file executable under `%LOCALAPPDATA%\GrunflexPOS\HardwareBridge` and
can optionally register a Scheduled Task.

The project intentionally is separate from the existing WPF and web UI
projects. The migration can add a project reference or HTTP client later
without coupling UI code to Windows printer/serial APIs.
