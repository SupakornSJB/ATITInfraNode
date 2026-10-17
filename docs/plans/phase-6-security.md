# docs/plans/phase-6-security.md — ESP32 Firmware Security

> Place this file in the root of `ATITFinalProject-firmware/`.
> Read the main `CLAUDE.md` first for Phases 1–5 context.
> This file covers Phase 6 security additions to the firmware only.

---

## What This Phase Adds

Phase 6 adds **mutual TLS client certificate authentication** to the MQTT connection.
Instead of username/password credentials stored in plaintext `config.py`, each ESP32
presents an X.509 client certificate signed by a project-owned CA. The broker validates
the certificate on connect — no valid cert, no connection.

This directly addresses the biggest firmware security gap: credentials readable from
flash memory by anyone with physical access to the device.

---

## Threat Model (Firmware Layer)

| Threat | Before Phase 6 | After Phase 6 |
|---|---|---|
| Attacker reads flash → gets MQTT credentials | ✅ Possible | ⚠️ Gets cert only — useless without CA-signed private key |
| Rogue device connects with stolen credentials | ✅ Possible | ❌ Rejected — cert not signed by trusted CA |
| Man-in-the-middle intercepts MQTT traffic | ❌ TLS already prevents | ❌ Still prevented |
| Tampered firmware with valid credentials | ✅ Would work | ⚠️ Still works if cert is also copied — see note below |

> **Note on firmware tampering:** Full protection requires secure boot (eFuse burning
> on ESP32). This is out of scope for Phase 6 due to irreversibility. The threat model
> documents this as a known limitation. The PKI approach significantly raises the bar
> — an attacker needs both the cert file AND the private key, both stored separately.

---

## CA and Certificate Structure

```
Project CA (root)
  └── esp32-device-1.crt  (signed for deviceId "esp32-device-1")
  └── esp32-device-2.crt  (signed for deviceId "esp32-device-2")
  └── rogue-device.crt    (signed for testing — used by Python rogue script)
```

The CA private key never leaves the provisioning machine. Each device gets:
- `ca.crt` — CA certificate (to verify the broker's server cert)
- `device.crt` — device client certificate
- `device.key` — device private key

The broker presents its own server certificate signed by the same CA or a separate
server CA. The ESP32 validates the broker cert against `ca.crt` — preventing
man-in-the-middle at the device level.

---

## Project Structure Changes

```
iotgame-firmware/
├── CLAUDE.md                    ← Phase 1–5
├── docs/plans/phase-6-security.md           ← this file
├── certs/                       ← NEW — never commit private keys to git
│   ├── .gitignore               ← ignores *.key, *.crt except ca.crt
│   ├── ca.crt                   ← CA cert — safe to commit
│   ├── esp32-device-1/
│   │   ├── device.crt
│   │   └── device.key           ← gitignored
│   └── esp32-device-2/
│       ├── device.crt
│       └── device.key           ← gitignored
├── scripts/
│   ├── gen_ca.sh                ← NEW — generates root CA
│   ├── gen_device_cert.sh       ← NEW — signs a device certificate
│   └── provision_device.py      ← NEW — flashes certs onto ESP32 via mpremote
├── tools/
│   └── rogue_device.py          ← NEW — Python MQTT rogue device simulator
├── iotgame/                     ← library — mqtt.py updated
│   ├── mqtt.py                  ← UPDATED — TLS client cert support
│   └── ...
└── game/
    └── config.py                ← UPDATED — cert paths replace credentials
```

---

## CA and Certificate Generation Scripts

### `scripts/gen_ca.sh`

Generates the root CA. Run once per project.

```bash
#!/bin/bash
# Generates root CA key and self-signed certificate
# Output: certs/ca.key (KEEP SECRET), certs/ca.crt (safe to distribute)

set -e
mkdir -p certs

# Generate CA private key (4096-bit RSA)
openssl genrsa -out certs/ca.key 4096

# Generate self-signed CA certificate (valid 10 years)
openssl req -new -x509 -days 3650 \
  -key certs/ca.key \
  -out certs/ca.crt \
  -subj "/C=TH/O=ATITFinalProject/CN=ATITFinalProject-CA"

echo "CA generated: certs/ca.crt"
echo "KEEP certs/ca.key SECRET — never commit or share"
```

### `scripts/gen_device_cert.sh`

Signs a certificate for one device. Run once per device.

```bash
#!/bin/bash
# Usage: ./gen_device_cert.sh <device-id>
# Example: ./gen_device_cert.sh esp32-device-1

set -e
DEVICE_ID=$1
if [ -z "$DEVICE_ID" ]; then
  echo "Usage: $0 <device-id>"
  exit 1
fi

OUTDIR="certs/$DEVICE_ID"
mkdir -p "$OUTDIR"

# Generate device private key (2048-bit RSA — smaller for ESP32 performance)
openssl genrsa -out "$OUTDIR/device.key" 2048

# Generate CSR — CN must match deviceId used in registry
openssl req -new \
  -key "$OUTDIR/device.key" \
  -out "$OUTDIR/device.csr" \
  -subj "/C=TH/O=ATITFinalProject/CN=$DEVICE_ID"

# Sign with CA (valid 2 years)
openssl x509 -req -days 730 \
  -in "$OUTDIR/device.csr" \
  -CA certs/ca.crt \
  -CAkey certs/ca.key \
  -CAcreateserial \
  -out "$OUTDIR/device.crt"

rm "$OUTDIR/device.csr"

echo "Device cert generated: $OUTDIR/device.crt"
echo "Flash onto device with: python scripts/provision_device.py $DEVICE_ID"
```

### `scripts/provision_device.py`

Flashes cert files onto the ESP32 using `mpremote`.

```python
#!/usr/bin/env python3
"""
Usage: python scripts/provision_device.py <device-id> [--port /dev/ttyUSB0]
Flashes ca.crt, device.crt, device.key onto the ESP32 filesystem.
"""
import argparse
import subprocess
import sys
from pathlib import Path

def flash_file(port: str, local_path: Path, remote_path: str):
    result = subprocess.run(
        ["mpremote", "connect", port, "cp", str(local_path), f":{remote_path}"],
        capture_output=True, text=True
    )
    if result.returncode != 0:
        print(f"ERROR flashing {local_path}: {result.stderr}")
        sys.exit(1)
    print(f"  Flashed {local_path} → :{remote_path}")

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("device_id", help="e.g. esp32-device-1")
    parser.add_argument("--port", default="/dev/ttyUSB0")
    args = parser.parse_args()

    cert_dir = Path("certs") / args.device_id
    if not cert_dir.exists():
        print(f"ERROR: {cert_dir} not found. Run gen_device_cert.sh first.")
        sys.exit(1)

    print(f"Provisioning {args.device_id} on {args.port}...")
    flash_file(args.port, Path("certs/ca.crt"),          "ca.crt")
    flash_file(args.port, cert_dir / "device.crt",        "device.crt")
    flash_file(args.port, cert_dir / "device.key",        "device.key")
    print("Done. Verify with: mpremote connect <port> ls")

if __name__ == "__main__":
    main()
```

---

## Firmware Changes

### `iotgame/mqtt.py` — TLS client certificate support

Replace the existing TLS connection code with full mutual TLS:

```python
import ussl
import network
import ubinascii

class MqttClient:
    def __init__(self, client_id, host, port, user=None, password=None,
                 ca_cert_path=None, cert_path=None, key_path=None):
        self._client_id  = client_id
        self._host       = host
        self._port       = port
        self._user       = user       # None when using cert auth
        self._password   = password   # None when using cert auth
        self._ca_cert    = self._load_file(ca_cert_path)
        self._cert       = self._load_file(cert_path)
        self._key        = self._load_file(key_path)
        self._client     = None

    def _load_file(self, path: str | None) -> bytes | None:
        if path is None:
            return None
        try:
            with open(path, 'rb') as f:
                return f.read()
        except OSError as e:
            print(f"[MQTT] Could not load {path}: {e}")
            return None

    def connect(self):
        from umqtt.simple import MQTTClient as _MQTTClient

        ssl_params = {
            "server_side": False,
            "server_hostname": self._host,
        }
        if self._ca_cert:
            ssl_params["cadata"] = self._ca_cert
            ssl_params["cert_reqs"] = ussl.CERT_REQUIRED  # verify broker cert
        if self._cert and self._key:
            ssl_params["certfile"] = self._cert   # client cert
            ssl_params["keyfile"]  = self._key    # client key

        self._client = _MQTTClient(
            client_id   = self._client_id,
            server      = self._host,
            port        = self._port,
            user        = self._user,       # None → broker uses cert CN as identity
            password    = self._password,
            ssl         = True,
            ssl_params  = ssl_params,
            keepalive   = 60,
        )
        self._client.connect()
        print(f"[MQTT] Connected to {self._host}:{self._port} with client cert")

    def publish(self, topic: str, payload_dict: dict):
        import json
        try:
            self._client.publish(topic, json.dumps(payload_dict), qos=0)
        except Exception as e:
            print(f"[MQTT] Publish error: {e}")
            self._reconnect()

    def _reconnect(self):
        try:
            self._client.disconnect()
        except Exception:
            pass
        self.connect()
```

> **MicroPython TLS note:** `ussl.CERT_REQUIRED` and client cert loading behaviour
> varies across MicroPython firmware builds. Test on the exact firmware version
> flashed onto the ESP32. If `certfile`/`keyfile` kwargs are not supported, load
> cert bytes and pass as `certfile=self._cert` (bytes object directly).
> MicroPython v1.22+ on ESP32 with mbedTLS supports this fully.

### `game/config.py` — replace credentials with cert paths

```python
# WiFi
WIFI_SSID     = "your-ssid"
WIFI_PASSWORD = "your-password"

# Device identity — must match CN in device certificate
DEVICE_ID = "esp32-device-1"

# MQTT Broker
MQTT_HOST = "your-broker-host"
MQTT_PORT = 8883           # TLS port — unchanged

# Certificate paths (flashed onto ESP32 by provision_device.py)
CA_CERT_PATH  = "ca.crt"
CERT_PATH     = "device.crt"
KEY_PATH      = "device.key"

# MQTT credentials — set both to None when using cert auth
# Mosquitto with use_identity_as_username=true uses the cert CN as username
MQTT_USER     = None
MQTT_PASSWORD = None

# Input slots — unchanged from Phase 5
INPUTS = [ ... ]
```

---

## Rogue Device Simulator

### `tools/rogue_device.py`

A Python script that simulates three attack scenarios for the demo. Run on a laptop.

```python
#!/usr/bin/env python3
"""
Rogue device simulator for Phase 6 security demo.
Demonstrates three attack scenarios:

  python rogue_device.py no-cert        # Attack 1: connect without certificate
  python rogue_device.py flood          # Attack 2: flood valid-looking events
  python rogue_device.py spoof-slot     # Attack 3: publish unknown slotId

Requires: pip install paho-mqtt
"""
import argparse
import json
import ssl
import time
import random
import sys
import paho.mqtt.client as mqtt

BROKER_HOST = "your-broker-host"
BROKER_PORT = 8883
CA_CERT     = "certs/ca.crt"

# ── Attack 1: No certificate ──────────────────────────────────────────────────
def attack_no_cert():
    print("[Attack 1] Attempting connection WITHOUT client certificate...")
    client = mqtt.Client(client_id="rogue-no-cert")
    ctx = ssl.create_default_context(ssl.Purpose.SERVER_AUTH, cafile=CA_CERT)
    # Deliberately no client cert loaded
    client.tls_set_context(ctx)

    def on_connect(c, userdata, flags, rc):
        if rc == 0:
            print("[Attack 1] CONNECTED — broker accepted (unexpected!)")
        else:
            print(f"[Attack 1] REJECTED — broker refused (rc={rc}) ✓")

    client.on_connect = on_connect
    try:
        client.connect(BROKER_HOST, BROKER_PORT, keepalive=5)
        client.loop_start()
        time.sleep(3)
        client.loop_stop()
    except ssl.SSLError as e:
        print(f"[Attack 1] SSL rejected at handshake: {e} ✓")
    except Exception as e:
        print(f"[Attack 1] Connection failed: {e} ✓")

# ── Attack 2: Flood events ────────────────────────────────────────────────────
def attack_flood(cert_path: str, key_path: str):
    print("[Attack 2] Flooding with 200 events/sec using valid-looking device ID...")
    client = mqtt.Client(client_id="rogue-flood")
    ctx = ssl.create_default_context(ssl.Purpose.SERVER_AUTH, cafile=CA_CERT)
    ctx.load_cert_chain(certfile=cert_path, keyfile=key_path)
    client.tls_set_context(ctx)
    client.connect(BROKER_HOST, BROKER_PORT)
    client.loop_start()

    topic   = "game/devices/esp32-device-1/slot/fire"
    payload = json.dumps({
        "deviceId": "esp32-device-1", "slotId": "fire",
        "inputType": "button", "event": "pressed",
        "value": 1.0, "timestamp": 0, "sequenceNumber": 0
    })

    sent = 0
    start = time.time()
    while time.time() - start < 5:   # flood for 5 seconds
        client.publish(topic, payload, qos=0)
        sent += 1
        time.sleep(0.005)   # 200 msgs/sec

    client.loop_stop()
    print(f"[Attack 2] Sent {sent} events in 5 seconds")
    print("[Attack 2] Check dashboard — device should be quarantined ✓")

# ── Attack 3: Unknown slotId ──────────────────────────────────────────────────
def attack_spoof_slot(cert_path: str, key_path: str):
    print("[Attack 3] Publishing unknown slotId 'inject-admin'...")
    client = mqtt.Client(client_id="rogue-slot")
    ctx = ssl.create_default_context(ssl.Purpose.SERVER_AUTH, cafile=CA_CERT)
    ctx.load_cert_chain(certfile=cert_path, keyfile=key_path)
    client.tls_set_context(ctx)
    client.connect(BROKER_HOST, BROKER_PORT)
    client.loop_start()

    topic   = "game/devices/esp32-device-1/slot/inject-admin"
    payload = json.dumps({
        "deviceId": "esp32-device-1", "slotId": "inject-admin",
        "inputType": "button", "event": "pressed",
        "value": 1.0, "timestamp": int(time.time()), "sequenceNumber": 9999
    })
    client.publish(topic, payload, qos=0)
    time.sleep(1)
    client.loop_stop()
    print("[Attack 3] Published. Check dashboard — unknown slot alert should appear ✓")

# ── Main ──────────────────────────────────────────────────────────────────────
if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("attack",
        choices=["no-cert", "flood", "spoof-slot"],
        help="Which attack to simulate")
    parser.add_argument("--cert", default="certs/rogue/device.crt",
        help="Rogue device cert (for attacks that need one)")
    parser.add_argument("--key",  default="certs/rogue/device.key")
    args = parser.parse_args()

    if args.attack == "no-cert":
        attack_no_cert()
    elif args.attack == "flood":
        attack_flood(args.cert, args.key)
    elif args.attack == "spoof-slot":
        attack_spoof_slot(args.cert, args.key)
```

> **Demo setup:** Generate a `rogue` device cert with `gen_device_cert.sh rogue`
> before the demo. Use it for attacks 2 and 3 (cert is valid but device is not
> in the registry). Attack 1 uses no cert at all.

---

## Mosquitto Broker Configuration

Update `/etc/mosquitto/mosquitto.conf` or your Mosquitto config file:

```
# TLS configuration
listener 8883
protocol mqtt

# Server certificate (signed by same CA or separate server CA)
cafile   /etc/mosquitto/certs/ca.crt
certfile /etc/mosquitto/certs/broker.crt
keyfile  /etc/mosquitto/certs/broker.key

# Require client certificate — no cert = no connection
require_certificate true

# Use the certificate CN as the MQTT client username
# This allows ACL rules to reference the deviceId
use_identity_as_username true

# ACL file — each device can only publish to its own topic prefix
acl_file /etc/mosquitto/acl.conf
```

### `/etc/mosquitto/acl.conf`

```
# Pattern substitution: %u = MQTT username = certificate CN = deviceId
pattern write game/devices/%u/#

# ASP.NET server (subscribes to all device topics)
# Server connects with its own cert (CN = aspnet-server)
user aspnet-server
topic read game/devices/#
```

---

## `.gitignore` additions for `certs/`

```
# certs/.gitignore
*.key
*.p12
*.pfx
# DO commit:
# ca.crt
# */device.crt
```

---

## Testing Checklist

```
[ ] gen_ca.sh produces ca.crt and ca.key
[ ] gen_device_cert.sh esp32-device-1 produces device.crt and device.key
[ ] provision_device.py esp32-device-1 flashes files onto ESP32
[ ] ESP32 connects to broker using cert (no username/password)
[ ] python rogue_device.py no-cert → SSL rejected at handshake
[ ] python rogue_device.py flood → quarantine appears in dashboard within 5 seconds
[ ] python rogue_device.py spoof-slot → unknown slot alert appears in dashboard
[ ] Removing ca.crt from ESP32 → connection refused (broker cert validation fails)
```
