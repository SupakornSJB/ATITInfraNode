# CLAUDE.md — ASP.NET Core Server

## Update Logs

Daily logs of problems encountered and solutions applied are in the `updates/` folder.
Read the relevant date file for context on recent changes before diving into code.

- [2026-04-24](updates/2026-04-24.md) — MQTT TLS/port mismatch crash on startup
- [2026-04-25](updates/2026-04-25.md) — Phase 2 (multi-input/device/player), gRPC SSL fix, logging, Phase 2.1 slot/inputName separation
- [2026-04-27](updates/2026-04-27.md) — Phase 3.3 gRPC keep-alive/reconnection, Phase 4 library foundation (IoTGame.Core + GameServer)

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


# Phase 3 — Space Shooter: Environmental Routing, Zone Logic & Admin Commands

## What Changed in Phase 3

Phase 3 adds three server-side concerns:
1. **Environmental input routing** — DHT11 and ultrasonic events have no `playerId`
   and are forwarded to Unity via a new `SendEnvironmentEvent` RPC rather than `SendInputBatch`
2. **Zone computation** — the server resolves raw ultrasonic distance into a discrete
   zone ID with hysteresis before forwarding to Unity
3. **Admin commands** — `SendAdminCommand` RPC added to the existing gRPC channel;
   new REST endpoints allow external round control without a separate connection
---

## Architectural Decision: Environmental Inputs Are Not Player Inputs

DHT11 temperature and ultrasonic distance have no `playerId` — they describe world
state, not player actions. Routing them through `SendInputBatch` (which requires a
`playerId`) would force an artificial player assignment. Instead they use a dedicated
`SendEnvironmentEvent` RPC on the same gRPC channel. Unity routes them to
`EnvironmentController` rather than `IoTInputManager`.

This separation keeps the two concerns permanently distinct:
- `SendInputBatch` → player input → `IoTInputManager` → player NetworkObjects
- `SendEnvironmentEvent` → world state → `EnvironmentController` → `EnvironmentState`
---

## Phase 3 Proto Contract (Additive — No Breaking Changes)

All Phase 2.1 RPCs unchanged. Three additions:

```proto
syntax = "proto3";
 
option csharp_namespace = "GameController.V2";
 
package game_controller.v2;
 
service GameControllerService {
  rpc SendInputEvent      (InputEventRequest)      returns (InputEventResponse);
  rpc SendInputBatch      (InputBatchRequest)       returns (InputEventResponse);
  rpc SendEnvironmentEvent(EnvironmentEventRequest) returns (InputEventResponse);  // new
  rpc SendAdminCommand    (AdminCommandRequest)     returns (AdminCommandResponse); // new
}
 
// Unchanged from Phase 2.1
message InputEventRequest { ... }
message InputBatchRequest  { repeated InputEventRequest events = 1; }
message InputEventResponse { bool accepted = 1; string message = 2; }
 
// New — environmental world state update
message EnvironmentEventRequest {
  string env_key    = 1;  // "temperature" or "ultrasonic_zone"
  float  raw_value  = 2;  // raw Celsius for temp; zone ID (1-4) for ultrasonic
  int64  timestamp  = 3;
}
 
// New — admin round control
message AdminCommandRequest {
  string command   = 1;  // "start_round", "end_round", "reset"
  string issued_by = 2;  // "admin", "system"
  int64  timestamp = 3;
}
 
message AdminCommandResponse {
  bool   accepted = 1;
  string message  = 2;
}
```

Update `server.csproj`:
```xml
<ItemGroup>
  <Protobuf Include="Proto/game_controller_v2.proto" GrpcServices="Client"/>
</ItemGroup>
```
 
---

## Phase 3 Device Registry Schema Update

New `inputRole` field per input entry: `"player"`, `"environment"`, or `"stage"`.
The server uses this to route events to the correct RPC.

```json
{
  "DeviceRegistry": {
    "players": {
      "player-1": ["esp32-ship-1"],
      "player-2": ["esp32-ship-2"],
      "player-3": ["esp32-bird-1"],
      "player-4": ["esp32-bird-2"]
    },
    "devices": {
      "esp32-ship-1": {
        "defaultPlayerId": "player-1",
        "inputs": {
          "joystick-x":  { "type": "knob",   "inputName": "move-x", "inputRole": "player" },
          "joystick-y":  { "type": "knob",   "inputName": "move-y", "inputRole": "player" },
          "stick-click": { "type": "button", "inputName": "special", "inputRole": "player" },
          "fire":        { "type": "button", "inputName": "fire",    "inputRole": "player" }
        }
      },
      "esp32-ship-2": {
        "defaultPlayerId": "player-2",
        "inputs": {
          "joystick-x":  { "type": "knob",   "inputName": "move-x", "inputRole": "player" },
          "joystick-y":  { "type": "knob",   "inputName": "move-y", "inputRole": "player" },
          "stick-click": { "type": "button", "inputName": "special", "inputRole": "player" },
          "fire":        { "type": "button", "inputName": "fire",    "inputRole": "player" }
        }
      },
      "esp32-bird-1": {
        "defaultPlayerId": "player-3",
        "inputs": {
          "flap": { "type": "button", "inputName": "flap", "inputRole": "player" }
        }
      },
      "esp32-bird-2": {
        "defaultPlayerId": "player-4",
        "inputs": {
          "flap": { "type": "button", "inputName": "flap", "inputRole": "player" }
        }
      },
      "esp32-stage": {
        "defaultPlayerId": null,
        "inputs": {
          "temp":      { "type": "dht11",      "inputRole": "environment", "envKey": "temperature" },
          "distance":  { "type": "ultrasonic", "inputRole": "environment", "envKey": "ultrasonic_zone" },
          "pipe-flip": { "type": "button",     "inputRole": "stage",       "inputName": "pipe-flip" }
        }
      }
    }
  }
}
```

`envKey` maps to the `env_key` field in `EnvironmentEventRequest`. Stage role inputs
(`pipe-flip`) are forwarded as player inputs with `playerId: "stage"` — Unity's
`IoTInputManager` routes `"stage"` player events to `StageController`.
 
---

## Phase 3 Zone Computation (`ZoneResolverService.cs`)

Raw ultrasonic distance (cm) is resolved to a zone ID server-side before forwarding
to Unity. Unity never receives raw distance values.

### Zone definitions

| Zone ID | Distance | Effect in Unity |
|---|---|---|
| 1 | 0–20cm | Shield bubble |
| 2 | 20–60cm | Time slow (60% speed) |
| 3 | 60–120cm | Normal — no effect |
| 4 | 120cm+ | Enemy frenzy |

### Hysteresis logic

To prevent zone flickering at boundaries, a zone transition only commits after the
reading has been consistently in the new zone for `HysteresisMs` (default: 500ms):

```csharp
public class ZoneResolverService
{
    private int   _currentZone       = 3;
    private int   _candidateZone     = 3;
    private long  _candidateSince    = 0;
    private const int HysteresisMs   = 500;
 
    public int? Resolve(float distanceCm)
    {
        int raw = distanceCm switch {
            < 20  => 1,
            < 60  => 2,
            < 120 => 3,
            _     => 4
        };
 
        if (raw == _currentZone) { _candidateZone = raw; return null; }
 
        if (raw != _candidateZone) {
            _candidateZone  = raw;
            _candidateSince = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return null;
        }
 
        long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _candidateSince;
        if (elapsed < HysteresisMs) return null;
 
        _currentZone = raw;
        return _currentZone;  // non-null = zone changed, forward to Unity
    }
}
```

Only forward to Unity when zone actually changes — not on every ultrasonic reading.
 
---

## Phase 3 Event Routing Pipeline

`MqttSubscriberService` now branches on `inputRole` after registry resolution:

```
Incoming MQTT event
      ↓
DeviceRegistryService.ResolveInput(deviceId, slotId)
      → InputRegistryEntry { inputRole, inputName, playerId, envKey, ... }
      ↓
Branch on inputRole:
 
  "player"      → InputBufferService.EnqueueAsync()
                       → SendInputBatch (existing)
 
  "environment" → if envKey == "ultrasonic_zone":
                       ZoneResolverService.Resolve(value)
                       if zone changed → UnityRelayService.SendEnvironmentEvent()
                  if envKey == "temperature":
                       UnityRelayService.SendEnvironmentEvent() directly
 
  "stage"       → InputBufferService.EnqueueAsync() with playerId = "stage"
                       → SendInputBatch (existing, Unity routes by playerId)
```
 
---

## Phase 3 Admin Endpoints

Three new minimal API endpoints. All call `UnityRelayService.SendAdminCommand()`
over the **existing gRPC channel** — no new connection, no new protocol.

```csharp
app.MapPost("/api/admin/start",  () => relay.SendAdminCommand("start_round",  "admin"));
app.MapPost("/api/admin/end",    () => relay.SendAdminCommand("end_round",    "admin"));
app.MapPost("/api/admin/reset",  () => relay.SendAdminCommand("reset",        "admin"));
```

These endpoints require no auth for the POC (add API key header in production).
They are the fallback round trigger mechanism — primary trigger is player ready-up
via physical button press.
 
---

## Phase 3 Project Structure Changes

```
server/
├── Services/
│   ├── MqttSubscriberService.cs    # Updated: branches on inputRole
│   ├── DeviceRegistryService.cs    # Updated: resolves inputRole + envKey
│   ├── ZoneResolverService.cs      # New: hysteresis zone computation
│   ├── InputBufferService.cs       # Unchanged
│   └── UnityRelayService.cs        # Updated: SendEnvironmentEvent + SendAdminCommand
├── Models/
│   ├── InputEvent.cs               # Unchanged
│   ├── EnvironmentEvent.cs         # New: { EnvKey, RawValue, Timestamp }
│   └── DeviceRegistryConfig.cs     # Updated: inputRole + envKey fields
└── Endpoints/
    ├── IoTGameEndpoints.cs         # Updated: /health, /sessions
    └── AdminEndpoints.cs           # New: /api/admin/start|end|reset
```

---

# Phase 3.3 — Infrastructure Fix: gRPC Connection Stability

## What Changed in Phase 3.3

Long-lived gRPC channels between ASP.NET and Unity can drop silently due to
network hiccups, Unity server restarts, or cloud load balancer idle timeouts.
Phase 3.3 adds keep-alive pings and reconnection logic to `UnityRelayService`
so the channel recovers automatically without manual intervention.
 
---

## Keep-Alive Configuration

Configure `SocketsHttpHandler` when building the gRPC channel in
`UnityRelayService`. Keep-alive pings prevent idle timeout drops from
intermediate network equipment (load balancers typically kill idle connections
after 60–300 seconds):

```csharp
private GrpcChannel BuildChannel(string endpoint) =>
    GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions {
        HttpHandler = new SocketsHttpHandler {
            KeepAlivePingDelay    = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout  = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = true
        }
    });
```

Every 30 seconds the client sends an HTTP/2 PING frame. If no response within
10 seconds, the connection is considered dead and the reconnection logic takes
over. Kestrel on the Unity side responds to PING frames automatically — no
Unity-side configuration needed.
 
---

## Reconnection Logic (`UnityRelayService.cs`)

Check `ConnectivityState` before every RPC call. Rebuild the channel if it is
in `TransientFailure` or `Shutdown` state:

```csharp
public class UnityRelayService
{
    private GrpcChannel _channel;
    private GameControllerService.GameControllerServiceClient _client;
    private readonly string _endpoint;
 
    public UnityRelayService(string endpoint)
    {
        _endpoint = endpoint;
        _channel  = BuildChannel(endpoint);
        _client   = new GameControllerServiceClient(_channel);
    }
 
    private async Task EnsureConnectedAsync()
    {
        var state = _channel.State;
        if (state is ConnectivityState.TransientFailure or ConnectivityState.Shutdown)
        {
            _logger.LogWarning("[gRPC] Channel in {State} — rebuilding", state);
            await _channel.ShutdownAsync();
            _channel = BuildChannel(_endpoint);
            _client  = new GameControllerServiceClient(_channel);
        }
    }
 
    public async Task SendInputBatch(IEnumerable<InputEventRequest> events)
    {
        await EnsureConnectedAsync();
        try
        {
            await _client.SendInputBatchAsync(new InputBatchRequest {
                Events = { events }
            });
        }
        catch (RpcException ex)
        {
            _logger.LogError("[gRPC] SendInputBatch failed: {Status}", ex.Status);
            // do not throw — drop and continue
        }
    }
 
    // Same pattern for SendEnvironmentEvent and SendAdminCommand
}
```

`EnsureConnectedAsync()` is called at the top of every public method.
The channel rebuild is lightweight — HTTP/2 connection re-establishment happens
on the first RPC call after rebuild, not during `BuildChannel()` itself.
 
---

## gRPC Connectivity States Reference

| State | Meaning | Action |
|---|---|---|
| `Idle` | Connected, no active calls | Normal — proceed |
| `Connecting` | Establishing connection | Normal — proceed, gRPC queues the call |
| `Ready` | Connected and healthy | Normal — proceed |
| `TransientFailure` | Connection lost, retrying | Rebuild channel |
| `Shutdown` | Channel explicitly closed | Rebuild channel |

 
---
 
---

# Phase 4 — Library Foundation

## What Changed in Phase 4

Phase 4 restructures the ASP.NET server into a clear library/game separation,
formalises the three developer interfaces, adds session management with a
swappable storage backend, and closes authentication gaps identified in Phase 3.
 
---

## Architectural Decision: Source Library

The server is distributed as a **source library** — developers clone the repo
and write their game-specific code in `GameServer/`. The `IoTGame.Core/` project
is the library and should not be modified. Developers reference it as a project
reference, not a NuGet package.

```
Clone repo
  → implement interfaces in GameServer/
  → configure appsettings.json
  → dotnet run --project GameServer
```
 
---

## Phase 4 Folder Structure

```
iotgame-server/
├── IoTGame.Core/                              ← LIBRARY — do not modify
│   ├── IoTGame.Core.csproj
│   ├── Abstractions/
│   │   ├── IDeviceAuthenticator.cs
│   │   ├── ISessionResolver.cs
│   │   ├── IPlayerMapper.cs
│   │   ├── ISessionStore.cs
│   │   └── IAdminAuthenticator.cs
│   ├── Services/
│   │   ├── MqttSubscriberService.cs
│   │   ├── InputBufferService.cs
│   │   ├── ZoneResolverService.cs
│   │   ├── UnityRelayService.cs
│   │   └── SessionManager.cs
│   ├── Storage/
│   │   └── InMemorySessionStore.cs            ← default, ships with library
│   ├── Auth/
│   │   ├── AllowAllDeviceAuthenticator.cs     ← default
│   │   ├── ApiKeyAdminAuthenticator.cs        ← default
│   │   └── SessionTokenService.cs             ← optional token flow
│   ├── Endpoints/
│   │   ├── IoTGameEndpoints.cs                ← /health
│   │   ├── AdminEndpoints.cs                  ← /api/admin/*
│   │   └── SessionEndpoints.cs                ← /api/sessions/*
│   ├── Models/
│   │   ├── GameSession.cs
│   │   ├── SessionConfig.cs
│   │   ├── DeviceConfig.cs
│   │   ├── SlotConfig.cs
│   │   └── PlayerMapping.cs
│   ├── Proto/
│   │   └── game_controller_v2.proto
│   └── Extensions/
│       └── IoTGameServiceCollectionExtensions.cs
│
└── GameServer/                                ← DEVELOPER CODE — modify freely
    ├── GameServer.csproj                      ← references IoTGame.Core
    ├── Program.cs                             ← AddIoTGame() + developer choices
    ├── appsettings.json
    ├── Auth/
    │   └── MyDeviceAuthenticator.cs           ← implements IDeviceAuthenticator
    ├── Sessions/
    │   └── MySessionStore.cs                  ← optional custom ISessionStore
    └── Mapping/
        └── MyPlayerMapper.cs                  ← optional custom IPlayerMapper
```
 
---

## Developer Interfaces

Three interfaces a developer can implement. All have sensible defaults so the
library works out of the box without implementing any of them.

### `IDeviceAuthenticator`

**Question:** Is this ESP32 device trusted?
**Called:** Before any other processing on every incoming MQTT event.
**Default:** `AllowAllDeviceAuthenticator` — accepts everything.

```csharp
public interface IDeviceAuthenticator
{
    Task<bool> IsAuthenticatedAsync(string deviceId, string sessionId);
}
```

Example implementation:
```csharp
public class MyDeviceAuthenticator : IDeviceAuthenticator
{
    public Task<bool> IsAuthenticatedAsync(string deviceId, string sessionId)
    {
        // Check against your own device list, database, etc.
        var allowed = new[] { "esp32-ship-1", "esp32-ship-2", "esp32-bird-1" };
        return Task.FromResult(allowed.Contains(deviceId));
    }
}
```

### `ISessionResolver`

**Question:** Which Unity server should this event go to?
**Called:** After device authentication passes.
**Default:** `SessionStoreResolver` — reads from `ISessionStore`.

```csharp
public interface ISessionResolver
{
    // Returns gRPC endpoint e.g. "https://unity-server:9090"
    // Returns null if session unknown or ended → event dropped
    Task<string?> ResolveEndpointAsync(string sessionId);
}
```

The default implementation reads from `ISessionStore` automatically — developers
only implement this if they need custom resolution logic (e.g. DNS-based discovery,
external service lookup).

### `IPlayerMapper`

**Question:** Which player and input name does this physical slot resolve to?
**Called:** After session resolution.
**Default:** `SessionStorePlayerMapper` — reads slot mappings from `ISessionStore`.

```csharp
public interface IPlayerMapper
{
    Task<PlayerMapping?> GetPlayerMappingAsync(
        string deviceId, string slotId, string sessionId);
}
 
public record PlayerMapping(
    string? PlayerId,    // null for environment/stage roles
    string? InputName,   // null for environment roles
    string  InputRole,   // "player", "environment", "stage"
    string? EnvKey,      // populated for environment role
    Dictionary<string, string>? AuxiliaryKeys
);
```

### `IAdminAuthenticator`

**Question:** Is this HTTP request authorised to call admin endpoints?
**Called:** On every `/api/admin/*` and `/api/sessions/*` request.
**Default:** `ApiKeyAdminAuthenticator` — validates `X-IoTGame-Key` header.

```csharp
public interface IAdminAuthenticator
{
    Task<bool> IsAuthorizedAsync(HttpContext context, string action);
}
```

Default reads key from config:
```json
{
  "IoTGame": {
    "AdminApiKey": "your-secret-key-here"
  }
}
```
 
---

## `AddIoTGame()` Fluent Builder

The developer's `Program.cs` is the only place they configure the library:

```csharp
builder.Services.AddIoTGame(options =>
{
    options.MqttHost      = "xxxx.hivemq.cloud";
    options.MqttPort      = 8883;
    options.MqttUser      = Environment.GetEnvironmentVariable("MQTT_USER");
    options.MqttPassword  = Environment.GetEnvironmentVariable("MQTT_PASS");
    options.SharedSecret  = Environment.GetEnvironmentVariable("IOTGAME_SECRET");
    options.BufferWindowMs = 0;
})
// Storage — default is InMemorySessionStore
.WithSessionStore<InMemorySessionStore>()     // default, can omit
// .WithSessionStore<MyCustomSessionStore>()  // developer provides persistence
 
// Interfaces — all optional, defaults used if omitted
.WithDeviceAuthenticator<MyDeviceAuthenticator>()
.WithPlayerMapper<MyPlayerMapper>()
.WithAdminAuthenticator<ApiKeyAdminAuthenticator>()
 
// Optional features
.WithSessionTokenAuth();  // enables token-based NGO client validation
 
app.MapIoTGameEndpoints();  // registers all library endpoints
```
 
---

## Session Management

### Session Lifecycle

```
Created → Active → Ended
```

Sessions are created and managed via REST API or programmatically via
`SessionManager`. Once `Active`, the session accepts MQTT events. Once `Ended`,
events from its devices are dropped.

### `ISessionStore` Interface

```csharp
public interface ISessionStore
{
    Task SaveAsync(GameSession session);
    Task<GameSession?> GetAsync(string sessionId);
    Task DeleteAsync(string sessionId);
    Task<IEnumerable<GameSession>> GetAllActiveAsync();
}
```

**Default:** `InMemorySessionStore` — `ConcurrentDictionary` in process memory.
State is lost on server restart. Suitable for demos and short-lived matches.

**Custom persistence:** Developer implements `ISessionStore` with SQLite, Redis,
or any other backend. The library documents the interface contract; the
implementation is the developer's responsibility.

```csharp
// Example — developer writes this if they need persistence
public class SqliteSessionStore : ISessionStore
{
    public async Task SaveAsync(GameSession session) { ... }
    public async Task<GameSession?> GetAsync(string sessionId) { ... }
    public async Task DeleteAsync(string sessionId) { ... }
    public async Task<IEnumerable<GameSession>> GetAllActiveAsync() { ... }
}
```

### `GameSession` Model

```csharp
public class GameSession
{
    public string        SessionId      { get; init; }
    public string        UnityEndpoint  { get; init; }  // gRPC endpoint
    public SessionStatus Status         { get; set; }   // Created, Active, Ended
    public DateTime      CreatedAt      { get; init; }
    public DateTime?     StartedAt      { get; set; }
    public DateTime?     EndedAt        { get; set; }
    public List<DeviceConfig> Devices   { get; init; }
}
 
public class DeviceConfig
{
    public string            DeviceId { get; init; }
    public string?           DefaultPlayerId { get; init; }
    public List<SlotConfig>  Slots    { get; init; }
}
 
public class SlotConfig
{
    public string  SlotId     { get; init; }
    public string  InputType  { get; init; }
    public string  InputRole  { get; init; }  // "player", "environment", "stage"
    public string? PlayerId   { get; init; }  // input-level override
    public string? InputName  { get; init; }
    public string? EnvKey     { get; init; }
    public Dictionary<string, string>? AuxiliaryKeys { get; init; }
}
```

### Session REST API

```
POST   /api/sessions                         → create session
GET    /api/sessions                         → list all active sessions
GET    /api/sessions/{sessionId}             → get session details + device list
POST   /api/sessions/{sessionId}/start       → activate session
POST   /api/sessions/{sessionId}/end         → end session
DELETE /api/sessions/{sessionId}             → remove session entirely
POST   /api/sessions/{sessionId}/devices     → add device to session
DELETE /api/sessions/{sessionId}/devices/{deviceId} → remove device
```

All endpoints require `X-IoTGame-Key` header (or custom `IAdminAuthenticator`).

### Session Config Delivery to Unity

When a session starts, `SessionManager` calls `SendAdminCommand` with session
metadata in the `auxiliary` map so Unity can configure itself:

```csharp
await _relay.SendAdminCommand(new AdminCommandRequest {
    Command   = "session_start",
    IssuedBy  = "system",
    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    Auxiliary = {
        { "round_duration", 120f },
        { "max_minions",    20f  },
        { "player_count",   4f   }
    }
});
```
 
---

## Authentication

### ASP.NET → Unity: Shared Secret

Every outgoing gRPC call includes a shared secret in the request metadata.
Unity validates it on every incoming RPC. Both sides configure the same secret:

**ASP.NET (`appsettings.json`):**
```json
{ "IoTGame": { "SharedSecret": "your-secret-here" } }
```

**ASP.NET sends on every RPC:**
```csharp
var headers = new Metadata {
    { "x-iotgame-secret", _options.SharedSecret }
};
await _client.SendInputBatchAsync(request, headers);
```

If the secret does not match, Unity rejects the call with `StatusCode.Unauthenticated`.

### Admin Endpoints: API Key

Default `ApiKeyAdminAuthenticator` validates `X-IoTGame-Key` header on all
`/api/admin/*` and `/api/sessions/*` endpoints:

```http
POST /api/sessions HTTP/1.1
X-IoTGame-Key: your-secret-key
Content-Type: application/json
```

### Optional: Session Token Flow (`WithSessionTokenAuth()`)

When enabled, issues short-lived tokens for Unity game client NGO connections:

```
POST /api/sessions/{sessionId}/join
{ "preferredPlayerId": "player-1" }    ← optional display preference, not auth
← { "token": "tok_xK92mPqR", "expiresIn": 60 }
 
Client connects to Unity NGO with token in payload:
"spectator:tok_xK92mPqR"
 
Unity validates with ASP.NET:
GET /api/sessions/{sessionId}/validate?token=tok_xK92mPqR
← { "valid": true }
```

Note: the token does not grant player control — all clients are spectators.
The token only proves the client is a known participant of an active session.

### HiveMQ Topic ACLs (Broker Configuration)

Configure in HiveMQ Cloud dashboard — not in application code:

```
Device: esp32-ship-1
  Allow publish:   game/devices/esp32-ship-1/#
  Deny publish:    game/devices/#  (all other device topics)
  Allow subscribe: (none)
```

Each device can only publish to its own topic prefix. This prevents a
compromised device from spoofing another device's events at the broker level.
Document this setup in the project README as part of production deployment.
 
---

## MQTT Event Processing Pipeline (Phase 4 Complete)

```
MQTT event arrives on game/devices/{deviceId}/slot/{slotId}
      │
      ▼
IDeviceAuthenticator.IsAuthenticatedAsync(deviceId, sessionId)
      │ false → drop + warn
      ▼
ISessionResolver.ResolveEndpointAsync(sessionId)
      │ null → drop + warn
      ▼
IPlayerMapper.GetPlayerMappingAsync(deviceId, slotId, sessionId)
      │ null → drop + warn
      ▼
Branch on InputRole:
  "player"      → InputBufferService → SendInputBatch (with shared secret)
  "environment" → ZoneResolverService → SendEnvironmentEvent (with shared secret)
  "stage"       → InputBufferService with playerId="stage" → SendInputBatch
      │
      ▼
UnityRelayService (keep-alive + reconnection from Phase 3.3)
``