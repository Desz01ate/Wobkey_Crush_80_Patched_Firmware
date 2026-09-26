#!/usr/bin/env python3
"""Quick debug: send VIA commands and print raw responses."""
import os, select, sys, glob

VID, PID = 0x320F, 0x5055

def find_device():
    for hidraw in sorted(glob.glob('/sys/class/hidraw/hidraw*')):
        name = os.path.basename(hidraw)
        desc_path = os.path.join(hidraw, 'device', 'report_descriptor')
        if not os.path.exists(desc_path):
            continue
        with open(desc_path, 'rb') as f:
            desc = f.read()
        if b'\x06\x60\xff' not in desc:
            continue
        search = os.path.join(hidraw, 'device')
        for _ in range(5):
            ue = os.path.join(search, 'uevent')
            if os.path.exists(ue):
                with open(ue) as f:
                    for line in f:
                        if line.startswith('HID_ID='):
                            parts = line.strip().split('=')[1].split(':')
                            if len(parts) == 3 and int(parts[1],16) == VID and int(parts[2],16) == PID:
                                return f'/dev/{name}'
                            break
            search = os.path.dirname(search)
    return None

def transact(fd, data):
    pkt = bytearray(33)
    pkt[0] = 0x00
    for i, b in enumerate(data[:32]):
        pkt[1+i] = b
    os.write(fd, bytes(pkt))
    ready, _, _ = select.select([fd], [], [], 2.0)
    if ready:
        return os.read(fd, 256)
    return None

def is_error(resp):
    """Check if response is the protocol version error (0x01, 0x00, 0x09)."""
    return resp and len(resp) >= 3 and resp[0] == 0x01 and resp[1] == 0x00 and resp[2] == 0x09

dev = find_device()
if not dev:
    print("Device not found"); sys.exit(1)
print(f"Device: {dev}")
fd = os.open(dev, os.O_RDWR | os.O_NONBLOCK)

# Drain
while select.select([fd], [], [], 0.05)[0]:
    os.read(fd, 256)

# Read current keycode at L0 R0 C0 via get_buffer (0x12, known working)
resp = transact(fd, [0x12, 0, 0, 2])
if resp and len(resp) >= 6 and resp[0] == 0x12:
    orig_hi, orig_lo = resp[4], resp[5]
    print(f"Current L0 R0 C0 = 0x{orig_hi:02X}{orig_lo:02X} (via 0x12 get_buffer)")
else:
    print("ERROR: get_buffer (0x12) failed!")
    os.close(fd); sys.exit(1)

tests = [
    # Read commands
    ("get_keycode (0x04) L0R0C0", [0x04, 0, 0, 0]),
    ("macro_get_count (0x0C)",    [0x0C]),
    ("macro_get_buf_size (0x0D)", [0x0D]),
    # Write commands — all write the SAME value back (safe, no change)
    ("set_keycode (0x05) L0R0C0 same val", [0x05, 0, 0, 0, orig_hi, orig_lo]),
    ("set_buffer V2 (0x0D) off=0 same val", [0x0D, 0, 0, 2, orig_hi, orig_lo]),
    ("set_buffer V3 (0x13) off=0 same val", [0x13, 0, 0, 2, orig_hi, orig_lo]),
    # Brute force: try all cmd IDs 0x02-0x15 to find which ones are implemented
]

# Also scan all commands 0x00-0x15
print("\n=== Scanning all command IDs 0x00-0x15 ===")
for cmd_id in range(0x16):
    resp = transact(fd, [cmd_id])
    err = is_error(resp)
    status = "ERROR (unrecognized)" if err else "IMPLEMENTED"
    rx_str = ' '.join(f'{b:02X}' for b in resp[:8]) if resp else "<timeout>"
    print(f"  0x{cmd_id:02X}: {status:22s}  RX: {rx_str}...")

print("\n=== Targeted tests ===")
for label, cmd in tests:
    resp = transact(fd, cmd)
    err = is_error(resp)
    if resp:
        rx_str = ' '.join(f'{b:02X}' for b in resp[:20])
        suffix = '...' if len(resp) > 20 else ''
        tag = " *** ERROR ***" if err else ""
        print(f"\n{label}{tag}")
        print(f"  TX: {' '.join(f'{b:02X}' for b in cmd)}")
        print(f"  RX: {rx_str}{suffix}")
    else:
        print(f"\n{label}")
        print(f"  TX: {' '.join(f'{b:02X}' for b in cmd)}")
        print(f"  RX: <timeout>")

os.close(fd)
