#!/usr/bin/env python3
"""
Rogue device simulator for Phase 6 security demo.
Demonstrates three attack scenarios against the IoTGame server.

Usage:
  python rogue_device.py no-cert              # Attack 1: connect without certificate
  python rogue_device.py flood                # Attack 2: flood valid-looking events (auto-quarantine)
  python rogue_device.py spoof-slot           # Attack 3: publish unknown slotId (alert only)

Options:
  --broker  MQTT broker host  (default: localhost)
  --port    MQTT broker port  (default: 1883)
  --ca      CA cert path      (default: ../Certs/ca.crt)
  --cert    Rogue device cert (default: ../Certs/device.crt)
  --key     Rogue device key  (default: ../Certs/device.key)

Requires: pip install paho-mqtt
"""
import argparse
import json
import time
import sys

try:
    import paho.mqtt.client as mqtt
except ImportError:
    print("ERROR: paho-mqtt not installed. Run: pip install paho-mqtt")
    sys.exit(1)

# ── Attack 1: No certificate ──────────────────────────────────────────────────
def attack_no_cert(args):
    print("[Attack 1] Attempting connection WITHOUT client certificate...")
    client = mqtt.Client(client_id="rogue-no-cert")

    if args.ca:
        import ssl
        ctx = ssl.create_default_context(ssl.Purpose.SERVER_AUTH, cafile=args.ca)
        # Deliberately no client cert loaded
        client.tls_set_context(ctx)

    connected = False

    def on_connect(c, userdata, flags, rc):
        nonlocal connected
        if rc == 0:
            connected = True
            print("[Attack 1] CONNECTED — broker accepted without cert (unexpected!)")
        else:
            print(f"[Attack 1] REJECTED by broker (rc={rc}) ✓ — mTLS is working")

    client.on_connect = on_connect
    try:
        client.connect(args.broker, args.port, keepalive=5)
        client.loop_start()
        time.sleep(3)
        client.loop_stop()
        if not connected:
            print("[Attack 1] Result: connection refused ✓")
    except Exception as e:
        print(f"[Attack 1] SSL/connection rejected: {e} ✓")


# ── Attack 2: Flood events ────────────────────────────────────────────────────
def attack_flood(args):
    print("[Attack 2] Flooding esp32-01 with 200 events/sec for 5 seconds...")
    print("           Expect: RateExceeded alert + device quarantined in dashboard")

    client = mqtt.Client(client_id="rogue-flood")
    _apply_tls(client, args)
    client.connect(args.broker, args.port)
    client.loop_start()

    topic = "game/devices/esp32-02/slot/knob"
    seq   = 0
    sent  = 0
    start = time.time()

    while time.time() - start < 5:
        payload = json.dumps({
            "deviceId": "esp32-02",
            "slotId": "knob",
            "inputType": "knob",
            "event": "change",
            "value": round(0.5 + 0.1 * (seq % 5), 2),
            "timestamp": int(time.time() * 1000),
            "sequenceNumber": seq
        })
        client.publish(topic, payload, qos=0)
        sent += 1
        seq  += 1
        time.sleep(0.005)  # 200 msgs/sec

    client.loop_stop()
    client.disconnect()
    print(f"[Attack 2] Sent {sent} events. Check dashboard — quarantine should appear ✓")


# ── Attack 3: Unknown slotId ──────────────────────────────────────────────────
def attack_spoof_slot(args):
    print("[Attack 3] Publishing to unknown slot 'inject-admin' on esp32-01...")
    print("           Expect: UnknownSlot alert in dashboard (no quarantine)")

    client = mqtt.Client(client_id="rogue-slot")
    _apply_tls(client, args)
    client.connect(args.broker, args.port)
    client.loop_start()

    topic   = "game/devices/{device_id}/slot/{slot_id}"
    payload = json.dumps({
        "deviceId": "esp32-02",
        "slotId": "inject-admin",
        "inputType": "button",
        "event": "pressed",
        "value": 1.0,
        "timestamp": int(time.time() * 1000),
        "sequenceNumber": 9999
    })
    client.publish(topic, payload, qos=0)
    time.sleep(1)
    client.loop_stop()
    client.disconnect()
    print("[Attack 3] Published. Check dashboard — UnknownSlot alert should appear ✓")


# ── Attack 4: Device spoofing (blocked by broker ACL) ────────────────────────
def attack_spoof_device(args):
    print("[Attack 4] esp32-02 cert trying to publish as esp32-01...")
    print("           Expect: broker silently drops — message never reaches server")

    client = mqtt.Client(client_id="rogue-spoof-device")
    _apply_tls(client, args)
    client.connect(args.broker, args.port)
    client.loop_start()

    # Publish to esp32-01's topic using esp32-02's cert (CN mismatch with ACL)
    topic   = "game/devices/esp32-01/slot/fire"
    payload = json.dumps({
        "deviceId": "esp32-01",
        "slotId": "fire",
        "inputType": "button",
        "event": "pressed",
        "value": 1.0,
        "timestamp": int(time.time() * 1000),
        "sequenceNumber": 1
    }) 
    client.publish(topic, payload, qos=0)
    time.sleep(1)
    client.loop_stop()
    client.disconnect()
    print("[Attack 4] Published to esp32-01 topic using esp32-02 cert.")
    print("           Check server logs — no MQTT message should arrive for esp32-01 ✓")


# ── Helpers ───────────────────────────────────────────────────────────────────
def _apply_tls(client, args):
    import ssl
    import os
    if not args.ca:
        return
    if not os.path.exists(args.ca):
        print(f"WARNING: CA cert not found at {args.ca} — skipping TLS")
        return
    ctx = ssl.create_default_context(ssl.Purpose.SERVER_AUTH, cafile=args.ca)
    ctx.check_hostname = False
    ctx.verify_mode    = ssl.CERT_NONE  # broker uses self-signed cert
    if args.cert and args.key and os.path.exists(args.cert) and os.path.exists(args.key):
        ctx.load_cert_chain(certfile=args.cert, keyfile=args.key)
        print(f"[TLS] Using client cert: {args.cert}")
    client.tls_set_context(ctx)


# ── Main ──────────────────────────────────────────────────────────────────────
if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="IoTGame rogue device attack simulator")
    parser.add_argument("attack", choices=["no-cert", "flood", "spoof-slot", "spoof-device"])
    parser.add_argument("--broker", default="localhost")
    parser.add_argument("--port",   type=int, default=1883)
    parser.add_argument("--ca",     default="../Certs/ca.crt")
    parser.add_argument("--cert",   default="../Certs/esp32-02/device.crt")
    parser.add_argument("--key",    default="../Certs/esp32-02/device.key")
    args = parser.parse_args()

    if args.attack == "no-cert":
        attack_no_cert(args)
    elif args.attack == "flood":
        attack_flood(args)
    elif args.attack == "spoof-slot":
        attack_spoof_slot(args)
    elif args.attack == "spoof-device":
        attack_spoof_device(args)