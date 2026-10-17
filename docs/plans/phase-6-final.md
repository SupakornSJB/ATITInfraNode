# Phase 6 — Final Implementation Summary

## Overview

Phase 6 adds two parallel security layers to the IoT game controller system:

1. **Admin Security Dashboard Backend** — real-time intrusion monitoring via SignalR, REST API, JWT authentication, and SQLite persistence
2. **Mutual TLS Firmware Authentication** — ESP32 devices authenticate to the MQTT broker using X.509 client certificates instead of shared credentials

---

## Part 1: Admin Security Dashboard Backend (`GameServer/`)

### Purpose

Provides a real-time admin web dashboard to monitor connected IoT devices, track intrusion events, and take manual action (quarantine/unquarantine devices). The React frontend SPA is served directly by the ASP.NET server.

---

### Authentication: JWT + BCrypt

**Login flow:**
- `POST /api/auth/login` — accepts `{ username, password }` over HTTPS
- Server verifies plaintext password against a bcrypt hash (cost 12) stored in SQLite using `BCrypt.Verify` (constant-time, no timing attack)
- Returns a signed JWT (HS256, 60-minute expiry)
- Returns the same `"Invalid credentials"` error for wrong username and wrong password — prevents username enumeration

**Password storage:**
- Admin users stored in `dashboard.db` (SQLite, gitignored)
- Plaintext password never touches the database
- `Scripts/seed_admin.py` — one-time setup script that bcrypt-hashes a password and inserts the admin user directly into SQLite

**JWT claims:**
- `ClaimTypes.Name` (username), `ClaimTypes.Role` (role), `Jti` (unique token ID for future revocation)
- Issued as `ATITFinalProject` audience `dashboard`

---

### SignalR Real-Time Push (`SecurityHub`)

- Hub endpoint: `/hubs/security` — requires valid JWT
- Server-to-client push only (no client → server methods)
- Push events:
  - `OnAlert(IntrusionAlert)` — fired when the intrusion detector raises an alert
  - `OnDeviceStateChanged(DeviceSecurityState)` — fired on quarantine/unquarantine state changes
- **WebSocket JWT extraction:** browsers cannot set `Authorization` headers on WebSocket upgrades, so JWT is extracted from the `access_token` query parameter and injected into the authentication pipeline via `JwtBearerEvents.OnMessageReceived`

---

### Security REST API (`SecurityController`)

All endpoints require `[Authorize]` (valid JWT in `Authorization: Bearer` header).

| Endpoint | Description |
|---|---|
| `GET /api/security/status` | Full snapshot — all device states + recent alert feed (initial load) |
| `GET /api/security/devices` | Device state list only |
| `GET /api/security/alerts` | Recent alert feed only |
| `POST /api/security/devices/{deviceId}/quarantine` | Manually quarantine a device |
| `POST /api/security/devices/{deviceId}/unquarantine` | Lift quarantine + push `OnDeviceStateChanged` via SignalR |
| `POST /api/security/alerts/clear` | Clear alert feed (cosmetic, does not affect device state) |

---

### Intrusion Detection (`RuleBasedIntrusionDetector`)

Implements `IIntrusionDetector` — sits inside the MQTT event pipeline and evaluates every incoming event.

**Rules evaluated per event:**
1. **Rate limiting** — rolling 1-second window per `deviceId:slotId`. If events exceed `MaxEventsPerSecond` (default: 60), the device is auto-quarantined and an `AlertType.RateExceeded` alert is raised
2. **Unknown slot** — if `slotId` is not in the device registry, raises `AlertType.UnknownSlot`
3. **Unknown device** — if `deviceId` is not in the registry at all, raises `AlertType.UnknownDevice`
4. **Unauthorized device** — if `IDeviceAuthenticator` rejects the device, raises `AlertType.UnauthorizedDevice`

**Quarantine behaviour:**
- Quarantined devices have all subsequent events dropped before any processing
- Quarantine is in-memory; lifted via REST API or server restart
- `Unquarantine()` restores the device to `Active` state and pushes a SignalR update

**Alert history:**
- Stored in a bounded in-memory ring buffer (max 500 alerts, LIFO)
- Available via `GetRecentAlerts()`

**`DeviceSecurityState` model tracks per device:**
- `IsQuarantined`, `AlertCount`, `LastSeen`, `CurrentEventRate`, `SequenceNumber`

**`IntrusionAlert` model:**
- `DeviceId`, `AlertType` (enum: `RateExceeded`, `UnknownSlot`, `UnknownDevice`, `UnauthorizedDevice`, `ManualQuarantine`), `Detail`, `Timestamp`, `ShouldQuarantine`

---

### Core Library Additions (`IoTGame.Core/`)

**New interface:**
```csharp
// IoTGame.Core/Abstractions/IIntrusionDetector.cs
public interface IIntrusionDetector
{
    Task<bool> EvaluateAsync(string deviceId, string slotId);
    Task ReportAsync(IntrusionAlert alert);
    void Unquarantine(string deviceId);
    IEnumerable<DeviceSecurityState> GetAllDeviceStates();
    IEnumerable<IntrusionAlert> GetRecentAlerts();
    void ClearAlerts();
}
```

**Default no-op implementation:**
- `NoOpIntrusionDetector` — passes all events, never quarantines. Used when security monitoring is not needed.

**New models:**
- `DeviceSecurityState` — per-device runtime security state
- `IntrusionAlert` — alert record with type, detail, and quarantine flag

---

### Data Layer

**`DashboardDbContext` + `DashboardUser` (EF Core + SQLite):**
- `Users` table: `Id`, `Username` (unique index), `PasswordHash` (bcrypt), `Role`, `CreatedAt`, `LastLoginAt`
- `dashboard.db` auto-created on startup via `EnsureCreated()`
- Gitignored: `dashboard.db`, `dashboard.db-shm`, `dashboard.db-wal`

---

### Static File Serving (React SPA)

- Built React app served from `wwwroot/dashboard/`
- All `/dashboard/*` routes fall back to `index.html` (client-side routing)
- Dev CORS: `http://localhost:5173` (Vite) allowed with `AllowCredentials()` — required for SignalR

---

### `Program.cs` Changes

```
Services added:   AddDbContext (SQLite), AddSignalR(), AddAuthentication (JWT Bearer),
                  AddAuthorization(), AddControllers()
Middleware order: UseAuthentication → UseAuthorization → UseStaticFiles
Endpoints added:  MapHub<SecurityHub>("/hubs/security"), MapControllers(),
                  MapFallbackToFile("/dashboard/{**path}", "dashboard/index.html")
Startup:          EnsureCreated() for auto-DB creation
```

---

### New NuGet Packages

| Package | Purpose |
|---|---|
| `Microsoft.EntityFrameworkCore.Sqlite` | SQLite user database |
| `Microsoft.EntityFrameworkCore.Design` | EF Core tooling |
| `BCrypt.Net-Next` | bcrypt password hashing |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | JWT validation |
| `System.IdentityModel.Tokens.Jwt` | JWT token generation |

---

## Part 2: Mutual TLS Firmware Authentication (ESP32 Firmware Project)

### Threat Model

| Threat | Before Phase 6 | After Phase 6 |
|---|---|---|
| Flash dump exposes MQTT credentials | Credentials readable in plaintext | Certificate only — useless without CA-signed private key |
| Rogue device with stolen credentials | Would connect successfully | Rejected — cert not signed by trusted CA |
| Man-in-the-middle on MQTT | Prevented by TLS | Still prevented |
| Tampered firmware | Would work if credentials copied | Cert + private key both needed — raises the bar |

> **Known limitation:** Full firmware tamper protection requires ESP32 secure boot (eFuse burning). This is irreversible and out of scope. The PKI approach is documented as a significant security improvement, not a complete solution.

---

### PKI Structure

```
Project CA (root)
  └── esp32-device-1.crt   (CN = "esp32-device-1")
  └── esp32-device-2.crt   (CN = "esp32-device-2")
  └── rogue.crt            (CN = "rogue" — for demo attack scripts)
```

Each device receives three files flashed onto its filesystem:
- `ca.crt` — CA certificate (verifies broker's server cert)
- `device.crt` — device client certificate
- `device.key` — device private key (never committed to git)

---

### Certificate Generation Scripts

**`scripts/gen_ca.sh`** — run once per project:
- Generates 4096-bit RSA CA key + self-signed cert (10-year validity)
- Subject: `/C=TH/O=ATITFinalProject/CN=ATITFinalProject-CA`
- `ca.key` stays on the provisioning machine, never distributed

**`scripts/gen_device_cert.sh <device-id>`** — run once per device:
- Generates 2048-bit RSA device key (smaller for ESP32 performance)
- CN must match the `deviceId` in the device registry
- CSR is generated, signed by CA, then deleted — only `.crt` is kept
- 2-year validity

**`scripts/provision_device.py`** — flashes certs onto ESP32 via `mpremote`:
- Uploads `ca.crt`, `device.crt`, `device.key` to the device filesystem
- Verify after: `mpremote connect <port> ls`

---

### Firmware Changes

**`iotgame/mqtt.py` — mutual TLS connection:**
- Loads CA cert, client cert, and private key from filesystem at connect time
- Sets `ssl_params["cert_reqs"] = ussl.CERT_REQUIRED` — validates broker cert against CA
- Passes client cert and key for mutual authentication
- `MQTT_USER` and `MQTT_PASSWORD` set to `None` — broker uses cert CN as identity

**`game/config.py` — updated config:**
- Removed: `MQTT_USER`, `MQTT_PASSWORD`
- Added: `CA_CERT_PATH = "ca.crt"`, `CERT_PATH = "device.crt"`, `KEY_PATH = "device.key"`
- `DEVICE_ID` must match the CN in the device certificate

---

### Mosquitto Broker Configuration

```
require_certificate true          # no cert = no connection
use_identity_as_username true     # cert CN becomes MQTT username (= deviceId)
acl_file /etc/mosquitto/acl.conf
```

**ACL (`acl.conf`):**
```
# Each device can only publish to its own topic prefix
pattern write game/devices/%u/#

# ASP.NET server subscribes to all device topics
user aspnet-server
topic read game/devices/#
```

The `%u` substitution uses the MQTT username, which equals the cert CN, which equals the `deviceId`. A compromised device cannot publish to another device's topics.

---

### Rogue Device Simulator (`tools/rogue_device.py`)

Python script (uses `paho-mqtt`) for three demo attack scenarios:

| Command | Attack | Expected result |
|---|---|---|
| `rogue_device.py no-cert` | Connect without client certificate | SSL rejected at TLS handshake |
| `rogue_device.py flood` | Send 200 events/sec for 5 seconds | Device quarantined within 5 seconds (rate limit) |
| `rogue_device.py spoof-slot` | Publish unknown `slotId` `"inject-admin"` | `UnknownSlot` alert appears in dashboard |

Attacks 2 and 3 use a valid `rogue` cert (device is in PKI but not in the game registry), demonstrating that TLS authentication alone is not sufficient — the application-layer registry check is also necessary.

---

## File Inventory (Phase 6 Additions)

### `GameServer/` (new files)
```
Controllers/AuthController.cs           ← POST /api/auth/login
Controllers/SecurityController.cs       ← /api/security/* endpoints
Data/DashboardDbContext.cs              ← EF Core SQLite context
Data/DashboardUser.cs                   ← User entity
Hubs/SecurityHub.cs                     ← SignalR hub
Models/DashboardModels.cs               ← LoginRequest/LoginResponse DTOs
Scripts/seed_admin.py                   ← Admin user seeding utility
Scripts/deploy-dashboard.sh             ← React build deploy script
Certs/ca.crt                            ← CA cert (committed)
Certs/device.crt                        ← Server/device cert (committed)
```

### `IoTGame.Core/` (new files)
```
Abstractions/IIntrusionDetector.cs      ← Intrusion detection interface
Auth/NoOpIntrusionDetector.cs           ← No-op default implementation
Models/DeviceSecurityState.cs           ← Per-device runtime security state
Models/IntrusionAlert.cs                ← Alert record model
```

### `GameServer/Security/` (new)
```
RuleBasedIntrusionDetector.cs           ← Full IIntrusionDetector implementation
```

### Firmware project (separate repo — documented here)
```
scripts/gen_ca.sh                       ← Root CA generation
scripts/gen_device_cert.sh              ← Per-device cert signing
scripts/provision_device.py             ← ESP32 cert flashing via mpremote
tools/rogue_device.py                   ← Attack simulator for demo
iotgame/mqtt.py                         ← Updated: mutual TLS support
game/config.py                          ← Updated: cert paths replace credentials
certs/.gitignore                        ← Ignores *.key — commits *.crt
```

---

## Known Limitations (Documented)

| Limitation | Production solution |
|---|---|
| No MFA on dashboard login | Add TOTP (Google Authenticator) |
| JWT not revocable | Token blacklist in Redis, or short expiry + refresh tokens |
| Single admin user | User management endpoints with role-based access |
| No login rate limiting | `AspNetCoreRateLimit` — 5 attempts/minute |
| ESP32 secure boot not implemented | eFuse burning — irreversible, out of scope for demo |

---

## Setup Checklist (Before Demo)

```
[ ] dotnet add packages (5 new NuGet packages in GameServer)
[ ] Set Dashboard:JwtSecret (≥32 chars) in appsettings.json
[ ] dotnet run once → dashboard.db auto-created
[ ] python Scripts/seed_admin.py <password> → admin user inserted
[ ] bash scripts/gen_ca.sh → ca.crt + ca.key generated
[ ] bash scripts/gen_device_cert.sh esp32-device-1 → device cert signed
[ ] bash scripts/gen_device_cert.sh rogue → rogue cert for attack demo
[ ] python scripts/provision_device.py esp32-device-1 → certs on ESP32
[ ] Mosquitto: require_certificate true, acl_file updated, service restarted
[ ] npm run build + copy to wwwroot/dashboard/ (optional — SPA)
[ ] POST /api/auth/login correct credentials → JWT returned
[ ] GET /api/security/status with JWT → device list + alert feed
[ ] python tools/rogue_device.py no-cert → SSL rejected ✓
[ ] python tools/rogue_device.py flood → quarantine in dashboard ✓
[ ] python tools/rogue_device.py spoof-slot → unknown slot alert ✓
```
