#!/usr/bin/env python3
"""Dump raw keymap buffer to see the actual layout."""
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

dev = find_device()
if not dev:
    print("Device not found"); sys.exit(1)
print(f"Device: {dev}")
fd = os.open(dev, os.O_RDWR | os.O_NONBLOCK)

# Drain
while select.select([fd], [], [], 0.05)[0]:
    os.read(fd, 256)

# Read a larger buffer to see the full layout
# 4 layers x 128 keys x 2 bytes = 1024 bytes
# But maybe there's MORE data or padding between layers
# Let's read 2048 bytes to see the full picture
TOTAL = 2048
buf = bytearray()
off = 0
while off < TOTAL:
    n = min(28, TOTAL - off)
    resp = transact(fd, [0x12, (off >> 8) & 0xFF, off & 0xFF, n])
    if resp and len(resp) >= 4 + n and resp[0] == 0x12:
        buf.extend(resp[4:4+n])
    else:
        print(f"Read failed at offset {off}")
        break
    off += n

print(f"\nRead {len(buf)} bytes from get_buffer (0x12)")

# Print as 16-bit big-endian keycodes, 16 per row
COLS = 16
for i in range(0, len(buf), 2):
    key_idx = i // 2
    row_in_layer = (key_idx % 128) // COLS
    col_in_layer = (key_idx % 128) % COLS
    layer = key_idx // 128

    if key_idx % 128 == 0:
        print(f"\n{'='*70}")
        print(f"  Buffer offset 0x{i:04X} — Layer {layer} (if 128 keys/layer)")
        print(f"{'='*70}")
    if col_in_layer == 0:
        sys.stdout.write(f"  R{row_in_layer}: ")

    kc = (buf[i] << 8) | buf[i+1]
    if kc == 0xFFFF:
        sys.stdout.write(" FFFF")
    elif kc == 0x0000:
        sys.stdout.write("    .")
    else:
        sys.stdout.write(f" {kc:04X}")

    if col_in_layer == COLS - 1:
        print()

os.close(fd)
