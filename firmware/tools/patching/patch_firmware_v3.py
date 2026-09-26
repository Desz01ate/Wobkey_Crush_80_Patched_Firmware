#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-only
# Copyright (C) 2026 Wobkey Crush 80 firmware patch contributors
# Community-authored patch tooling; does not relicense the input firmware.
"""
Wobkey Crush 80 Firmware v2 (v1.06) Full HSV Fix Patch (v3)

Extends the v2 hue-only patch to incorporate saturation into the RGB output.

Problem with v2 patch:
  The code cave converts H→RGB (pure saturated color) and stores S separately
  in state[0x1e]. The firmware's effect engine applies state[0x1e] as a GLOBAL
  white overlay to ALL LEDs — including dark/off keys in animated effects.
  This makes dynamic saturation unusable: lowering S washes out the entire board.

Fix (v3):
  1. Code cave now does full H+S → RGB (desaturation baked into the RGB values)
  2. After blending, overrides a3 = 0xFF so the store handler writes
     state[0x1e] = ~0xFF = 0x00 (no additional global white from effect engine)

Formula per channel:
  final = (pure_channel * S) >> 8 + (255 - S)

This produces correct desaturated colors in the RGB output while preventing
the global white overlay in non-solid effects.

Usage:
    python3 firmware/tools/patching/patch_firmware_v3.py
    # Input:  /tmp/wobkey_v2_fw.bin (extracted v2 firmware)
    # Optional OTA input: /tmp/wobkey_v2_resources/code_2M.bin
    # Output: firmware/releases/v1.06/v3_patched.bin and firmware/releases/v1.06/code_2M_v3_patched.bin
"""

import struct
import binascii
import os
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "../../.."))

# RV32I/M instruction encoders
def pack32(val): return struct.pack('<I', val & 0xFFFFFFFF)
def pack16(val): return struct.pack('<H', val & 0xFFFF)

def addi(rd, rs1, imm):
    return pack32(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0x13)

def slli(rd, rs1, shamt):
    return pack32((shamt << 20) | (rs1 << 15) | (0b001 << 12) | (rd << 7) | 0x13)

def srli(rd, rs1, shamt):
    return pack32((shamt << 20) | (rs1 << 15) | (0b101 << 12) | (rd << 7) | 0x13)

def add(rd, rs1, rs2):
    return pack32((rs2 << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0x33)

def sub(rd, rs1, rs2):
    return pack32((0x20 << 25) | (rs2 << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0x33)

def mul(rd, rs1, rs2):
    return pack32((0x01 << 25) | (rs2 << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0x33)

def sb(rs2, base, offset):
    off = offset & 0xFFF
    return pack32(((off >> 5) << 25) | (rs2 << 20) | (base << 15) | (0b000 << 12) | ((off & 0x1F) << 7) | 0x23)

def lb(rd, base, offset):
    return pack32(((offset & 0xFFF) << 20) | (base << 15) | (0b000 << 12) | (rd << 7) | 0x03)

def lbu(rd, base, offset):
    return pack32(((offset & 0xFFF) << 20) | (base << 15) | (0b100 << 12) | (rd << 7) | 0x03)

def bltu(rs1, rs2, offset):
    off = offset if offset >= 0 else offset + (1 << 13)
    off &= 0x1FFF
    return pack32(((off >> 12) << 31) | (((off >> 5) & 0x3F) << 25) | (rs2 << 20) |
                  (rs1 << 15) | (0b110 << 12) | (((off >> 1) & 0xF) << 8) |
                  (((off >> 11) & 1) << 7) | 0x63)

def j_instr(offset):
    off = offset if offset >= 0 else offset + (1 << 21)
    off &= 0x1FFFFF
    return pack32(((off >> 20) << 31) | (((off >> 1) & 0x3FF) << 21) |
                  (((off >> 11) & 1) << 20) | (((off >> 12) & 0xFF) << 12) | 0x6F)

# Registers
x0, t0, t1, t2, t3 = 0, 5, 6, 7, 28
s1, a2, a3, a4, a5 = 9, 12, 13, 14, 15

# v2 addresses
CODE_CAVE = 0x10A8
JUMP_TARGET = 0xDBF4
PATCH_ADDR = 0xDD72


def build_sector(sector, h_offset):
    """Build one hue sector handler (computes frac, sets pure R/G/B to a4[0..2])."""
    sc = bytearray()
    sc += addi(t0, a2, (-h_offset) & 0xFFF)  # t0 = H - sector_start
    sc += slli(t1, t0, 1)                      # t1 = t0 * 2
    sc += slli(t2, t0, 2)                      # t2 = t0 * 4
    sc += add(t0, t1, t2)                      # t0 = t0 * 6 (fractional brightness)

    if sector == 0:    # H=0-42:   R=255, G=frac, B=0
        sc += addi(t1, x0, 255); sc += sb(t1, a4, 0); sc += sb(t0, a4, 1); sc += sb(x0, a4, 2)
    elif sector == 1:  # H=43-85:  R=255-frac, G=255, B=0
        sc += addi(t1, x0, 255); sc += sub(t2, t1, t0); sc += sb(t2, a4, 0); sc += sb(t1, a4, 1); sc += sb(x0, a4, 2)
    elif sector == 2:  # H=86-128: R=0, G=255, B=frac
        sc += addi(t1, x0, 255); sc += sb(x0, a4, 0); sc += sb(t1, a4, 1); sc += sb(t0, a4, 2)
    elif sector == 3:  # H=129-171:R=0, G=255-frac, B=255
        sc += addi(t1, x0, 255); sc += sub(t2, t1, t0); sc += sb(x0, a4, 0); sc += sb(t2, a4, 1); sc += sb(t1, a4, 2)
    elif sector == 4:  # H=172-214:R=frac, G=0, B=255
        sc += addi(t1, x0, 255); sc += sb(t0, a4, 0); sc += sb(x0, a4, 1); sc += sb(t1, a4, 2)
    elif sector == 5:  # H=215-255:R=255, G=0, B=255-frac
        sc += addi(t1, x0, 255); sc += sub(t2, t1, t0); sc += sb(t1, a4, 0); sc += sb(x0, a4, 1); sc += sb(t2, a4, 2)
    return sc


def build_saturation_blend():
    """
    Post-process: blend pure RGB in a4[0..2] toward white using S (in a3).
    Formula: final_ch = (pure_ch * S) >> 8 + (255 - S)
    Then override a3 = 0xFF so store handler writes state[0x1e] = ~0xFF = 0.
    """
    blend = bytearray()

    # t3 = 255 - S (the white component, computed once)
    blend += addi(t1, x0, 255)         # t1 = 255
    blend += sub(t3, t1, a3)           # t3 = 255 - S

    # Channel R (a4[0])
    blend += lbu(t0, a4, 0)            # t0 = pure_R
    blend += mul(t0, t0, a3)           # t0 = pure_R * S
    blend += srli(t0, t0, 8)           # t0 = (pure_R * S) >> 8
    blend += add(t0, t0, t3)           # t0 = final_R
    blend += sb(t0, a4, 0)            # a4[0] = final_R

    # Channel G (a4[1])
    blend += lbu(t0, a4, 1)            # t0 = pure_G
    blend += mul(t0, t0, a3)           # t0 = pure_G * S
    blend += srli(t0, t0, 8)           # t0 = (pure_G * S) >> 8
    blend += add(t0, t0, t3)           # t0 = final_G
    blend += sb(t0, a4, 1)            # a4[1] = final_G

    # Channel B (a4[2])
    blend += lbu(t0, a4, 2)            # t0 = pure_B
    blend += mul(t0, t0, a3)           # t0 = pure_B * S
    blend += srli(t0, t0, 8)           # t0 = (pure_B * S) >> 8
    blend += add(t0, t0, t3)           # t0 = final_B
    blend += sb(t0, a4, 2)            # a4[2] = final_B

    # Override a3 so store handler writes state[0x1e] = ~0xFF = 0
    blend += addi(a3, x0, 0xFF)        # a3 = 255

    return blend


def build_code_cave():
    """Build the full HSV→RGB code cave at CODE_CAVE."""
    sectors = {s: build_sector(s, s * 43) for s in range(6)}
    blend = build_saturation_blend()

    # Layout: displaced lb, 5 comparisons, sector 5 (fallthrough),
    # sectors 0-4, saturation blend, final jump
    lb_size = 4
    cmp_size = 40  # 5 thresholds × 8 bytes (addi + bltu)
    s5_end = CODE_CAVE + lb_size + cmp_size + len(sectors[5]) + 4  # +4 for j after s5

    starts = {}
    pos = s5_end
    for s in range(5):
        starts[s] = pos
        pos += len(sectors[s]) + 4  # +4 for j after each sector

    # end_label is where all sectors jump to (saturation blend)
    end_label = pos
    # After blend, the final jump to store handler
    final_jump_pos = end_label + len(blend)

    # Build code
    code = bytearray()
    pc = CODE_CAVE

    # Displaced instruction: lb a3, 3(s1) -- loads S byte
    code += lb(a3, s1, 3); pc += 4

    # Compare H against sector thresholds, branch to matching sector
    for i, thresh in enumerate([43, 86, 129, 172, 215]):
        code += addi(t1, x0, thresh); pc += 4
        code += bltu(a2, t1, starts[i] - pc); pc += 4

    # Sector 5 (fallthrough for H >= 215)
    code += sectors[5]; pc += len(sectors[5])
    code += j_instr(end_label - pc); pc += 4

    # Sectors 0-4
    for s in range(5):
        code += sectors[s]; pc += len(sectors[s])
        code += j_instr(end_label - pc); pc += 4

    # Saturation blend (post-processes a4[0..2] using S in a3)
    code += blend; pc += len(blend)

    # Final: jump to store handler
    code += j_instr(JUMP_TARGET - pc); pc += 4

    return code


def apply_patch():
    fw_path = "/tmp/wobkey_v2_fw.bin"
    with open(fw_path, "rb") as f:
        fw = bytearray(f.read())

    print(f"Input: {fw_path} ({len(fw)} bytes)")

    # Verify original bytes at patch site
    expected = bytes.fromhex("83863400bdbd")
    actual = fw[PATCH_ADDR:PATCH_ADDR+6]
    assert actual == expected, \
        f"Patch site at 0x{PATCH_ADDR:X} doesn't match!\n  Expected: {expected.hex()}\n  Got:      {actual.hex()}"

    # Verify code cave is empty
    cave = build_code_cave()
    cave_size = len(cave)
    print(f"Code cave size: {cave_size} bytes")

    assert all(b == 0 for b in fw[CODE_CAVE:CODE_CAVE+cave_size]), \
        f"Code cave at 0x{CODE_CAVE:X} is not empty (need {cave_size} bytes)!"

    # Insert code cave
    print(f"Code cave: {cave_size} bytes at 0x{CODE_CAVE:X}")
    fw[CODE_CAVE:CODE_CAVE+cave_size] = cave

    # Patch jump at 0xDD72: replace lb+c.j with j+nop
    fw[PATCH_ADDR:PATCH_ADDR+4] = j_instr(CODE_CAVE - PATCH_ADDR)
    fw[PATCH_ADDR+4:PATCH_ADDR+6] = pack16(0x0001)  # c.nop

    # Update CRC (last 4 bytes = ~CRC32 of preceding data)
    crc = binascii.crc32(bytes(fw[:-4])) & 0xFFFFFFFF
    old_crc = struct.unpack('<I', fw[-4:])[0]
    fw[-4:] = struct.pack('<I', crc ^ 0xFFFFFFFF)
    new_crc = struct.unpack('<I', fw[-4:])[0]
    print(f"CRC: 0x{old_crc:08X} → 0x{new_crc:08X}")

    # Verify CRC
    assert binascii.crc32(bytes(fw)) & 0xFFFFFFFF == 0xFFFFFFFF, "CRC check failed!"

    # Write patched firmware
    out_dir = os.path.join(ROOT, "firmware", "releases", "v1.06")
    os.makedirs(out_dir, exist_ok=True)

    fw_out = os.path.join(out_dir, "v3_patched.bin")
    with open(fw_out, "wb") as f:
        f.write(fw)
    print(f"Written {fw_out} ({len(fw)} bytes)")

    # Create patched code_2M.bin for OTA flasher
    code2m_path = "/tmp/wobkey_v2_resources/code_2M.bin"
    try:
        with open(code2m_path, "rb") as f:
            code2m = bytearray(f.read())
        code2m[256:256+len(fw)] = fw
        ota_out = os.path.join(out_dir, "code_2M_v3_patched.bin")
        with open(ota_out, "wb") as f:
            f.write(code2m)
        print(f"Written {ota_out} ({len(code2m)} bytes)")
    except FileNotFoundError:
        print(f"  {code2m_path} not found, skipping OTA package")

    print("\nPatch applied successfully!")
    print("\nBehavior change vs v2:")
    print("  - RGB output now includes saturation blend (white/gray representable)")
    print("  - state[0x1e] forced to 0 (no global white overlay from effect engine)")
    print("  - Animated effects no longer wash out when S < 0xFF")


if __name__ == "__main__":
    apply_patch()
