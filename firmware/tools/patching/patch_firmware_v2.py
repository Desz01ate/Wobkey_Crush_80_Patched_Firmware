#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-only
# Copyright (C) 2026 Wobkey Crush 80 firmware patch contributors
# Community-authored patch tooling; does not relicense the input firmware.
"""
Wobkey Crush 80 Firmware v2 (v1.06) Hue Fix Patch

Same fix as v1 patch, adapted for v2 firmware addresses.

The VIA SET handler for cmd 0x83 (COLOR) stores the H byte correctly
but loads stale RGB from a precomputed table instead of converting H→RGB.
This patch inserts an HSV→RGB color wheel conversion in a code cave
and redirects the handler to use it.

v1 → v2 address changes:
  - Code cave:   0x10A4 → 0x10A8 (shifted +4, byte at 0x10A4 is non-zero in v2)
  - Patch site:  0xDA4E → 0xDD72 (same instruction bytes)
  - Jump target: 0xD8D0 → 0xDBF4 (store handler)

Usage:
    python3 firmware/tools/patching/patch_firmware_v2.py
    # Input:  /tmp/wobkey_v2_fw.bin (extracted v2 firmware)
    # Optional OTA input: /tmp/wobkey_v2_resources/code_2M.bin
    # Output: firmware/releases/v1.06/v2_patched.bin and firmware/releases/v1.06/code_2M_v2_patched.bin
"""

import struct
import binascii
import os
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "../../.."))

# RV32I instruction encoders
def pack32(val): return struct.pack('<I', val & 0xFFFFFFFF)
def pack16(val): return struct.pack('<H', val & 0xFFFF)

def addi(rd, rs1, imm):
    return pack32(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0x13)

def slli(rd, rs1, shamt):
    return pack32((shamt << 20) | (rs1 << 15) | (0b001 << 12) | (rd << 7) | 0x13)

def add(rd, rs1, rs2):
    return pack32((rs2 << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0x33)

def sub(rd, rs1, rs2):
    return pack32((0x20 << 25) | (rs2 << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0x33)

def sb(rs2, base, offset):
    off = offset & 0xFFF
    return pack32(((off >> 5) << 25) | (rs2 << 20) | (base << 15) | (0b000 << 12) | ((off & 0x1F) << 7) | 0x23)

def lb(rd, base, offset):
    return pack32(((offset & 0xFFF) << 20) | (base << 15) | (0b000 << 12) | (rd << 7) | 0x03)

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
x0, t0, t1, t2 = 0, 5, 6, 7
s1, a2, a3, a4, a5 = 9, 12, 13, 14, 15

# v2 addresses
CODE_CAVE = 0x10A8
JUMP_TARGET = 0xDBF4
PATCH_ADDR = 0xDD72

def build_sector(sector, h_offset):
    """Build one HSV sector handler (computes frac, sets R/G/B)."""
    sc = bytearray()
    sc += addi(t0, a2, (-h_offset) & 0xFFF)  # t0 = H - sector_start
    sc += slli(t1, t0, 1)                      # t1 = t0 * 2
    sc += slli(t2, t0, 2)                      # t2 = t0 * 4
    sc += add(t0, t1, t2)                      # t0 = t0 * 6 (fractional brightness)

    if sector == 0:    # H=0-42:   R=255, G=frac, B=0
        sc += addi(t1,x0,255); sc += sb(t1,a4,0); sc += sb(t0,a4,1); sc += sb(x0,a4,2)
    elif sector == 1:  # H=43-85:  R=255-frac, G=255, B=0
        sc += addi(t1,x0,255); sc += sub(t2,t1,t0); sc += sb(t2,a4,0); sc += sb(t1,a4,1); sc += sb(x0,a4,2)
    elif sector == 2:  # H=86-128: R=0, G=255, B=frac
        sc += addi(t1,x0,255); sc += sb(x0,a4,0); sc += sb(t1,a4,1); sc += sb(t0,a4,2)
    elif sector == 3:  # H=129-171:R=0, G=255-frac, B=255
        sc += addi(t1,x0,255); sc += sub(t2,t1,t0); sc += sb(x0,a4,0); sc += sb(t2,a4,1); sc += sb(t1,a4,2)
    elif sector == 4:  # H=172-214:R=frac, G=0, B=255
        sc += addi(t1,x0,255); sc += sb(t0,a4,0); sc += sb(x0,a4,1); sc += sb(t1,a4,2)
    elif sector == 5:  # H=215-255:R=255, G=0, B=255-frac
        sc += addi(t1,x0,255); sc += sub(t2,t1,t0); sc += sb(t1,a4,0); sc += sb(x0,a4,1); sc += sb(t2,a4,2)
    return sc

def build_code_cave():
    """Build the HSV->RGB code cave at CODE_CAVE."""
    sectors = {s: build_sector(s, s * 43) for s in range(6)}

    # Calculate layout: displaced lb, 5 comparisons, sector 5 (fallthrough), sectors 0-4, final jump
    lb_size = 4
    cmp_size = 40  # 5 thresholds × 8 bytes (addi + bltu)
    s5_end = CODE_CAVE + lb_size + cmp_size + len(sectors[5]) + 4  # +4 for j after s5

    starts = {}
    pos = s5_end
    for s in range(5):
        starts[s] = pos
        pos += len(sectors[s]) + 4  # +4 for j after each sector
    end_label = pos

    # Build code
    code = bytearray()
    pc = CODE_CAVE

    # Displaced instruction: lb a3, 3(s1)
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
    cave_size = 276
    assert all(b == 0 for b in fw[CODE_CAVE:CODE_CAVE+cave_size]), \
        f"Code cave at 0x{CODE_CAVE:X} is not empty!"

    # Build and insert code cave
    cave = build_code_cave()
    print(f"Code cave: {len(cave)} bytes at 0x{CODE_CAVE:X}")
    fw[CODE_CAVE:CODE_CAVE+len(cave)] = cave

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

    fw_out = os.path.join(out_dir, "v2_patched.bin")
    with open(fw_out, "wb") as f:
        f.write(fw)
    print(f"Written {fw_out} ({len(fw)} bytes)")

    # Create patched code_2M.bin for OTA flasher
    code2m_path = "/tmp/wobkey_v2_resources/code_2M.bin"
    try:
        with open(code2m_path, "rb") as f:
            code2m = bytearray(f.read())
        code2m[256:256+len(fw)] = fw
        ota_out = os.path.join(out_dir, "code_2M_v2_patched.bin")
        with open(ota_out, "wb") as f:
            f.write(code2m)
        print(f"Written {ota_out} ({len(code2m)} bytes)")
    except FileNotFoundError:
        print(f"  {code2m_path} not found, skipping OTA package")

    print("\nPatch applied successfully!")

if __name__ == "__main__":
    apply_patch()
