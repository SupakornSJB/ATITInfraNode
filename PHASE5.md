# CLAUDE.md — Phase 5

> **This file is a continuation of the per-project CLAUDE.md files.**
> Read the main CLAUDE.md in each project folder for Phases 1–4 context.
> This file covers Phase 5 only and applies to all three projects simultaneously.

---

## What Changed in Phase 5

Phase 5 is a game redesign that simplifies the player structure, replaces asymmetric
multiplayer with a cooperative two-player shooter, and removes all ready-up logic in
favour of admin-only round start. The IoT pipeline, protocol design, and library
architecture are **completely unchanged**. All changes are in game mechanics and
device registry configuration.

---

## Architectural Decisions Made in Phase 5

### Decision 1: Remove boss bird players — enemies are AI only

Boss bird players (player-3, player-4) and their physical button devices are removed.
All enemy birds are now server-controlled AI. This simplifies the game structure,
removes the asymmetric role complexity, and makes the demo easier to run with two
physical ESP32 devices instead of four or more.

### Decision 2: Remove player ready-up — admin API only for round start

The ready-up mechanic (pressing fire/flap during Waiting phase to signal readiness)
is removed entirely. `GameSessionManager` no longer tracks ready flags. Rounds start
exclusively via `POST /api/admin/start`, which calls `SendAdminCommand("start_round")`
over the existing gRPC channel. This removes a source of accidental round starts
during demo setup and simplifies `GameInputRouter` (no phase-specific input handling
for Waiting phase beyond ignoring all input).

### Decision 3: Two-player cooperative setup on two ESP32 devices

One physical ESP32 per player. Each device has a distinct input profile. Both players
cooperate to survive enemy waves — there is no opposing team.

### Decision 4: Knob for horizontal position, not velocity

Player 2's knob maps to absolute horizontal position (0.0 = left edge, 1.0 = right
edge) rather than velocity. This is more intuitive — turn the knob all the way left,
ship is on the left. No drift, no dead zone tuning required, no centering issue.
The relay server forwards the normalized value as-is; Unity maps it to screen position
in `FixedUpdate`.

---

## Phase 5 Device Map

### ESP32 #1 — Player 1 + Environment

| Slot | Hardware | inputRole | inputName / envKey | Notes |
|---|---|---|---|---|
| `joystick-x` | Thumbstick VRX | player | `move-x` | 0.5 = center |
| `joystick-y` | Thumbstick VRY | player | `move-y` | 0.5 = center |
| `stick-click` | Thumbstick SW | player | `fire` | press to fire |
| `admin-start` | Button | stage | `admin-start` | triggers POST /api/admin/start |
| `admin-end` | Button | stage | `admin-end` | triggers POST /api/admin/end |
| `temp` | DHT11 | environment | `gravity` | temperature → gravity |
| `distance` | HC-SR04 | environment | `ultrasonic_zone` | distance → zone 1–4 |

Player 1 controls their ship with the thumbstick (X/Y movement) and stick-click
(fire). The two admin buttons are for the demo operator — press to start or end the
round without a REST call. DHT11 and ultrasonic run alongside player input on the
same ESP32 with no conflict (different slots, independent polling).

### ESP32 #2 — Player 2

| Slot | Hardware | inputRole | inputName | Notes |
|---|---|---|---|---|
| `knob` | Potentiometer | player | `move-x` | absolute position 0.0–1.0 |
| `btn-up` | Button | player | `move-y-up` | held = move up |
| `btn-down` | Button | player | `move-y-down` | held = move down |
| `btn-fire` | Button | player | `fire` | press to fire |

Player 2 has no vertical analog — up/down are digital buttons. The ship moves upward
while `move-y-up` is held (value=1.0) and downward while `move-y-down` is held
(value=1.0). Both released = no vertical movement. This is handled in Unity by
`PlayerShip` tracking the last known value of both slots and combining them in
`FixedUpdate`.

---

## Phase 5 Enemy Bird AI Design

Enemy birds replace the boss bird players entirely. They are server-controlled
NetworkObjects spawned by `EnemySpawnManager`.

### Spawn behaviour
- Spawn at the right edge of the screen at a random Y position
- Move leftward at a configurable speed (affected by `GlobalGameSpeed`)
- Gravity registered with `EnvironmentController` — float slightly, affected by
  `GlobalGravity` like all other Rigidbodies
- Despawn when they reach the left edge (deal damage to nearest ship on exit)
- Despawn when hit by a projectile (ship scores a point)

### Bird shooting
- Each bird has a `ShootCooldown` (default: 3–6 seconds, randomised per bird)
- When cooldown expires, bird fires a projectile toward the nearest ship
- Bird projectile travels rightward (opposite direction to ship projectiles)
- Bird projectile hits a ship → `PlayerShip.TakeDamage(10)` → despawns
- Bird projectile goes off left edge → despawns

### Win / lose conditions

| Condition | Result |
|---|---|
| Both ships reach 0 HP | Round ended — players lose |
| Wave timer expires (all waves cleared) | Round ended — players win |
| Admin `POST /api/admin/end` | Round ended immediately |

No "boss health bar" — victory is surviving the wave timer. The environmental
controller's effects (gravity, spawn rate, zone) affect difficulty throughout.

---

## Phase 5 Registry Configuration (ASP.NET `appsettings.json`)

```json
{
  "DeviceRegistry": {
    "players": {
      "player-1": ["esp32-device-1"],
      "player-2": ["esp32-device-2"]
    },
    "devices": {
      "esp32-device-1": {
        "defaultPlayerId": "player-1",
        "inputs": {
          "joystick-x":  { "type": "knob",   "inputName": "move-x",  "inputRole": "player" },
          "joystick-y":  { "type": "knob",   "inputName": "move-y",  "inputRole": "player" },
          "stick-click": { "type": "button", "inputName": "fire",    "inputRole": "player" },
          "admin-start": { "type": "button", "inputName": "admin-start", "inputRole": "stage" },
          "admin-end":   { "type": "button", "inputName": "admin-end",   "inputRole": "stage" },
          "temp":        { "type": "dht11",  "inputRole": "environment", "envKey": "gravity",
                           "auxiliaryKeys": { "humidity": "spawn_rate" } },
          "distance":    { "type": "ultrasonic", "inputRole": "environment",
                           "envKey": "ultrasonic_zone" }
        }
      },
      "esp32-device-2": {
        "defaultPlayerId": "player-2",
        "inputs": {
          "knob":      { "type": "knob",   "inputName": "move-x",     "inputRole": "player",
                         "minRaw": 0, "maxRaw": 4095 },
          "btn-up":    { "type": "button", "inputName": "move-y-up",  "inputRole": "player" },
          "btn-down":  { "type": "button", "inputName": "move-y-down","inputRole": "player" },
          "btn-fire":  { "type": "button", "inputName": "fire",       "inputRole": "player" }
        }
      }
    }
  }
}
```

---

## Phase 5 Firmware Changes

### ESP32 #1 config (`game/config.py`)

```python
DEVICE_ID = "esp32-device-1"

INPUTS = [
    # Player 1 — thumbstick
    { "slotId": "joystick-x",  "type": "knob",       "pin": 34,
      "min_raw": 0, "max_raw": 4095, "dead_zone": 0.04 },
    { "slotId": "joystick-y",  "type": "knob",       "pin": 35,
      "min_raw": 0, "max_raw": 4095, "dead_zone": 0.04 },
    { "slotId": "stick-click", "type": "button",     "pin": 32,
      "debounce_ms": 50 },

    # Admin buttons — operator use only
    { "slotId": "admin-start", "type": "button",     "pin": 14,
      "debounce_ms": 100 },
    { "slotId": "admin-end",   "type": "button",     "pin": 27,
      "debounce_ms": 100 },

    # Environmental sensors
    { "slotId": "temp",        "type": "dht11",      "pin": 4,
      "read_interval_ms": 2000 },
    { "slotId": "distance",    "type": "ultrasonic", "trig_pin": 5, "echo_pin": 18,
      "min_cm": 2.0, "max_cm": 200.0, "dead_zone_cm": 2.0,
      "read_interval_ms": 100 },
]
```

### ESP32 #2 config (`game/config.py`)

```python
DEVICE_ID = "esp32-device-2"

INPUTS = [
    # Player 2 — knob + buttons
    { "slotId": "knob",      "type": "knob",   "pin": 34,
      "min_raw": 0, "max_raw": 4095, "dead_zone": 0.02 },
    { "slotId": "btn-up",    "type": "button", "pin": 14, "debounce_ms": 50 },
    { "slotId": "btn-down",  "type": "button", "pin": 27, "debounce_ms": 50 },
    { "slotId": "btn-fire",  "type": "button", "pin": 32, "debounce_ms": 50 },
]
```

### No firmware code changes required

Both ESP32s use existing `KnobInput`, `ButtonInput`, `Dht11Input`, `UltrasonicInput`
classes unchanged. Only `config.py` files differ between devices.

---

## Phase 5 ASP.NET Changes

### Admin button routing

`admin-start` and `admin-end` slots on ESP32 #1 are registered with
`inputRole: "stage"`. When received, `MqttSubscriberService` routes them through
`InputBufferService` with `playerId: "stage"`. `GameInputRouter` in Unity receives
them and calls `GameSessionManager` directly:

```csharp
// In GameInputRouter.RouteStageInput()
case "admin-start":
    if (evt.EventType == "pressed")
        GameSessionManager.Instance?.StartRound();
    break;
case "admin-end":
    if (evt.EventType == "pressed")
        GameSessionManager.Instance?.EndRound();
    break;
```

This means the physical admin buttons on ESP32 #1 are functionally equivalent to
`POST /api/admin/start` and `POST /api/admin/end` — both paths call the same
`GameSessionManager` methods. The REST API remains available as a fallback.

### No other ASP.NET changes

Protocol, gRPC contract, session management, zone resolver, and relay service
are all unchanged.

---

## Phase 5 Unity Changes

### `GameSessionManager` — remove ready-up logic

Remove entirely:
- `NetworkVariable<bool> Ship1Ready`, `Ship2Ready`, `Bird1Ready`, `Bird2Ready`
- `ToggleShipReady()`, `ToggleBirdReady()` methods
- All ready-up UI NetworkVariables
- `Waiting → Countdown` transition via ready flags

Simplify `Waiting → Countdown` transition:
- Only triggered by `StartRound()` called from `GameInputRouter` (admin buttons)
  or from `AdminCommandDispatcher` (REST API → gRPC → AdminCommandDispatcher)
- No player input required during Waiting phase

### `GameInputRouter` — remove ready-up and bird routing

Remove:
- `HandleReadyUp()` method entirely
- `RouteBirdInput()` method
- `case "player-3": case "player-4":` switch arms

Simplify `HandleInput()`:
```csharp
private void HandleInput(IoTInputEvent evt)
{
    var phase = GameSessionManager.Instance?.CurrentPhase.Value
              ?? SessionPhase.Waiting;
    PlayerInputStateRegistry.Instance?
        .CheckSequenceNumber(evt.DeviceId, evt.SequenceNumber);

    // No ready-up handling — Waiting phase ignores all player input
    if (phase != SessionPhase.Active) return;

    HandleGameplay(evt);
}

private void HandleGameplay(IoTInputEvent evt)
{
    PlayerInputStateRegistry.Instance?.UpdateState(
        evt.PlayerId, evt.InputName, evt.Value);
    switch (evt.PlayerId)
    {
        case "player-1": RouteShipInput(evt); break;
        case "player-2": RouteShipInput(evt); break;  // same routing, different ship
        case "stage":    RouteStageInput(evt); break;
    }
}
```

### `RouteShipInput()` — add `move-y-up` / `move-y-down` handling

Player 2 uses two buttons for vertical movement. `PlayerShip` needs to handle
`move-y-up` and `move-y-down` as held-button directional inputs:

```csharp
private void RouteShipInput(IoTInputEvent evt)
{
    var ship = BirdSpawnManager.Instance?.GetShip(evt.PlayerId);
    if (ship == null) return;
    switch (evt.InputName)
    {
        case "move-x":
            ship.SetJoystickX(evt.Value);
            break;
        case "move-y":
            ship.SetJoystickY(evt.Value);
            break;
        case "move-y-up":
            // Button held = 1.0, released = 0.0 → map to joystick Y positive
            ship.SetJoystickY(evt.EventType == "pressed" ? 1.0f : 0.5f);
            break;
        case "move-y-down":
            ship.SetJoystickY(evt.EventType == "pressed" ? 0.0f : 0.5f);
            break;
        case "fire":
            if (evt.EventType == "pressed") ship.Fire();
            break;
    }
}
```

Note: `0.5f` maps to zero velocity (center dead-zone) in `PlayerShip.SetJoystickY()`.

### `PlayerShip` — knob position mapping

Player 2's `move-x` from the knob is an absolute position (0.0–1.0), not a velocity
input. `PlayerShip.SetJoystickX()` must detect which player it is and apply
differently, OR the ship itself maps the knob value to a screen X position:

```csharp
// In PlayerShip — server FixedUpdate
// For joystick players: _joystickInput.x is velocity (-1 to 1)
// For knob players:     _knobPosition is absolute (0.0–1.0)
// Simplest approach: always store as _joystickInput, let each ESP32 config produce
// the right value convention. Knob at 0.5 = center = 0 velocity implicitly.
// Pure position mapping is cleaner — implement if velocity feel is wrong.
```

Recommended: keep `SetJoystickX(float value)` identical for both players.
The knob's absolute position naturally produces different feel vs joystick velocity
input — this is acceptable and physically intuitive. Refine after playtesting.

### `BossEnemyBird` — remove entirely

Delete `BossEnemyBird.cs` and its prefab. Remove all references in:
- `BirdSpawnManager` — remove `SpawnBossBird()`, `GetBossBird()`, `TryGetBossBird()`
- `EnvironmentController.ApplyBossDrainZone4()` — remove boss bird drain logic
- `EnvironmentController.GetBossFlapBonus()` — remove
- `GameSessionManager.CheckWinConditions()` — remove boss health check
- `ConnectionApprovalHandler` — remove player-3, player-4 from valid player set

### `EnemyBird` — add AI shooting

Each `EnemyBird` needs a shoot coroutine. Add to `EnemyBird.cs`:

```csharp
// Server-only — runs after OnNetworkSpawn
private IEnumerator ShootCoroutine()
{
    yield return new WaitForSeconds(Random.Range(2f, 5f));  // initial delay
    while (true)
    {
        float cooldown = Random.Range(3f, 6f)
                       / (EnvironmentController.Instance?.GlobalGameSpeed.Value ?? 1f);
        yield return new WaitForSeconds(cooldown);

        if (!IsSpawned) yield break;
        if (GameSessionManager.Instance?.CurrentPhase.Value != SessionPhase.Active)
            continue;

        FireAtNearestShip();
    }
}

private void FireAtNearestShip()
{
    // Spawn a bird projectile that travels rightward (negative X direction)
    // Use a separate BirdProjectile prefab — travels left, damages ships on hit
}
```

### `EnemySpawnManager` — spawn from right, move left

Update spawn logic:
- Spawn position: right edge of screen, random Y within play area
- Initial velocity: leftward (negative X), configurable speed
- Despawn: on reaching left edge, call `PlayerShip.TakeDamage(15)` on nearest ship
- Spawn rate: driven by `EnvironmentController` humidity → `spawn_rate` envKey
  (unchanged from Phase 3)

### Win condition — wave timer only

`GameSessionManager.CheckWinConditions()` simplifies to:

```csharp
// Called each Update during Active phase
void CheckWinConditions()
{
    // Both ships dead → lose
    var ships = BirdSpawnManager.Instance?.GetAllShips();
    if (ships != null && ships.All(s => s.ShipHealth.Value <= 0))
    {
        TransitionTo(SessionPhase.Ended);
        CalloutManager.Instance?.ShowCalloutClientRpc("SHIPS DESTROYED", 5f);
        return;
    }

    // Timer expired → win
    if (SessionTimer.Value <= 0)
    {
        TransitionTo(SessionPhase.Ended);
        CalloutManager.Instance?.ShowCalloutClientRpc("WAVE CLEARED", 5f);
    }
}
```

---

## Phase 5 Scene Setup Notes

### Waiting screen
No ready-up UI needed. Show:
- "Waiting for admin to start round..."
- Current zone indicator (environmental sensor still active)
- Both ship spawn positions (ships visible but frozen)

### Round start flow
```
Admin presses physical btn on ESP32 #1
  OR
Admin calls POST /api/admin/start
      ↓
GameSessionManager.StartRound()
      ↓
SessionPhase.Waiting → SessionPhase.Countdown
      ↓
5 second countdown
      ↓
SessionPhase.Active — input processed, enemies spawn, timer starts
```

### Connection approval
`ConnectionApprovalHandler` valid player IDs:
```csharp
static readonly HashSet<string> ValidPlayerIds = new()
    { "player-1", "player-2" };  // removed player-3, player-4
```

---

## Phase 5 UI Simplification

### Design principle: world-space UI only, no screen-space HUD

Remove the screen-space HUD (`RoundUIManager` canvas panels) almost entirely.
All per-player information floats above the player's GameObject in world space,
visible to all clients because it is attached to the NetworkObject. The screen
stays clean — the game world communicates state directly.

---

### What to remove from `RoundUIManager`

Delete or disable these panels entirely:

- Boss HP bars — boss birds are gone
- Ship HP bars panel (screen-space) — replaced by world-space labels
- Shield indicator panel — replaced by world-space indicator
- Zone indicator panel — keep as a small persistent corner overlay only
- Ready-up player slot indicators — removed with ready-up logic
- Round timer countdown display — keep as a small corner overlay only

**Keep (minimal screen-space overlay):**
- Zone indicator — small top-right corner, shows current zone (1–4) and effect name
- Round timer — small top-left corner, seconds remaining during Active phase
- Callout system — centered large text, fades out (unchanged)
- Waiting screen text — "Waiting for admin to start..." (plain text, no panels)
- Ended screen — "WAVE CLEARED" / "SHIPS DESTROYED" (callout handles this)

The two kept overlays should be minimal — plain `TextMeshProUGUI` with no
background panels, no borders, no decoration. Small font, dimmed color.

---

### World-space labels on `PlayerShip`

Each `PlayerShip` prefab gets a child `Canvas` in **World Space** render mode
positioned above the ship mesh. This canvas is a NetworkObject child — it replicates
automatically with the ship.

```
PlayerShip (NetworkObject)
├── MeshRenderer (ship model)
├── Rigidbody
├── WorldSpaceUI (Canvas — World Space, scale 0.01)
│   ├── PlayerLabel     (TextMeshProUGUI — e.g. "P1")
│   ├── HealthBar       (simple Image fill, no background panel)
│   └── ShieldIndicator (TextMeshProUGUI — "🛡" shown only when ShieldActive)
└── PlayerShip.cs
```

**`PlayerShipUI.cs`** — attach to `WorldSpaceUI` canvas root:

```csharp
public class PlayerShipUI : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI _playerLabel;
    [SerializeField] private Image           _healthFill;
    [SerializeField] private TextMeshProUGUI _shieldIcon;

    private PlayerShip _ship;

    public void Init(PlayerShip ship, string label, Color color)
    {
        _ship = ship;
        _playerLabel.text  = label;   // "P1" or "P2"
        _playerLabel.color = color;   // matches ShipColor
        _healthFill.color  = color;
    }

    private void Update()
    {
        if (_ship == null) return;
        _healthFill.fillAmount  = _ship.ShipHealth.Value / 100f;
        _shieldIcon.enabled     = _ship.ShieldActive.Value;
    }
}
```

`BirdSpawnManager` calls `Init()` after spawning and assigning `ShipColor`.
The canvas faces the camera via a `LookAt` in `Update()` — or set the canvas
to **Billboard** mode so it always faces the camera automatically.

**Key constraint:** the `WorldSpaceUI` canvas must have `Sorting Layer` set to
`UI` and `Order in Layer` high enough to render above the ship mesh.

---

### World-space labels on `EnemyBird`

Each `EnemyBird` gets a minimal world-space label showing its HP (if enemies
have health) or nothing at all if they die in one hit. For simplicity:

- If enemies are one-hit-kill (recommended for Phase 5): **no world-space UI
  on enemies** — they just explode on hit. Clean.
- If enemies have HP (e.g. 3 hits): add a small health bar same pattern as ship.

**Recommendation: one-hit-kill enemies for Phase 5.** Simpler, faster-feeling,
no UI needed on enemies at all.

---

### Callout system — unchanged

`CalloutManager` fires centered large text that fades out. This is already
world-agnostic (screen-space canvas, centered). Keep as-is — it handles all
major game events (SHIELD ACTIVATED, TIME SLOW, WAVE CLEARED, etc.).

---

### Summary — UI components status

| UI Element | Phase 4 | Phase 5 |
|---|---|---|
| Ship HP bars (screen HUD) | ✅ Screen panel | ❌ Removed → world-space |
| Shield indicator (screen HUD) | ✅ Screen panel | ❌ Removed → world-space |
| Boss HP bars | ✅ Screen panel | ❌ Removed (no boss) |
| Player name labels (screen) | ✅ Screen panel | ❌ Removed → world-space |
| Ready-up indicators | ✅ Screen panel | ❌ Removed |
| Zone indicator | ✅ Screen panel | ✅ Kept — minimal corner text |
| Round timer | ✅ Screen panel | ✅ Kept — minimal corner text |
| Callout system | ✅ Centered fade | ✅ Unchanged |
| World-space ship label | ❌ None | ✅ New — above each ship |
| World-space ship HP bar | ❌ None | ✅ New — above each ship |
| World-space shield icon | ❌ None | ✅ New — above each ship |

---

## Phase 5 Summary — What Changed vs Phase 4 (Updated)

| Component | Changed | What |
|---|---|---|
| ESP32 firmware | Config only | New slot layouts per device |
| ASP.NET registry | Config only | New device/slot/role mapping |
| ASP.NET code | None | Protocol unchanged |
| gRPC proto | None | Unchanged |
| `GameSessionManager` | Yes | Remove ready-up flags and methods |
| `GameInputRouter` | Yes | Remove bird routing, remove ready-up, add admin-start/end |
| `PlayerShip` | Minor | Add `move-y-up`/`move-y-down` button handling |
| `PlayerShipUI` | New | World-space canvas — label, HP bar, shield icon |
| `BossEnemyBird` | Deleted | Removed entirely |
| `EnemyBird` | Yes | Add AI shooting coroutine; one-hit-kill (no HP bar needed) |
| `EnemySpawnManager` | Yes | Spawn from right, move left |
| `BirdSpawnManager` | Yes | Remove boss bird methods; call `PlayerShipUI.Init()` on spawn |
| `EnvironmentController` | Yes | Remove boss drain and flap bonus |
| `ConnectionApprovalHandler` | Yes | Remove player-3, player-4 |
| `RoundUIManager` | Yes | Remove most panels; keep zone + timer as minimal corner text |

Everything in the IoT pipeline (MQTT, gRPC, relay server, dispatcher pattern,
session management, authentication) is unchanged.

