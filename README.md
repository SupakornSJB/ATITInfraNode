# ATITInfraNode

ASP.NET Core server that acts as the central hub in an IoT-powered physical game controller system. It bridges ESP32 button devices (over MQTT) and a Unity dedicated game server (over gRPC), handling device-to-player identity mapping and protocol translation.

## System Architecture

```
[ESP32 #1]  ──┐
[ESP32 #2]  ──┤  MQTT (plain TCP or TLS)
[ESP32 #N]  ──┘  topic: game/devices/{deviceId}/slot/{slotId}
                  broker: HiveMQ Cloud or local
                        │
                        ▼
              [ATITInfraNode]               ← this project
                - Resolves (deviceId, slotId) → (playerId, inputName)
                - Normalizes raw sensor values
                - Forwards to Unity over gRPC
                        │
                        │  gRPC (HTTP/2 + Protobuf)
                        ▼
              [Unity Dedicated Server]
                - Receives resolved input events
                - Runs game logic via Netcode for GameObjects
```

## What It Does

1. Subscribes to `game/devices/+/slot/+` — one wildcard catches all devices and slots
2. On each MQTT message, looks up `(deviceId, slotId)` in the device registry to resolve `playerId` and `inputName`
3. Normalizes raw ADC values (e.g. knobs) to `0.0–1.0`
4. Forwards the resolved event to Unity via gRPC `SendInputBatch`
5. Applies per-slot rate limiting (default 60 events/sec) to protect against sensor floods
6. Exposes `GET /health` for deployment readiness probes

Unity only ever receives `{ playerId, inputName, value, ... }` — it never sees physical device topology.

## Tech Stack

- .NET 10 / ASP.NET Core (minimal API)
- [MQTTnet](https://github.com/dotnet/MQTTnet) v4
- gRPC via `Grpc.Net.Client` + `Grpc.Tools` + `Google.Protobuf`

## Project Structure

```
ATITInfraNode/
├── Proto/
│   └── game_controller_v2.proto     # gRPC contract shared with Unity
├── Services/
│   ├── MqttSubscriberService.cs     # IHostedService — MQTT listener
│   ├── DeviceRegistryService.cs     # (deviceId, slotId) → (playerId, inputName)
│   ├── InputBufferService.cs        # Optional batching + rate limiting
│   └── UnityRelayService.cs         # gRPC client
├── Models/
│   ├── InputEvent.cs                # Resolved internal event DTO
│   └── DeviceRegistryConfig.cs      # Typed config models
├── Program.cs
├── appsettings.json
└── appsettings.Development.json
```

## Setup

### 1. Install dependencies

```bash
dotnet restore
```

### 2. Set MQTT credentials (dev)

```bash
cd ATITInfraNode
dotnet user-secrets set "Mqtt:Username" "your-hivemq-username"
dotnet user-secrets set "Mqtt:Password" "your-hivemq-password"
```

### 3. Configure `appsettings.json`

| Key | Description |
|---|---|
| `Mqtt:Host` | MQTT broker hostname (e.g. `abc123.s1.eu.hivemq.cloud` or local IP) |
| `Mqtt:Port` | `1883` for plain TCP, `8883` for TLS |
| `Unity:GrpcEndpoint` | Unity server address (e.g. `http://192.168.1.50:9090`) |
| `IoTGame:BufferWindowMs` | `0` = forward immediately; `>0` = batch window in ms |
| `IoTGame:MaxEventsPerSecond` | Per-slot rate limit (default `60`) |

### 4. Configure the device registry

The registry maps physical hardware slots to player IDs and semantic input names. Keys under `"inputs"` are `slotId` values (matching what the firmware publishes); `inputName` is what Unity receives.

```json
"DeviceRegistry": {
  "players": {
    "player-1": ["esp32-01"],
    "player-2": ["esp32-shared"],
    "player-3": ["esp32-shared"]
  },
  "devices": {
    "esp32-01": {
      "defaultPlayerId": "player-1",
      "inputs": {
        "btn1":  { "type": "button", "inputName": "jump" },
        "knob1": { "type": "knob",   "inputName": "speed", "minRaw": 0, "maxRaw": 4095 }
      }
    },
    "esp32-shared": {
      "defaultPlayerId": null,
      "inputs": {
        "btn1": { "type": "button", "inputName": "jump", "playerId": "player-2" },
        "btn2": { "type": "button", "inputName": "jump", "playerId": "player-3" }
      }
    }
  }
}
```

`minRaw`/`maxRaw` on knob inputs cause the server to normalize raw ADC values to `0.0–1.0` before forwarding to Unity.

**Player resolution priority per slot:**
1. Slot-level `playerId` override
2. Device `defaultPlayerId`
3. Neither set → event is dropped with a warning

## MQTT Message Format (from firmware)

Topic: `game/devices/{deviceId}/slot/{slotId}`

```json
{
  "deviceId": "esp32-01",
  "slotId": "btn1",
  "inputType": "button",
  "event": "pressed",
  "value": 1.0,
  "timestamp": 1234567890,
  "sequenceNumber": 42
}
```

## Running Locally

```bash
dotnet run --project ATITInfraNode
```

Check health: `curl http://localhost:5025/health`

## Production Environment Variables

Use double-underscore (`__`) as the .NET config delimiter:

```bash
Mqtt__Host=abc123.s1.eu.hivemq.cloud
Mqtt__Port=8883
Mqtt__Username=your-user
Mqtt__Password=your-password
Unity__GrpcEndpoint=https://unity-server:9090
```