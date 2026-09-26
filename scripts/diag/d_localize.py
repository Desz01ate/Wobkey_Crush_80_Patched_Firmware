#!/usr/bin/env python3
"""
Diagnostic D - localize where the per-key patch fails.

Runs a 4-step sequence with the CURRENTLY-FLASHED firmware
(firmware_per_key_v2.bin). At each step the keyboard holds for 4
seconds; the user reports what they see. The combination of answers
pins the failure to one of:

  (a) magic-byte recognizer in the color cave never fires
      (per-LED path never engages)
  (b) magic recognized but mode flag write doesn't stick
      (per-LED path engages but mode is read back as not-0xA5 next frame)
  (c) magic + mode both work, but buffer writes/index mapping wrong
      (per-LED path engages and the table walks indices our writes
       don't cover, or RAM at our address gets overwritten)

After this we will know which architectural fix (if any) we need.
"""
import os, sys, time
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from probe_via_perkey import find_via_device, build_packet, drain

# Hue convention (matches firmware HSV):
#   0   = red
#   85  = green
#   170 = blue

def send(fd, payload, settle=0.05):
    os.write(fd, build_packet(payload))
    drain(fd, settle)

def hold(label, seconds=4):
    print(f"\n  [{label}]  observe for {seconds}s ...")
    time.sleep(seconds)

def main():
    dev = find_via_device()
    if not dev:
        sys.exit("No VIA device found.")
    print(f"VIA device: {dev}\n")
    fd = os.open(dev, os.O_RDWR | os.O_NONBLOCK)
    drain(fd, 0.05)

    try:
        # Setup: LIGHT_MODE, max brightness
        send(fd, [0x07, 3, 2, 6])   # effect = LIGHT_MODE
        send(fd, [0x07, 3, 1, 9])   # brightness = 9

        # ---- Step 1: baseline RED via normal color SET (no magic) ----
        print("STEP 1: normal color SET to RED")
        print("        Expected: keyboard turns RED uniformly.")
        send(fd, [0x07, 3, 4, 0, 255])
        hold("step 1 - should be RED")

        # ---- Step 2: ambiguous magic packet ----
        # Payload[3] = H = 85 (green hue) if cave does NOT see magic
        # Payload[5] = 0xA5 (magic) - if cave sees it, OEM color SET is skipped
        print("\nSTEP 2: send `[H=85, S=255, magic=0xA5, mode=0]`")
        print("        If cave SEES magic: keyboard stays RED (OEM skipped).")
        print("        If cave MISSES magic: keyboard turns GREEN.")
        send(fd, [0x07, 3, 4, 85, 255, 0xA5, 0x00, 0, 0])
        hold("step 2 - RED-or-GREEN?")

        # Make sure mode is 0 going into step 3.
        send(fd, [0x07, 3, 4, 0, 255, 0xA5, 0x00, 0, 0])
        send(fd, [0x07, 3, 4, 0, 255])     # baseline RED again (normal path)
        time.sleep(0.4)

        # ---- Step 3: turn per-key mode ON, then issue normal color SET ----
        # Payload[6] = 0xA5 should set PER_LED_MODE := 0xA5.
        # Payload[8] = 0 (count) -> no buffer writes; buffer remains as-is.
        print("\nSTEP 3: enable per-key mode (count=0, no buffer writes), then")
        print("        send `[H=0, S=255]` (plain RED color SET).")
        print("        If mode flag STUCK at 0xA5: LIGHT_MODE per-key path runs,")
        print("        reads uninitialized buffer -> keyboard is NOT clean red.")
        print("        It will be DARK, BLACK-ISH, or RANDOM-LOOKING per key.")
        print("        If mode flag DID NOT stick: keyboard stays clean RED.")
        send(fd, [0x07, 3, 4, 0, 255, 0xA5, 0xA5, 0, 0])
        send(fd, [0x07, 3, 4, 0, 255])
        hold("step 3 - clean-RED or dark/random?")

        # ---- Step 4: full per-LED chunk-write with all SOLID GREEN ----
        # If steps 2 and 3 both indicated "magic + mode work", and this still
        # doesn't show green, the buffer writes themselves are landing
        # somewhere other than where the LIGHT_MODE cave reads.
        print("\nSTEP 4: per-LED chunk-write of SOLID GREEN to slots 0..104")
        print("        Expected (if everything works): keyboard turns GREEN.")
        CHUNK = 7
        for start in range(0, 105, CHUNK):
            payload = [0x07, 3, 4, 0, 255, 0xA5, 0xA5, start, CHUNK] + [0, 255, 0] * CHUNK
            send(fd, payload, 0.005)
        hold("step 4 - green or unchanged?")

        # Disable + restore
        send(fd, [0x07, 3, 4, 0, 255, 0xA5, 0x00, 0, 0])
        send(fd, [0x07, 3, 4, 0, 255])
        print("\nRestored: per-key OFF, color RED.")
    finally:
        os.close(fd)


if __name__ == "__main__":
    main()
