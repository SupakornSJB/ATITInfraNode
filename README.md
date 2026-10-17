# ATITInfraNode — IoT Relay Server

ASP.NET Core (.NET 8) server at the center of
[ATITFinalProject](https://github.com/SupakornSJB/ATITFinalProject). It subscribes to
ESP32 controller events over **MQTT with mutual TLS**, works out which player and action each
physical slot belongs to, and forwards clean game events to the Unity dedicated server over
**gRPC with mutual TLS**. It also runs intrusion detection and the backend for the admin
security dashboard.

```
ESP32s ──MQTT/mTLS──▶ Mosquitto ──▶ GameServer (this repo) ──gRPC/mTLS──▶ Unity server
                                        │
                                        └── REST + SignalR ──▶ admin dashboard
```

---

## What it does

For every message on `game/devices/+/slot/+`:

1. **Quarantine check** — events from quarantined devices are dropped
2. **Session resolution** — finds the Unity server that owns the device
3. **Device authentication** — pluggable `IDeviceAuthenticator` (identity itself is enforced
   by the broker's certificate check)
4. **Rate limiting** — per-device events/second above `IoTGame:MaxEventsPerSecond` raise an
   alert and quarantine the device
5. **Mapping** — looks up `(deviceId, slotId)` in the `DeviceRegistry`; unknown slots raise an
   alert and are dropped
6. **Routing by role:**
   - `player` → buffered `InputEvent` for that player (`move-x`, `fire`, …) → `SendInputBatch`
   - `stage` → forwarded as `stage` input; Unity turns `admin-start` / `admin-end` into
     round start / end
   - `environment` → `SendEnvironmentEvent`; ultrasonic distance goes through
     `ZoneResolverService`, which applies hysteresis and only emits zone 1–4 changes;
     `auxiliaryKeys` fan out extra sensor values (e.g. DHT11 humidity → `spawn_rate`)

Unity receives `playerId` + a semantic `inputName` or `envKey`; the device id is passed along
for diagnostics only, and slot ids and raw sensor names never reach it.

---

## Projects

| Project | Role |
|---|---|
| `IoTGame.Core/` | **Library** — MQTT subscriber, input buffer + rate limiter, zone resolver, gRPC relay, session manager, admin/session endpoints, and the extension interfaces |
| `GameServer/` | **This game's app** — wires the library with game-specific implementations, the device registry, JWT auth, the security API and SignalR hub |

`IoTGame.Core` is game-agnostic. A game plugs in its own behaviour through the builder in
`GameServer/Program.cs`:

```csharp
builder.Services.AddIoTGame(options => { /* from configuration */ })
    .WithSessionStore<InMemorySessionStore>()
    .WithDeviceAuthenticator<AllowAllDeviceAuthenticator>()
    .WithAdminAuthenticator<ApiKeyAdminAuthenticator>()
    .WithPlayerMapper<StaticDeviceRegistryMapper>()     // DeviceRegistry → players
    .WithSessionResolver<StaticSessionResolver>()        // single Unity endpoint
    .WithIntrusionDetector<RuleBasedIntrusionDetector>() // alerts + quarantine + SignalR
    .WithSessionTokenAuth();
```

```
IoTGame.Core/
├── Abstractions/   IDeviceAuthenticator, IPlayerMapper, ISessionResolver, ISessionStore,
│                   IAdminAuthenticator, IIntrusionDetector
├── Services/       MqttSubscriberService, InputBufferService, ZoneResolverService,
│                   UnityRelayService, SessionManager, GameStateService
├── Endpoints/      /health, /api/admin/*, /api/sessions/*
├── Auth/ Storage/ Models/ Extensions/
└── Proto/          game_controller_v2.proto — gRPC contract shared with Unity
GameServer/
├── Program.cs      Composition root, JWT, CORS (dev), static dashboard hosting
├── Mapping/        StaticDeviceRegistryMapper, StaticSessionResolver
├── Security/       RuleBasedIntrusionDetector
├── Controllers/    AuthController (/api/auth), SecurityController (/api/security)
├── Hubs/           SecurityHub (/hubs/security)
├── Data/           EF Core + SQLite dashboard users
└── Scripts/        seed_admin.py, rogue_device.py
```

---

## API

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /health` | — | Readiness probe |
| `POST /api/auth/login` | — | Dashboard login → JWT |
| `GET /api/security/status`, `/devices`, `/alerts` | JWT | Device security state and alert history |
| `PUT /api/security/devices/{id}/quarantine?quarantine=true\|false` | JWT | Manually quarantine / release a device |
| `POST /api/security/devices/{id}/quarantine`, `/unquarantine` | JWT | Same, as separate actions |
| `POST /api/security/alerts/clear` | JWT | Clear the alert list |
| `POST /api/admin/start`, `/end`, `/reset` | Admin API key¹ | Round control on the Unity server |
| `/api/sessions/*` | Admin API key¹ | Create / start / end sessions, assign devices, issue join tokens |
| SignalR `/hubs/security` | JWT | Pushes `OnAlert` and `OnDeviceStateChanged` in real time |

¹ `X-IoTGame-Key` header, checked against `IoTGame:AdminApiKey`. If the key is empty these
endpoints are open — see [Known limitations](#known-limitations).

---

## Intrusion detection

`RuleBasedIntrusionDetector` tracks every device and raises alerts that are logged, kept (last
500) and broadcast to the dashboard:

| Alert | Trigger | Quarantines |
|---|---|---|
| `RateExceeded` | More events/second than `MaxEventsPerSecond` | Yes |
| `UnauthorizedDevice` | Device authenticator rejects the device | Yes |
| `UnknownSlot` | Slot not in the device's registry entry | No |
| `UnknownDevice` | No session for the device | No |
| `ManualQuarantine` | Operator clicks *Quarantine* in the dashboard | Yes |

`Scripts/rogue_device.py` is a demo attacker (`no-cert`, `flood`, `spoof-slot`, `spoof-device`);
see [Known limitations](#known-limitations) — it has not been re-tested since Phase 6.

---

## Device registry

`GameServer/appsettings.json` maps physical slots to meaning — remapping a controller is a
config change, not a firmware or Unity change:

```json
"DeviceRegistry": {
  "devices": {
    "esp32-01": {
      "defaultPlayerId": "player-1",
      "inputs": {
        "joystick-x": { "type": "knob",       "inputName": "move-x",      "inputRole": "player" },
        "btn-1":      { "type": "button",     "inputName": "admin-start", "inputRole": "stage" },
        "dht11":      { "type": "dht11",      "inputRole": "environment", "envKey": "unused",
                        "auxiliaryKeys": { "humidity": "spawn_rate" } },
        "distance":   { "type": "ultrasonic", "inputRole": "environment", "envKey": "ultrasonic_zone" }
      }
    }
  }
}
```

Optional per-slot fields: `playerId` (overrides `defaultPlayerId`) and `minRaw` / `maxRaw`
(normalize a raw value to 0.0–1.0; the firmware already normalizes knobs).

---

## Running

Certificates, broker setup and secrets are covered step by step in the
[setup guide](https://github.com/SupakornSJB/ATITFinalProject/blob/main/docs/setup.md).
In short:

```bash
cd GameServer
# copy ca.crt, device.crt, device.key (CN=gameserver) into Certs/
dotnet user-secrets set "Dashboard:JwtSecret" "$(openssl rand -base64 48)"
dotnet run                                        # https://localhost:5000
python Scripts/seed_admin.py "<admin-password>"   # once, creates the dashboard login
```

The server refuses to start without a `Dashboard:JwtSecret` of at least 32 bytes. Outside
Development, supply secrets as environment variables (`Dashboard__JwtSecret`,
`IoTGame__AdminApiKey`, …).

Key settings in `appsettings.json`:

| Key | Meaning |
|---|---|
| `Mqtt:Host`, `Mqtt:Port`, `Mqtt:UseTls` | Broker connection |
| `Mqtt:CaFile`, `Mqtt:ClientCertFile`, `Mqtt:ClientKeyFile` | Broker mTLS (paths relative to `GameServer/`) |
| `Unity:GrpcEndpoint`, `Unity:GrpcClientCertFile`, `Unity:GrpcClientKeyFile` | Unity gRPC server and the client cert presented to it |
| `IoTGame:BufferWindowMs` | `0` = forward immediately; `>0` = batch window in ms |
| `IoTGame:MaxEventsPerSecond` | Per-device rate limit (default `60`) |

The [admin dashboard](https://github.com/SupakornSJB/atit-dashboard) is a separate React app.
In development it runs on Vite and proxies to
this server; a production build copied into `GameServer/wwwroot/dashboard/` is served at
`/dashboard`.

---

## Known limitations

- **Admin endpoints vs. dashboard:** `/api/admin/*` accepts only the API key. With
  `AdminApiKey` empty they are open to anyone; with it set, the dashboard's Start/End buttons
  (which send a JWT) are rejected. Accepting either credential would fix both.
- **Unity server certificate check depends on the endpoint** (`UnityRelayService`): for
  `https://localhost` / `https://127.*` the relay accepts any server certificate. For any other
  host it uses the operating system's trust store and hostname check, so a Unity certificate
  signed by the project CA is rejected unless `ca.crt` is installed as a trusted root and the
  certificate's name matches the host in `Unity:GrpcEndpoint`. Running the relay and Unity on
  the same machine avoids this.
- **`rogue_device.py` is untested since Phase 6:** `spoof-slot` publishes to a literal
  `{device_id}` topic (missing f-string), so the broker ACL drops it and no alert fires; its
  defaults (port 1883, `../Certs/esp32-02/…`) don't match the setup guide, and it reports
  success even when the client certificate wasn't loaded.
- `AllowAllDeviceAuthenticator` and `StaticSessionResolver` are single-game defaults; the
  interfaces exist to replace them.

---

## License

MIT — see [LICENSE](LICENSE).
