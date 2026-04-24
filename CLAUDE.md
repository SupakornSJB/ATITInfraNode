# CLAUDE.md — ASP.NET Core Server

## Update Logs

Daily logs of problems encountered and solutions applied are in the `updates/` folder.
Read the relevant date file for context on recent changes before diving into code.

- [2026-04-24](updates/2026-04-24.md) — MQTT TLS/port mismatch crash on startup
- [2026-04-25](updates/2026-04-25.md) — Phase 2 (multi-input/device/player), gRPC SSL fix, logging, Phase 2.1 slot/inputName separation

## What This Project Is

This is one of three components in an **IoT-powered physical game controller system**.
This server is the central hub — it sits between the physical ESP32 button devices and
the Unity game server, handling identity mapping, protocol translation, and routing.
It is the only component that speaks both MQTT (to the IoT devices) and gRPC (to Unity).

## Full System Architecture

```
[ESP32 #1]  ──┐
[ESP32 #2]  ──┤  MQTT publish over TLS
[ESP32 #N]  ──┘  topic: game/controllers/{deviceId}/button
                  broker: HiveMQ Cloud
                        │
                        ▼
              [ASP.NET Core Server]          <- THIS PROJECT
                - Subscribes to all MQTT button topics (wildcard)
                - Maps deviceId → playerId via device registry
                - Forwards input to Unity over gRPC
                        │
                        │  gRPC (HTTP/2 + Protobuf)
                        ▼
              [Unity Dedicated Server]       <- sibling project: /unity-game-server
                - Embedded gRPC server receives input
                - Feeds input into NGO server-side game logic
                - Replicates game state to clients via NGO
                        │
                        │  Netcode for GameObjects (NGO/UTP)
                        ▼
              [Unity Game Clients]
                - Players view the game on screen
                - Game state is driven by physical button presses
```

## What the Completed Server Does

When fully implemented, this server:
1. Starts as a background service and connects to HiveMQ Cloud via MQTT
2. Subscribes to `game/controllers/+/button` — one wildcard subscription catches all devices
3. On each incoming MQTT message:
    - Deserializes the JSON payload `{ deviceId, event, timestamp }`
    - Looks up the `deviceId` in the device registry to find the corresponding `playerId`
    - Drops the event with a warning if the device is unregistered
    - Calls the Unity gRPC server with a `ButtonEventRequest` containing `playerId`, `eventType`, and `timestamp`
4. Exposes a `GET /health` endpoint for cloud deployment readiness probes
5. All secrets (MQTT credentials, Unity endpoint) are provided via environment variables

The server is stateless beyond the device registry config — it does not store events
or maintain game state. It is purely a translation and routing layer.

## This Component's Responsibilities

| Responsibility | Owner |
|---|---|
| Read physical button GPIO | ❌ ESP32 firmware |
| Receive MQTT button events | ✅ This project |
| Map deviceId → playerId | ✅ This project |
| Validate / filter unknown devices | ✅ This project |
| Forward input to Unity via gRPC | ✅ This project |
| Game logic | ❌ Unity server |
| Replicate game state to clients | ❌ Unity server |

## MQTT Message Format (incoming from ESP32)

Topic: `game/controllers/{deviceId}/button`

```json
{
  "deviceId": "esp32-01",
  "event": "pressed",
  "timestamp": 1234567890
}
```

## gRPC Message Format (outgoing to Unity)

Defined in `Proto/game_controller.proto` — the same `.proto` file used by the Unity server.
The proto is the contract between this server (gRPC client) and Unity (gRPC server).
Any change to the proto must be coordinated with the Unity server project.

## Project Overview

An ASP.NET Core backend that acts as the central hub between IoT ESP32 controllers and the
Unity Dedicated Game Server. It subscribes to MQTT events from all physical buttons, maps
device identities to player identities, and forwards input events to the Unity server over gRPC.

## Tech Stack

- **Runtime:** .NET 8
- **Framework:** ASP.NET Core (minimal API or controllers — prefer minimal API)
- **MQTT Client:** `MQTTnet` (v4)
- **gRPC Client:** `Grpc.Net.Client` + `Google.Protobuf` + `Grpc.Tools`
- **Config:** `appsettings.json` + environment variables (no hardcoded secrets)

## Project Structure

```
server/
├── CLAUDE.md
├── server.csproj
├── Program.cs                        # App bootstrap, DI registration
├── appsettings.json                  # Non-secret config
├── appsettings.Development.json
├── Proto/
│   └── game_controller.proto         # Shared gRPC contract (source of truth)
├── Services/
│   ├── MqttSubscriberService.cs      # IHostedService — MQTT listener
│   ├── DeviceRegistryService.cs      # deviceId <-> playerId mapping
│   └── UnityRelayService.cs          # gRPC client, sends events to Unity
└── Models/
    └── ButtonEvent.cs                # Internal DTO
```

## Proto Contract (`Proto/game_controller.proto`)

This file is the source of truth shared between this server (gRPC client) and the Unity
server (gRPC server). Both sides generate stubs from the same `.proto`.

```proto
syntax = "proto3";

option csharp_namespace = "GameController";

package game_controller;

service GameControllerService {
  rpc SendButtonEvent (ButtonEventRequest) returns (ButtonEventResponse);
}

message ButtonEventRequest {
  string device_id  = 1;
  string player_id  = 2;
  string event_type = 3;  // "pressed" or "released"
  int64  timestamp  = 4;
}

message ButtonEventResponse {
  bool   accepted = 1;
  string message  = 2;
}
```

Configure `server.csproj` to auto-generate the client stub:

```xml
<ItemGroup>
  <Protobuf Include="Proto/game_controller.proto" GrpcServices="Client" />
</ItemGroup>
```

## MQTT Configuration (`appsettings.json`)

```json
{
  "Mqtt": {
    "Host": "xxxx.hivemq.cloud",
    "Port": 8883,
    "Username": "",
    "Password": "",
    "TopicFilter": "game/controllers/+/button"
  },
  "Unity": {
    "GrpcEndpoint": "https://unity-game-server:9090"
  }
}
```

Secrets (`Username`, `Password`) should be provided via environment variables or
.NET user secrets in development:
```bash
dotnet user-secrets set "Mqtt:Username" "your-user"
dotnet user-secrets set "Mqtt:Password" "your-password"
```

## Device Registry (`DeviceRegistryService.cs`)

Maps `deviceId` (from MQTT topic/payload) to `playerId` (sent to Unity).

- On startup, load mappings from `appsettings.json` under `"DeviceRegistry"`:
  ```json
  {
    "DeviceRegistry": {
      "esp32-01": "player-1",
      "esp32-02": "player-2"
    }
  }
  ```
- Expose `string? GetPlayerId(string deviceId)`
- If `deviceId` is unknown, log a warning and drop the event (do not forward)
- Future extension point: support dynamic registration via a POST endpoint

## Implementation Tasks

### Task 1: Proto + csproj setup
- Add the `.proto` file
- Add NuGet packages: `Grpc.Net.Client`, `Grpc.Tools`, `Google.Protobuf`
- Verify stub generation works with `dotnet build`

### Task 2: `DeviceRegistryService.cs`
- Singleton service, loads config on construction
- Simple `Dictionary<string, string>` lookup
- Thread-safe (reads only after init)

### Task 3: `MqttSubscriberService.cs`
- Implement `IHostedService`
- On `StartAsync`: connect to HiveMQ with TLS using `MQTTnet`
- Subscribe to `game/controllers/+/button` (wildcard covers all devices)
- On message received:
    1. Deserialize JSON payload to `ButtonEvent`
    2. Look up `playerId` from `DeviceRegistryService`
    3. Call `UnityRelayService.SendButtonEvent(...)`
- On `StopAsync`: gracefully disconnect MQTT

### Task 4: `UnityRelayService.cs`
- Singleton gRPC client wrapping the generated `GameControllerService.GameControllerServiceClient`
- Create channel once on construction (`GrpcChannel.ForAddress(...)`)
- Expose `Task SendButtonEvent(string deviceId, string playerId, string eventType, long timestamp)`
- Log success/failure; do not throw — log and continue on gRPC error

### Task 5: `Program.cs`
- Register all services in DI
- Add health check endpoint `GET /health` returning 200 OK (for cloud deployment readiness probes)
- No auth for now (add later if deploying publicly)

## Running Locally

```bash
cd server
dotnet restore
dotnet run
```

## Deployment Notes

- Intended for cloud deployment (e.g. Azure App Service, Fly.io, Railway)
- Must be reachable by ESP32 devices over the internet (or same LAN in dev)
- gRPC to Unity requires HTTP/2 — ensure the hosting environment supports it
- Use environment variables for all secrets in production

## Key Dependencies

```bash
dotnet add package MQTTnet --version 4.*
dotnet add package Grpc.Net.Client
dotnet add package Grpc.Tools
dotnet add package Google.Protobuf
```

## First-Time Setup (Required Before Running)

### 1. Set MQTT credentials via user secrets (dev only)

```bash
cd ATITInfraNode
dotnet user-secrets set "Mqtt:Username" "your-hivemq-username"
dotnet user-secrets set "Mqtt:Password" "your-hivemq-password"
```

### 2. Update placeholder values in `appsettings.json`

| Key | What to set |
|---|---|
| `Mqtt:Host` | Your HiveMQ Cloud hostname, e.g. `abc123.s1.eu.hivemq.cloud` |
| `Unity:GrpcEndpoint` | Address of the Unity dedicated server, e.g. `https://192.168.1.50:9090` |
| `DeviceRegistry` | Map each real ESP32 device ID to a player ID |

### 3. Production — use environment variables instead of user secrets

```bash
Mqtt__Host=abc123.s1.eu.hivemq.cloud
Mqtt__Username=your-user
Mqtt__Password=your-password
Unity__GrpcEndpoint=https://unity-game-server:9090
```

Double-underscore (`__`) is the .NET environment variable delimiter for nested config keys.

---

# Phase 2 — Multi-Input, Multi-Device, Multi-Player

## What Changed in Phase 2

Phase 1 modelled a simple flat mapping: one device → one player → one button.
Phase 2 introduces a richer topology:

- **One device has multiple named inputs** of different types (button, knob, sensor)
- **One player owns multiple devices** — all their devices' inputs route to the same player
- **One device can serve multiple players** — different inputs on the same physical ESP32
  are mapped to different players via input-level registry entries (Option A shared device)
  The ASP.NET server is where all of this resolution happens. Unity never sees device
  topology — it only ever receives a fully-resolved `{ playerId, inputName, inputType, value }`.

---

## Architectural Decisions Documented

### Decision 1: Player resolution at input level, not device level

In Phase 1, `playerId` was resolved per device. In Phase 2 it is resolved per
**(deviceId, inputName)** pair. This enables one physical ESP32 to serve multiple players:

```
esp32-shared
  "p1-jump"  →  player-1
  "p1-fire"  →  player-1
  "p2-jump"  →  player-2
  "p2-fire"  →  player-2
```

Resolution priority in `DeviceRegistryService`:
1. If `input.playerId` is explicitly set → use it
2. Else if `device.defaultPlayerId` is set → use it
3. Else → drop event, log warning
   This is backward compatible: single-player devices just set `defaultPlayerId` and omit
   per-input `playerId` overrides.

### Decision 2: Identity aggregation in ASP.NET, state aggregation in Unity

Two distinct aggregation concerns, deliberately separated:

| Aggregation type | Meaning | Where |
|---|---|---|
| **Identity aggregation** | Resolve `(deviceId, inputName) → playerId` | ASP.NET server ✅ |
| **Event batching** | Combine multiple events into one gRPC call | ASP.NET server ✅ |
| **Input state aggregation** | "Player is holding jump AND fire simultaneously" | Unity ✅ |

The server aggregates identity and batches wire calls. Unity aggregates logical input
state per player. Neither crosses into the other's domain.

### Decision 3: Input buffering via configurable BufferWindowMs

By default the server forwards each MQTT event to Unity immediately (zero latency added).
When `BufferWindowMs > 0`, events are accumulated over the window and flushed as a single
`SendInputBatch` gRPC call. This enables frame-aligned delivery for high-frequency sensor
inputs without adding latency to casual button-press games.

```
BufferWindowMs = 0  (default):
  MQTT event → immediate gRPC call (single-item batch)
  Latency added: 0ms
 
BufferWindowMs = 16 (60hz aligned):
  MQTT events accumulate → flush every 16ms as one gRPC batch
  Latency added: 0–16ms per event
  Benefit: all inputs that arrived in the same frame window are processed
           together in the same Unity Update() tick
```

The `InputBufferService` sits between `MqttSubscriberService` and `UnityRelayService`
and is transparent when `BufferWindowMs = 0`.
 
---

## Phase 2 MQTT Topic Schema (Breaking Change)

**Phase 1 (no longer used):**
```
game/controllers/{deviceId}/button
```

**Phase 2:**
```
game/devices/{deviceId}/input/{inputName}
```

Server wildcard subscription: `game/devices/+/input/+`
 
---

## Phase 2 MQTT Payload Format (Breaking Change)

```json
{
  "deviceId": "esp32-living-room",
  "inputName": "speed",
  "inputType": "knob",
  "event": "change",
  "value": 0.73,
  "timestamp": 1234567890,
  "sequenceNumber": 4821
}
```

New fields vs Phase 1:
- `inputName` — must match a key in the device's `inputs` registry entry
- `inputType` — `"button"` / `"knob"` / `"sensor"` — informational for Unity
- `value` — always explicit float
- `sequenceNumber` — monotonic per-device counter; used to detect drops/reordering;
  resets only on device reboot (not on reconnect)
---

## Phase 2 Proto Contract (Breaking Change)

Package renamed to `game_controller.v2`. Both this server and the Unity server must
use the same updated proto. Regenerate stubs in both projects after any change.

```proto
syntax = "proto3";
 
option csharp_namespace = "GameController.V2";
 
package game_controller.v2;
 
service GameControllerService {
  rpc SendInputEvent  (InputEventRequest)  returns (InputEventResponse);
  rpc SendInputBatch  (InputBatchRequest)  returns (InputEventResponse);
}
 
message InputEventRequest {
  string player_id       = 1;
  string device_id       = 2;
  string input_name      = 3;
  string input_type      = 4;  // "button", "knob", "sensor"
  string event_type      = 5;  // "pressed", "released", "change"
  float  value           = 6;
  int64  timestamp       = 7;
  int64  sequence_number = 8;
}
 
message InputBatchRequest {
  repeated InputEventRequest events = 1;
}
 
message InputEventResponse {
  bool   accepted = 1;
  string message  = 2;
}
```

Update `server.csproj`:
```xml
<ItemGroup>
  <Protobuf Include="Proto/game_controller_v2.proto" GrpcServices="Client" />
</ItemGroup>
```
 
---

## Phase 2 Device Registry Schema (`appsettings.json`)

The registry is now a nested hierarchy: player → devices → inputs.

```json
{
  "DeviceRegistry": {
    "players": {
      "player-1": ["esp32-living-room", "esp32-bedroom"],
      "player-2": ["esp32-shared"],
      "player-3": ["esp32-shared"]
    },
    "devices": {
      "esp32-living-room": {
        "defaultPlayerId": "player-1",
        "inputs": {
          "jump":  { "type": "button" },
          "speed": { "type": "knob", "minRaw": 0, "maxRaw": 4095 }
        }
      },
      "esp32-bedroom": {
        "defaultPlayerId": "player-1",
        "inputs": {
          "fire": { "type": "button" }
        }
      },
      "esp32-shared": {
        "defaultPlayerId": null,
        "inputs": {
          "p2-jump": { "type": "button", "playerId": "player-2" },
          "p2-fire": { "type": "button", "playerId": "player-2" },
          "p3-jump": { "type": "button", "playerId": "player-3" },
          "p3-fire": { "type": "button", "playerId": "player-3" }
        }
      }
    }
  },
  "IoTGame": {
    "BufferWindowMs": 0
  }
}
```

`minRaw`/`maxRaw` on knob inputs let the server normalize raw ADC values to `0.0–1.0`
before forwarding to Unity, so Unity always receives clean normalized values regardless
of hardware ADC range.
 
---

## Phase 2 Project Structure

```
server/
├── CLAUDE.md
├── server.csproj
├── Program.cs
├── appsettings.json
├── Proto/
│   └── game_controller_v2.proto          # Updated proto (v2)
├── Services/
│   ├── MqttSubscriberService.cs          # Updated: new topic, new payload parsing
│   ├── DeviceRegistryService.cs          # Updated: nested registry, input-level playerId
│   ├── InputBufferService.cs             # New: optional buffering via PeriodicTimer
│   └── UnityRelayService.cs              # Updated: SendInputBatch, normalized values
└── Models/
    ├── InputEvent.cs                     # Updated DTO (replaces ButtonEvent.cs)
    ├── DeviceRegistryConfig.cs           # Typed config model for nested registry
    └── BufferedInputBatch.cs             # New: batch accumulator model
```
 
---

## Phase 2 Implementation Tasks

### Task 1: Update proto
- Rename to `game_controller_v2.proto` with package `game_controller.v2`
- Add `SendInputBatch` RPC and `InputBatchRequest` message
- Add new fields: `input_name`, `input_type`, `value`, `sequence_number`
- Regenerate stubs, update namespace references throughout
### Task 2: Update `DeviceRegistryService.cs`
- Load new nested config structure into `DeviceRegistryConfig` typed model
- Expose `InputRegistryEntry? ResolveInput(string deviceId, string inputName)`
  returning `{ PlayerId, InputType, MinRaw?, MaxRaw? }`
- Resolution order: input-level `playerId` → device `defaultPlayerId` → null (drop)
- Expose `float NormalizeValue(InputRegistryEntry entry, float raw)` — clamps and
  normalizes knob values using `minRaw`/`maxRaw`; returns raw value unchanged for
  buttons and sensors
### Task 3: Update `MqttSubscriberService.cs`
- Update topic subscription from `game/controllers/+/button`
  to `game/devices/+/input/+`
- Parse `deviceId` and `inputName` from topic segments (not just payload)
- Deserialize new payload fields into `InputEvent` DTO
- Pass to `InputBufferService` instead of calling `UnityRelayService` directly
### Task 4: New `InputBufferService.cs`
- Reads `IoTGame:BufferWindowMs` from config
- If `BufferWindowMs == 0`: synchronously calls `UnityRelayService.SendInputBatch()`
  with a single-item batch (no buffering, no added latency)
- If `BufferWindowMs > 0`: accumulates events in a `List<InputEventRequest>`
  guarded by a `SemaphoreSlim`; a `PeriodicTimer` flushes and sends the batch
  on each tick; clears the list after each flush
- Expose `Task EnqueueAsync(InputEvent evt)`
### Task 5: Update `UnityRelayService.cs`
- Replace `SendButtonEvent` with `SendInputBatch(IEnumerable<InputEventRequest> events)`
- Use the v2 generated stub `GameControllerService.GameControllerServiceClient`
- Log dropped events on gRPC error; do not throw
### Task 6: Update `Program.cs`
- Register `InputBufferService` as singleton
- Register updated `DeviceRegistryService` with new config binding
---

## Phase 2 Rate Limiting (Knob / Sensor Defence)

The firmware applies a dead zone to suppress jitter. The server adds a second line of
defence: if a `(deviceId, inputName)` pair emits more than a configurable
`MaxEventsPerSecond` (default: `60`), excess events are dropped and a warning is logged.

This is implemented inside `InputBufferService.EnqueueAsync()` using a per-key
token bucket (one `Dictionary<string, RateLimiter>` entry per `deviceId:inputName`).
This prevents a malfunctioning sensor from flooding the gRPC channel to Unity.

---

# Phase 2.1 — Protocol Fix: Separate Physical Slot Identity from Semantic Input Name

## What Changed in Phase 2.1

Phase 2 had a design flaw where `inputName` served two purposes: physical slot
identification (for routing) and semantic game labelling (for Unity). This caused a
naming collision when two physical slots on the same device represent the same game
action for different players — e.g. `btn1` and `btn2` are both `"jump"` but for
`player-1` and `player-2` respectively. Naming them both `"jump"` was impossible;
the workaround of `"p1-jump"` / `"p2-jump"` smuggled player identity into the name.

Phase 2.1 cleanly separates these concerns:

| Field | Meaning | Lives in |
|---|---|---|
| `slotId` | Physical hardware slot identifier | Firmware payload + MQTT topic |
| `inputName` | Semantic game label (`"jump"`, `"speed"`) | ASP.NET registry only |
| `playerId` | Player this slot belongs to | ASP.NET registry only |

**This server is now the sole owner of semantic meaning.** The firmware publishes
raw physical slot events. This server translates `(deviceId, slotId)` into
`(playerId, inputName)` before forwarding to Unity. Unity remains fully decoupled
from physical device topology.
 
---

## Architectural Decision: The ASP.NET Server Owns All Semantic Translation

This is a deliberate and important boundary. The full translation chain is:

```
Firmware publishes:
  topic:   game/devices/esp32-shared/slot/btn1
  payload: { deviceId: "esp32-shared", slotId: "btn1", inputType: "button", ... }
 
ASP.NET registry lookup: (deviceId="esp32-shared", slotId="btn1")
  → { playerId: "player-1", inputName: "jump" }
 
Unity receives:
  { playerId: "player-1", inputName: "jump", inputType: "button", value: 1.0, ... }
```

Neither the firmware nor Unity ever see the other side's naming. The registry is the
only place that knows both the physical layout and the game semantics simultaneously.
This means:
- **Reassigning a button to a different player** = registry config change only
- **Renaming a game action** = registry config change only
- **Neither requires a firmware reflash or Unity rebuild**
---

## Phase 2.1 MQTT Topic Schema (Breaking Change from Phase 2)

**Phase 2 (deprecated):**
```
game/devices/{deviceId}/input/{inputName}
```

**Phase 2.1:**
```
game/devices/{deviceId}/slot/{slotId}
```

Updated wildcard subscription in `MqttSubscriberService`:
```
game/devices/+/slot/+
```

Parse topic segments to extract `deviceId` (index 1) and `slotId` (index 3):
```csharp
var parts = topic.Split('/');
var deviceId = parts[1];  // "esp32-shared"
var slotId   = parts[3];  // "btn1"
```
 
---

## Phase 2.1 MQTT Payload Format (Breaking Change from Phase 2)

**Phase 2 (deprecated):**
```json
{ "deviceId": "...", "inputName": "jump", "inputType": "knob", ... }
```

**Phase 2.1:**
```json
{
  "deviceId": "esp32-shared",
  "slotId": "btn1",
  "inputType": "button",
  "event": "pressed",
  "value": 1.0,
  "timestamp": 1234567890,
  "sequenceNumber": 4821
}
```

`inputName` is removed from the payload. It is resolved server-side from the registry.
 
---

## Phase 2.1 Device Registry Schema Update

The registry keys under `"inputs"` are now `slotId` values (physical identifiers).
Each entry explicitly declares both `inputName` (semantic) and optionally `playerId`
(routing override). This is the only place in the entire system where physical slot
identity and game semantic meaning are linked.

```json
{
  "DeviceRegistry": {
    "players": {
      "player-1": ["esp32-living-room"],
      "player-2": ["esp32-shared"],
      "player-3": ["esp32-shared"]
    },
    "devices": {
      "esp32-living-room": {
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
          "btn2": { "type": "button", "inputName": "jump", "playerId": "player-3" },
          "btn3": { "type": "button", "inputName": "fire", "playerId": "player-2" },
          "btn4": { "type": "button", "inputName": "fire", "playerId": "player-3" }
        }
      }
    }
  }
}
```

Note: `btn1` on `esp32-shared` and `btn1` on `esp32-living-room` are completely
independent — `slotId` is scoped to a device, not global.
 
---

## Phase 2.1 `DeviceRegistryService.cs` Update

Update `ResolveInput` signature to accept `slotId` instead of `inputName`:

```csharp
// Phase 2 (deprecated):
InputRegistryEntry? ResolveInput(string deviceId, string inputName)
 
// Phase 2.1:
InputRegistryEntry? ResolveInput(string deviceId, string slotId)
```

`InputRegistryEntry` now carries both the physical and semantic fields:

```csharp
public record InputRegistryEntry(
    string PlayerId,
    string InputName,   // semantic — forwarded to Unity
    string InputType,
    float? MinRaw,
    float? MaxRaw
);
```

Resolution logic unchanged — input-level `playerId` overrides `defaultPlayerId`.
The difference is that the lookup key is now `slotId` (physical) and the result
includes `inputName` (semantic) as a resolved field.
 
---

## Phase 2.1 `InputEvent.cs` Model Update

```csharp
// Internal DTO after registry resolution — what gets forwarded to Unity
public record InputEvent(
    string PlayerId,    // resolved from registry
    string DeviceId,    // from payload (for diagnostics/logging)
    string SlotId,      // from payload (for diagnostics/logging)
    string InputName,   // resolved from registry — this is what Unity sees
    string InputType,   // from payload
    string EventType,
    float  Value,       // normalized if knob
    long   Timestamp,
    long   SequenceNumber
);
```

`SlotId` and `DeviceId` are retained in the internal model for logging and
sequence number tracking but are **not forwarded** to Unity via gRPC.
 
---

## Phase 2.1 Proto Contract — Unchanged

The gRPC proto does not change in Phase 2.1. Unity already receives `inputName`
(semantic) and `playerId` — both of which are now correctly resolved server-side.
`slotId` and `deviceId` are intentionally excluded from the proto; Unity has no
use for physical device topology.
 