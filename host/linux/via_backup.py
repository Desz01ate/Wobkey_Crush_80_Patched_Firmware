#!/usr/bin/env python3
"""
Wobkey Crush 80 VIA Config Backup/Restore

Saves and restores VIA configuration (keymaps, RGB settings) over
the raw HID interface, so you don't lose config when flashing.

Firmware command map (mix of V3 IDs, protocol version 11):
  0x12 = get_buffer (bulk read keymap)     — WORKS
  0x05 = set_keycode (write one key)       — WORKS
  0x13 = set_buffer (bulk write)           — NOT IMPLEMENTED
  0x04 = get_keycode (read one key)        — NOT IMPLEMENTED
  0x08 = custom_get, 0x07 = custom_set     — WORKS

Usage:
    python3 via_backup.py save                    # BEFORE flashing
    python3 via_backup.py restore via_config.json  # after flashing
    python3 via_backup.py save --device /dev/hidraw3 config.json
"""

import argparse
import glob
import json
import os
import select
import sys
import time

VID = 0x320F
PID = 0x5055
VIA_USAGE_PAGE = b'\x06\x60\xff'  # Usage Page 0xFF60
REPORT_SIZE = 32  # VIA uses 32-byte reports, no report ID

# Firmware command IDs (confirmed via probing)
CMD_GET_PROTOCOL_VERSION = 0x01
CMD_SET_KEYCODE = 0x05          # write one key: [0x05, layer, row, col, kc_hi, kc_lo]
CMD_CUSTOM_SET = 0x07
CMD_CUSTOM_GET = 0x08
CMD_GET_LAYER_COUNT = 0x11
CMD_GET_BUFFER = 0x12           # bulk read: [0x12, off_hi, off_lo, size] -> data

# VIA matrix from JSON config (logical, not physical)
MATRIX_ROWS = 8
MATRIX_COLS = 16

# Custom channel 3 IDs (from Crush80-RGB-USB.JSON)
CUSTOM_CHANNEL = 3
CUSTOM_IDS = {
    1: "brightness",   # 0-9
    2: "effect",       # 0-18
    3: "speed",        # 0-4
    4: "color",        # H, S (2 bytes)
}


def find_via_device():
    """Find the VIA raw HID hidraw device."""
    for hidraw in sorted(glob.glob('/sys/class/hidraw/hidraw*')):
        name = os.path.basename(hidraw)
        dev_path = f'/dev/{name}'

        desc_path = os.path.join(hidraw, 'device', 'report_descriptor')
        if not os.path.exists(desc_path):
            continue

        with open(desc_path, 'rb') as f:
            desc = f.read()

        if VIA_USAGE_PAGE not in desc:
            continue

        # Verify VID/PID
        search = os.path.join(hidraw, 'device')
        for _ in range(5):
            ue = os.path.join(search, 'uevent')
            if os.path.exists(ue):
                with open(ue) as f:
                    for line in f:
                        if line.startswith('HID_ID='):
                            parts = line.strip().split('=')[1].split(':')
                            if len(parts) == 3:
                                vid = int(parts[1], 16)
                                pid = int(parts[2], 16)
                                if vid == VID and pid == PID:
                                    return dev_path
                            break
            search = os.path.dirname(search)

    return None


class ViaHID:
    def __init__(self, dev_path):
        self.fd = os.open(dev_path, os.O_RDWR | os.O_NONBLOCK)
        self.dev_path = dev_path
        # Drain any pending data
        while True:
            ready, _, _ = select.select([self.fd], [], [], 0.05)
            if not ready:
                break
            try:
                os.read(self.fd, 256)
            except OSError:
                break

    def close(self):
        os.close(self.fd)

    def write(self, data):
        """Send one report without expecting a reply (streaming fragments)."""
        if len(data) > REPORT_SIZE:
            raise ValueError('VIA report exceeds 32 bytes')
        packet = bytes([0]) + bytes(data).ljust(REPORT_SIZE, b'\0')
        if os.write(self.fd, packet) != len(packet):
            raise OSError('Incomplete VIA report write')

    def transact(self, data, timeout=2.0):
        """Send a VIA command and return the response."""
        self.write(data)

        ready, _, _ = select.select([self.fd], [], [], timeout)
        if ready:
            resp = os.read(self.fd, 256)
            return bytes(resp)
        return None

    def is_error(self, resp):
        """Check if response is the error handler (protocol version fallback)."""
        return resp and len(resp) >= 3 and resp[0] == 0x01 and resp[2] == 0x09

    def get_protocol_version(self):
        resp = self.transact([CMD_GET_PROTOCOL_VERSION])
        if resp and len(resp) >= 3 and resp[0] == CMD_GET_PROTOCOL_VERSION:
            return (resp[1] << 8) | resp[2]
        return None

    def get_layer_count(self):
        resp = self.transact([CMD_GET_LAYER_COUNT])
        if resp and len(resp) >= 2 and resp[0] == CMD_GET_LAYER_COUNT:
            return resp[1]
        return None

    def get_buffer(self, offset, size):
        """Bulk read keymap buffer via cmd 0x12. size <= 28."""
        resp = self.transact([CMD_GET_BUFFER,
                              (offset >> 8) & 0xFF, offset & 0xFF, size])
        if resp and len(resp) >= 4 + size and resp[0] == CMD_GET_BUFFER:
            return bytes(resp[4:4 + size])
        return None

    def set_keycode(self, layer, row, col, keycode, retries=3):
        """Write a single keycode via cmd 0x05 with retry."""
        cmd = [CMD_SET_KEYCODE, layer, row, col,
               (keycode >> 8) & 0xFF, keycode & 0xFF]
        for attempt in range(retries):
            try:
                resp = self.transact(cmd)
                if resp and resp[0] == CMD_SET_KEYCODE:
                    return True
            except (TimeoutError, OSError):
                pass
            # Wait and retry — keyboard may be busy writing to flash
            time.sleep(0.1 * (attempt + 1))
            # Drain any stale data
            while True:
                ready, _, _ = select.select([self.fd], [], [], 0.02)
                if not ready:
                    break
                try:
                    os.read(self.fd, 256)
                except OSError:
                    break
        return False

    def get_custom(self, channel, value_id):
        """Read custom channel value via cmd 0x08."""
        resp = self.transact([CMD_CUSTOM_GET, channel, value_id])
        if resp and len(resp) >= 4 and resp[0] == CMD_CUSTOM_GET:
            return list(resp[3:])
        return None

    def set_custom(self, channel, value_id, values):
        """Write custom channel value via cmd 0x07."""
        cmd = [CMD_CUSTOM_SET, channel, value_id] + list(values)
        resp = self.transact(cmd)
        return resp is not None and resp[0] == CMD_CUSTOM_SET


def cmd_save(hid, output_path):
    """Save VIA config to JSON file (raw binary keycodes)."""
    config = {}

    # Protocol version
    ver = hid.get_protocol_version()
    if ver is not None:
        config["protocol_version"] = ver
        print(f"  VIA protocol version: {ver}")

    # Layer count
    layers = hid.get_layer_count()
    if layers is None:
        print("  WARNING: Could not read layer count, assuming 4")
        layers = 4
    else:
        print(f"  Layers: {layers}")
    config["layers"] = layers

    # Read keymap via bulk get_buffer (0x12) — fast
    keys_per_layer = MATRIX_ROWS * MATRIX_COLS
    total_bytes = layers * keys_per_layer * 2  # 2 bytes per keycode, big-endian
    print(f"  Keymap: {layers} layers x {MATRIX_ROWS}x{MATRIX_COLS} = {total_bytes} bytes")

    layer_bytes = keys_per_layer * 2  # 256 bytes per layer
    chunk_size = 28  # max payload per VIA packet
    buf = bytearray()
    for layer in range(layers):
        layer_off = layer * layer_bytes
        off = 0
        while off < layer_bytes:
            # Don't cross layer boundary — firmware returns 0xFFFF for cross-boundary reads
            n = min(chunk_size, layer_bytes - off)
            data = hid.get_buffer(layer_off + off, n)
            if data is None:
                print(f"\n  ERROR: Failed to read keymap at offset {layer_off + off}")
                return
            buf.extend(data)
            off += n
            pct = (layer_off + off) * 100 // total_bytes
            sys.stdout.write(f"\r  Reading keymap... {pct}%")
            sys.stdout.flush()
    print(f"\r  Reading keymap... done ({len(buf)} bytes)")

    # Parse buffer into layers of 16-bit keycodes
    keymap = []
    for layer in range(layers):
        layer_data = []
        for i in range(keys_per_layer):
            off = (layer * keys_per_layer + i) * 2
            kc = (buf[off] << 8) | buf[off + 1]  # big-endian in VIA buffer
            layer_data.append(kc)
        keymap.append(layer_data)
    config["keymap"] = keymap

    # Custom channel values (RGB settings)
    rgb = {}
    for value_id, name in CUSTOM_IDS.items():
        resp = hid.get_custom(CUSTOM_CHANNEL, value_id)
        if resp:
            rgb[name] = resp[:5]
            if value_id == 4:
                print(f"  RGB {name}: H={resp[0]}, S={resp[1]}")
            else:
                print(f"  RGB {name}: {resp[0]}")
    config["rgb"] = rgb
    config["matrix_rows"] = MATRIX_ROWS
    config["matrix_cols"] = MATRIX_COLS

    with open(output_path, 'w') as f:
        json.dump(config, f, indent=2)
    print(f"\nSaved to {output_path}")


def cmd_restore(hid, input_path):
    """Restore VIA config from binary dump."""
    with open(input_path) as f:
        config = json.load(f)

    print(f"  Config from: {input_path}")

    if "keymap" not in config or not isinstance(config["keymap"][0], list):
        print("  ERROR: Not a binary dump from this script.")
        print("  usevia.app .layout.json cannot be used (non-standard keycode encoding).")
        print("  Run 'via_backup.py save' BEFORE flashing to create a binary dump.")
        return False

    keymap = config["keymap"]
    layers = len(keymap)
    print(f"  Keymap: {layers} layers, {len(keymap[0])} keys each")

    # Read first, then restore only changed entries, including KC_NO/0xFFFF.
    # A firmware update can reset an intentionally disabled key to a keycode.
    current_layers = []
    for layer_idx, layer_data in enumerate(keymap):
        current = bytearray()
        layer_size = len(layer_data) * 2
        for offset in range(0, layer_size, 28):
            chunk = hid.get_buffer(layer_idx * layer_size + offset,
                                   min(28, layer_size - offset))
            if chunk is None:
                print(f"  ERROR: Could not read layer {layer_idx} before restoring.")
                return False
            current.extend(chunk)
        current_layers.append([
            int.from_bytes(current[i:i+2], 'big')
            for i in range(0, layer_size, 2)
        ])

    # Write changed keycodes one at a time via set_keycode (0x05).
    total = sum(len(layer) for layer in keymap)
    count = 0
    written = 0
    failed = 0
    for layer_idx, layer_data in enumerate(keymap):
        for key_idx, kc in enumerate(layer_data):
            count += 1
            if current_layers[layer_idx][key_idx] == kc:
                continue
            row = key_idx // MATRIX_COLS
            col = key_idx % MATRIX_COLS
            ok = hid.set_keycode(layer_idx, row, col, kc)
            if not ok:
                failed += 1
            written += 1
            pct = count * 100 // total
            sys.stdout.write(f"\r  Writing keymap... {pct}% ({written} keys)")
            sys.stdout.flush()
            time.sleep(0.05)  # 50ms between writes


    if failed:
        print(f"\r  Writing keymap... done ({written} written, {failed} failures!)")
    else:
        print(f"\r  Writing keymap... done ({written} keys written, {total - written} unchanged)")

    # Restore RGB settings
    if "rgb" in config:
        for name, values in config["rgb"].items():
            value_id = None
            for vid, vname in CUSTOM_IDS.items():
                if vname == name:
                    value_id = vid
                    break
            if value_id is not None:
                ok = hid.set_custom(CUSTOM_CHANNEL, value_id, values)
                if not ok:
                    failed += 1
                print(f"  RGB {name}: {'restored' if ok else 'FAILED'}")

    if not failed:
        response = hid.transact([0x09])
        if not response or response[0] != 0x09:
            print("  ERROR: Configuration SAVE was not acknowledged.")
            return False
        time.sleep(0.2)

    # Verify complete keymaps; an ACK alone does not prove a successful restore.
    for layer_idx, layer_data in enumerate(keymap):
        expected = b''.join(kc.to_bytes(2, 'big') for kc in layer_data)
        for offset in range(0, len(expected), 28):
            size = min(28, len(expected) - offset)
            actual = hid.get_buffer(layer_idx * len(expected) + offset, size)
            if actual != expected[offset:offset+size]:
                print(f"  ERROR: Keymap verification failed at layer {layer_idx}, byte {offset}.")
                return False

    print("\nRestore complete; all keymap bytes verified." if not failed else "\nRestore failed.")
    return failed == 0


def main():
    parser = argparse.ArgumentParser(
        description='Wobkey Crush 80 VIA Config Backup/Restore',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog='Examples:\n'
               '  %(prog)s save                          # save to via_config.json\n'
               '  %(prog)s restore via_config.json        # restore after flashing\n'
               '  %(prog)s save --device /dev/hidraw3 config.json')
    parser.add_argument('action', choices=['save', 'restore'],
                        help='save or restore VIA config')
    parser.add_argument('file', nargs='?', default='via_config.json',
                        help='config file path (default: via_config.json)')
    parser.add_argument('--device', help='override hidraw device path')
    args = parser.parse_args()

    # Find device
    if args.device:
        dev_path = args.device
    else:
        print("Searching for VIA device (VID=0x320F PID=0x5055)...")
        dev_path = find_via_device()
        if dev_path is None:
            print("ERROR: VIA device not found")
            print("  - Is the keyboard connected via USB?")
            print("  - Check udev rules")
            sys.exit(1)

    print(f"Using {dev_path}")

    try:
        hid = ViaHID(dev_path)
    except PermissionError:
        print(f"ERROR: Permission denied on {dev_path}")
        print("  Check udev rules or run with sudo")
        sys.exit(1)

    try:
        if args.action == 'save':
            cmd_save(hid, args.file)
        else:
            if not os.path.exists(args.file):
                print(f"ERROR: File not found: {args.file}")
                sys.exit(1)
            if not cmd_restore(hid, args.file):
                sys.exit(1)
    finally:
        hid.close()


if __name__ == '__main__':
    main()
