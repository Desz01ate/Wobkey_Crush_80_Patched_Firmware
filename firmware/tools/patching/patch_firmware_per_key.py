#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-only
# Copyright (C) 2026 Wobkey Crush 80 firmware patch contributors
# Community-authored patch tooling; does not relicense the input firmware.
"""Wired per-key RGB patch for the exact Crush 80 v1.06 hue-patched image.

See docs/user/PER-KEY-RGB.md. This builder never opens a device or flashes firmware.
"""
import argparse
import binascii
import hashlib
import struct
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
BASE_SHA256 = "1c3d3970cd8430133db1ac7e722749f059cf3a20390c59f18b00d03ddd238561"
IMAGE_SIZE = 122196
FLASH = 0x20000000
CAVE = 0x304
CAVE_END = 0x7C8
STACK_TOP = 0xA0000
RESERVED_SIZE = 608
RGB_BUFFER = STACK_TOP - RESERVED_SIZE
FRAME_BYTES = 276
FRONT = RGB_BUFFER + FRAME_BYTES * 2
# FRONT-relative metadata: active offset +0, next fragment +4, frame ID +8,
# pending (0 idle / 1 queued / 2 active awaiting ACK) +12, ACK report +16..47.
MODE = STACK_TOP - 4
STREAM_LEDS = 9
STREAM_CHUNKS = 11
LED_COUNT = 92
CHUNK_LEDS = 8
CHANNEL = 0x7F
RENDER_SITE = 0xAA3C
RENDER_RETURN = 0xAA48
SET_SITE = 0xDBAC
GET_SITE = 0xDC34
VIA_RETURN = 0xD930
VIA_SILENT_RETURN = 0xD7CC
FRAME_BOUNDARY = 0xA104
USB_SEND = 0xCA78
MAIN_ENTRY = 0x36D4
ZERO, RA, SP, GP = 0, 1, 2, 3
T0, T1, T2, S1 = 5, 6, 7, 9
A0, A1, A2, A3, A4, A5 = 10, 11, 12, 13, 14, 15
T3, T6 = 28, 31
A6, A7 = 16, 17


def word(value):
    return struct.pack("<I", value & 0xFFFFFFFF)


def immediate(rd, source, value, function=0, opcode=0x13):
    if not -2048 <= value <= 2047:
        raise ValueError("RV32 immediate is out of range")
    return word(((value & 0xFFF) << 20) | (source << 15)
                | (function << 12) | (rd << 7) | opcode)


def register(rd, source1, source2, function=0, upper=0):
    return word((upper << 25) | (source2 << 20) | (source1 << 15)
                | (function << 12) | (rd << 7) | 0x33)


def store(source, base, offset, function=0):
    if not -2048 <= offset <= 2047:
        raise ValueError("RV32 store offset is out of range")
    value = offset & 0xFFF
    return word(((value >> 5) << 25) | (source << 20) | (base << 15)
                | (function << 12) | ((value & 31) << 7) | 0x23)


def branch(source1, source2, offset, function):
    if offset % 2 or not -4096 <= offset <= 4094:
        raise ValueError("RV32 branch is out of range")
    value = offset & 0x1FFF
    return word(((value >> 12) << 31) | (((value >> 5) & 63) << 25)
                | (source2 << 20) | (source1 << 15) | (function << 12)
                | (((value >> 1) & 15) << 8) | (((value >> 11) & 1) << 7) | 0x63)


def jump(offset):
    if offset % 2 or not -(1 << 20) <= offset < (1 << 20):
        raise ValueError("RV32 jump is out of range")
    value = offset & 0x1FFFFF
    return word(((value >> 20) << 31) | (((value >> 1) & 1023) << 21)
                | (((value >> 11) & 1) << 20) | (((value >> 12) & 255) << 12) | 0x6F)


def load_constant(rd, value):
    if -2048 <= value <= 2047:
        return immediate(rd, ZERO, value)
    low = value & 0xFFF
    if low >= 0x800:
        low -= 0x1000
    high = ((value - low) >> 12) & 0xFFFFF
    result = word((high << 12) | (rd << 7) | 0x37)
    return result + immediate(rd, rd, low) if low else result


def load_byte(rd, base, offset=0):
    return immediate(rd, base, offset, 4, 0x03)


def load_word(rd, base, offset=0):
    return immediate(rd, base, offset, 2, 0x03)


class Assembly:
    """Label resolver for injected RV32I/M/C instructions."""
    def __init__(self):
        self.code = bytearray()
        self.labels = {}
        self.fixups = []

    @property
    def pc(self):
        return CAVE + len(self.code)

    def emit(self, instruction):
        self.code.extend(instruction)

    def mark(self, label):
        if label in self.labels:
            raise ValueError(f"Duplicate label: {label}")
        self.labels[label] = self.pc

    def condition(self, source1, source2, label, function=0):
        self.fixups.append((len(self.code), label, source1, source2, function))
        self.emit(bytes(4))

    def go(self, label):
        self.fixups.append((len(self.code), label, None, None, None))
        self.emit(bytes(4))

    def exit_to(self, address):
        self.emit(jump(address - self.pc))

    def save(self, registers, frame):
        self.emit(immediate(SP, SP, -frame))
        for index, source in enumerate(registers):
            offset = index * 4
            # C.SWSP: the OEM image already requires the compressed ISA.
            self.emit(struct.pack("<H", 0xC002 | ((offset & 0x3C) << 7)
                                  | ((offset & 0xC0) << 1) | (source << 2)))

    def restore(self, registers, frame):
        for index, destination in enumerate(registers):
            offset = index * 4
            self.emit(struct.pack("<H", 0x4002 | ((offset & 0x20) << 7)
                                  | ((offset & 0x1C) << 2) | ((offset & 0xC0) >> 4)
                                  | (destination << 7)))
        self.emit(immediate(SP, SP, frame))

    def finish(self):
        for offset, label, source1, source2, function in self.fixups:
            distance = self.labels[label] - (CAVE + offset)
            self.code[offset:offset+4] = (jump(distance) if source1 is None
                                         else branch(source1, source2, distance, function))
        if self.pc > CAVE_END:
            raise ValueError("Injected routines exceed the verified code cave")
        return bytes(self.code)


def build_routines():
    asm = Assembly()
    # Called before main by both boot sequences; no calls or stack use here.
    asm.mark("initialize")
    asm.emit(load_constant(T0, RGB_BUFFER))
    asm.emit(load_constant(T1, STACK_TOP))
    asm.mark("zero")
    asm.emit(store(ZERO, T0, 0, 2))
    asm.emit(immediate(T0, T0, 4))
    asm.condition(T0, T1, "zero", 1)
    asm.emit(immediate(ZERO, RA, 0, 0, 0x67))

    # Runs before any OEM lighting work. FRONT is a 0/276 byte offset;
    # pending=1 swaps it once, pending=2 only retries the non-blocking ACK.
    asm.mark("boundary")
    asm.save((T0, T1), 16)
    asm.emit(load_constant(T1, FRONT))
    asm.emit(load_word(T0, T1, 12))
    asm.condition(T0, ZERO, "boundary_return")
    asm.emit(immediate(T0, T0, -1))
    asm.condition(T0, ZERO, "boundary_send", 1)
    asm.emit(load_word(T0, T1))
    asm.emit(immediate(T0, T0, FRAME_BYTES, 4))
    asm.emit(store(T0, T1, 0, 2))
    asm.emit(load_constant(T0, 2))
    asm.emit(store(T0, T1, 12, 2))
    asm.mark("boundary_send")
    asm.emit(load_byte(T0, GP, 0x2E9))
    asm.condition(T0, ZERO, "boundary_return")
    io_saved = (RA, T2, A0, A1, A2, A3, A4, A5, A6, A7)
    # This exact OEM helper clobbers a0..a7/t1, but preserves t2 and t3..t6.
    asm.save(io_saved, 48)
    asm.emit(immediate(T2, T1, 0))
    asm.emit(load_constant(A0, 4))
    asm.emit(immediate(A1, T1, 16))
    asm.emit(load_constant(A2, 32))
    asm.emit(word(int.from_bytes(jump(USB_SEND - asm.pc), "little") | (RA << 7)))
    asm.condition(A0, ZERO, "boundary_restore", 1)
    asm.emit(store(ZERO, T2, 12, 2))
    asm.mark("boundary_restore")
    asm.restore(io_saved, 48)
    asm.mark("boundary_return")
    asm.restore((T0, T1), 16)
    asm.emit(bytes.fromhex("5d71a2c4"))  # displaced OEM prologue
    asm.exit_to(FRAME_BOUNDARY + 4)

    # The RGB stores sit inside a loop; ABI caller-saved registers are live too.
    asm.mark("render")
    asm.save((T0, T1), 16)
    asm.emit(load_constant(T0, STACK_TOP))
    asm.emit(load_word(T0, T0, -4))
    asm.emit(load_constant(T1, 1))
    asm.condition(T0, T1, "original_stores", 1)
    asm.emit(load_constant(T1, RGB_BUFFER))
    asm.emit(load_constant(T0, FRONT))
    asm.emit(load_word(T0, T0))
    asm.emit(register(T1, T1, T0))
    asm.emit(register(T0, A4, T3, upper=0x20))
    asm.emit(register(T1, T1, T0))
    for channel in range(3):
        asm.emit(load_byte(T0, T1, channel))
        asm.emit(register(T0, T0, T6, upper=1))
        asm.emit(immediate(T0, T0, 8, 5))
        asm.emit(store(T0, A4, channel))
    asm.go("render_return")
    asm.mark("original_stores")
    for channel, source in enumerate((A0, A1, A3)):
        asm.emit(store(source, A4, channel))
    asm.mark("render_return")
    asm.restore((T0, T1), 16)
    asm.exit_to(RENDER_RETURN)

    # Inspect the channel BEFORE either legacy or VIA 11 OEM dispatch.
    for label, resume in (("set_entry", SET_SITE + 4), ("get_entry", GET_SITE + 4)):
        asm.mark(label)
        asm.save((T0, T1), 16)
        asm.emit(load_byte(T0, S1, 1))
        asm.emit(load_constant(T1, CHANNEL))
        asm.condition(T0, T1, label + "_vendor")
        asm.restore((T0, T1), 16)
        asm.emit(immediate(A5, GP, 0x74))  # displaced addigp a5, 0x74
        asm.exit_to(resume)
        asm.mark(label + "_vendor")
        asm.restore((T0, T1), 16)
        asm.go("vendor")

    saved = (T0, T1, T2, A0, A1, A2, A3, A4)
    asm.mark("vendor")
    asm.save(saved, 32)
    asm.emit(load_constant(A4, FRONT))
    asm.emit(load_byte(T0, S1, 2))
    asm.emit(load_constant(T1, 3))
    asm.condition(T0, T1, "fragment")  # byte 3 is the fragment's frame ID
    asm.emit(store(ZERO, S1, 3))
    asm.condition(T0, ZERO, "info")
    asm.emit(load_constant(T1, 1))
    asm.condition(T0, T1, "mode")
    asm.emit(load_constant(T1, 2))
    asm.condition(T0, T1, "chunk")
    asm.emit(load_constant(T1, 4))
    asm.condition(T0, T1, "commit")
    asm.go("unsupported")

    asm.mark("info")
    asm.emit(load_byte(T0, S1, 0))
    asm.emit(load_constant(T1, 8))
    asm.condition(T0, T1, "unsupported", 1)
    # Native report buffer is 4-byte aligned (gp+0x398); these fields are too.
    asm.emit(load_constant(T0, int.from_bytes(b"PKRG", "little")))
    asm.emit(store(T0, S1, 4, 2))
    asm.emit(load_constant(T0, 2 | (LED_COUNT << 8) | (CHUNK_LEDS << 16)))
    asm.emit(store(T0, S1, 8, 2))
    asm.emit(load_constant(T1, STACK_TOP))
    asm.emit(load_word(T0, T1, -4))
    asm.emit(store(T0, S1, 11))
    asm.emit(load_constant(T0, STREAM_LEDS | (STREAM_CHUNKS << 8)))
    asm.emit(store(T0, S1, 12, 2))
    asm.go("done")

    asm.mark("mode")
    asm.emit(load_constant(T2, STACK_TOP))
    asm.emit(load_byte(T0, S1, 0))
    asm.emit(load_constant(T1, 8))
    asm.condition(T0, T1, "read_mode")
    asm.emit(load_word(T0, A4, 12))
    asm.condition(T0, ZERO, "busy", 1)
    asm.emit(load_byte(T0, S1, 4))
    asm.emit(load_constant(T1, 1))
    asm.condition(T1, T0, "bad_mode", 6)
    asm.emit(store(T0, T2, -4, 2))
    asm.go("done")
    asm.mark("read_mode")
    asm.emit(load_word(T0, T2, -4))
    asm.emit(store(T0, S1, 4))
    asm.go("done")

    asm.mark("chunk")
    asm.emit(load_byte(T0, S1, 0))
    asm.emit(load_constant(T1, 8))
    asm.condition(T0, T1, "chunk_range")
    asm.emit(load_word(T0, A4, 12))
    asm.condition(T0, ZERO, "busy", 1)
    asm.mark("chunk_range")
    asm.emit(load_byte(T0, S1, 4))
    asm.emit(load_byte(A2, S1, 5))
    asm.condition(A2, ZERO, "bad_range")
    asm.emit(load_constant(T1, CHUNK_LEDS))
    asm.condition(T1, A2, "bad_range", 6)
    asm.emit(register(T1, T0, A2))
    asm.emit(load_constant(T2, LED_COUNT))
    asm.condition(T2, T1, "bad_range", 6)
    asm.emit(immediate(T1, T0, 1, 1))
    asm.emit(register(T0, T0, T1))
    asm.emit(load_constant(T2, RGB_BUFFER))
    asm.emit(load_word(T1, A4))
    asm.emit(register(T2, T2, T1))
    asm.emit(register(T2, T2, T0))
    asm.emit(immediate(T1, A2, 1, 1))
    asm.emit(register(A2, A2, T1))
    asm.emit(immediate(A0, S1, 6))
    asm.emit(immediate(A1, T2, 0))
    asm.emit(load_byte(T0, S1, 0))
    asm.emit(load_constant(A3, 0))
    asm.emit(load_constant(T1, 7))
    asm.condition(T0, T1, "copy")
    asm.emit(immediate(A0, T2, 0))
    asm.emit(immediate(A1, S1, 6))
    asm.mark("copy")
    asm.emit(load_byte(T0, A0))
    asm.emit(store(T0, A1, 0))
    asm.emit(immediate(A0, A0, 1))
    asm.emit(immediate(A1, A1, 1))
    asm.emit(immediate(A2, A2, -1))
    asm.condition(A2, ZERO, "copy", 1)
    asm.condition(A3, ZERO, "silent", 1)
    asm.go("done")

    asm.mark("fragment")
    asm.emit(load_byte(T1, S1, 0))
    asm.emit(load_constant(T2, 7))
    asm.condition(T1, T2, "unsupported", 1)
    asm.emit(load_byte(T1, GP, 0x153))
    asm.emit(load_constant(T2, 2))
    asm.condition(T1, T2, "unsupported", 1)
    asm.emit(load_word(T1, A4, 12))
    asm.condition(T1, ZERO, "silent", 1)
    asm.emit(load_byte(T0, S1, 4))
    asm.emit(load_byte(T2, S1, 3))
    asm.condition(T0, ZERO, "fragment_continue", 1)
    asm.emit(store(ZERO, A4, 4, 2))
    asm.emit(store(T2, A4, 8, 2))
    asm.mark("fragment_continue")
    asm.emit(load_word(T1, A4, 8))
    asm.condition(T1, T2, "invalid_fragment", 1)
    asm.emit(load_word(T1, A4, 4))
    asm.condition(T0, T1, "invalid_fragment", 1)
    asm.emit(load_constant(T1, STREAM_CHUNKS))
    asm.condition(T0, T1, "invalid_fragment", 7)
    asm.emit(load_constant(T1, STREAM_LEDS * 3))
    asm.emit(register(T1, T0, T1, upper=1))
    asm.emit(load_word(T2, A4))
    asm.emit(immediate(T2, T2, FRAME_BYTES, 4))
    asm.emit(load_constant(A1, RGB_BUFFER))
    asm.emit(register(A1, A1, T2))
    asm.emit(register(A1, A1, T1))
    asm.emit(immediate(A0, S1, 5))
    asm.emit(load_constant(A2, STREAM_LEDS * 3))
    asm.emit(load_constant(T1, STREAM_CHUNKS - 1))
    asm.condition(T0, T1, "fragment_copy", 1)
    asm.emit(load_constant(A2, 6))
    asm.mark("fragment_copy")
    asm.emit(immediate(T0, T0, 1))
    asm.emit(store(T0, A4, 4, 2))
    asm.emit(load_constant(A3, 1))
    asm.go("copy")
    asm.mark("invalid_fragment")
    asm.emit(load_constant(T0, 255))
    asm.emit(store(T0, A4, 4, 2))
    asm.go("silent")

    asm.mark("commit")
    asm.emit(load_byte(T0, S1, 0))
    asm.emit(load_constant(T1, 7))
    asm.condition(T0, T1, "unsupported", 1)
    asm.emit(load_byte(T0, GP, 0x153))
    asm.emit(load_constant(T1, 2))
    asm.condition(T0, T1, "unsupported", 1)
    asm.emit(load_word(T0, A4, 12))
    asm.condition(T0, ZERO, "busy", 1)
    asm.emit(load_word(T0, A4, 4))
    asm.emit(load_constant(T1, STREAM_CHUNKS))
    asm.condition(T0, T1, "bad_frame", 1)
    asm.emit(load_word(T0, A4, 8))
    asm.emit(load_byte(T1, S1, 4))
    asm.condition(T0, T1, "bad_frame", 1)
    asm.emit(store(ZERO, A4, 4, 2))
    asm.emit(load_word(T1, A4, MODE - FRONT))
    asm.condition(T1, ZERO, "commit_disabled")
    asm.emit(store(T0, A4, 20, 2))
    asm.emit(load_constant(T0, 0x00047F07))
    asm.emit(store(T0, A4, 16, 2))
    asm.emit(load_constant(T0, 1))
    asm.emit(store(T0, A4, 12, 2))
    asm.go("silent")
    asm.mark("commit_disabled")
    asm.emit(load_word(T0, A4))
    asm.emit(immediate(T0, T0, FRAME_BYTES, 4))
    asm.emit(store(T0, A4, 0, 2))
    asm.go("done")

    asm.mark("silent")
    asm.restore(saved, 32)
    asm.exit_to(VIA_SILENT_RETURN)

    for label, status in (("unsupported", 1), ("bad_range", 2), ("bad_mode", 3),
                          ("bad_frame", 4), ("busy", 5)):
        asm.mark(label)
        asm.emit(load_constant(T0, status))
        asm.emit(store(T0, S1, 3))
        asm.go("done")
    asm.mark("done")
    asm.restore(saved, 32)
    asm.exit_to(VIA_RETURN)
    return asm.finish(), asm.labels


def startup_call(initialize):
    # Absolute call works both in flash and in the copied ILM startup block.
    return (load_constant(T0, FLASH) + immediate(RA, T0, initialize, 0, 0x67)
            + load_constant(T0, MAIN_ENTRY) + immediate(RA, T0, 0, 0, 0x67)
            + jump(0))


def build_image(source):
    if len(source) != IMAGE_SIZE or hashlib.sha256(source).hexdigest() != BASE_SHA256:
        raise ValueError("Input must be the exact v1.06 firmware/releases/v1.06/v2_patched.bin baseline")
    if binascii.crc32(source) != 0xFFFFFFFF:
        raise ValueError("Invalid baseline firmware CRC")
    routines, labels = build_routines()
    if any(source[CAVE:CAVE + len(routines)]):
        raise ValueError("Verified code cave is not empty")
    result = bytearray(source)

    def replace(offset, expected, replacement):
        if len(expected) != len(replacement) or source[offset:offset+len(expected)] != expected:
            raise ValueError(f"Patch anchor mismatch at {offset:#x}")
        result[offset:offset+len(expected)] = replacement

    # Keep the relocation-sensitive AUIPC; reserve both RGB buffers and metadata.
    replace(0x44, bytes.fromhex("938202fc"), immediate(T0, T0, -0x40 - RESERVED_SIZE))
    replace(0x1CE, bytes.fromhex("938262fc"), immediate(T0, T0, -0x3A - RESERVED_SIZE))
    call = startup_call(labels["initialize"])
    replace(0x172, bytes.fromhex("0100973200e09382025682920100010001000100010001a0"), call)
    replace(0x2D0, bytes.fromhex("0100973200009382225982920100010001000100010001a0"), call)
    replace(RENDER_SITE, bytes.fromhex("2300a700a300b7002301d700"),
            jump(labels["render"] - RENDER_SITE) + bytes.fromhex("0100") * 4)
    replace(FRAME_BOUNDARY, bytes.fromhex("5d71a2c4"), jump(labels["boundary"] - FRAME_BOUNDARY))
    for site, label in ((SET_SITE, "set_entry"), (GET_SITE, "get_entry")):
        replace(site, bytes.fromhex("8b174007"), jump(labels[label] - site))
    result[CAVE:CAVE+len(routines)] = routines
    result[-4:] = word(binascii.crc32(result[:-4]) ^ 0xFFFFFFFF)
    if binascii.crc32(result) != 0xFFFFFFFF:
        raise ValueError("Patched CRC invariant failed")
    return bytes(result)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, default=ROOT / "firmware/releases/v1.06/v2_patched.bin")
    parser.add_argument("--output", type=Path, default=ROOT / "firmware/releases/v1.06-per-key/firmware_per_key_v3.bin")
    parser.add_argument("--ota-input", type=Path, default=ROOT / "firmware/releases/v1.06/code_2M_v2_patched.bin")
    parser.add_argument("--ota-output", type=Path, default=ROOT / "firmware/releases/v1.06-per-key/code_2M_per_key_v3.bin")
    args = parser.parse_args()
    source = args.input.read_bytes()
    image = build_image(source)
    ota = bytearray(args.ota_input.read_bytes())
    if len(ota) != 0x200000 or ota[256:256+len(source)] != source:
        raise ValueError("OTA wrapper does not contain the exact baseline firmware")
    ota[256:256+len(image)] = image
    args.output.write_bytes(image)
    args.ota_output.write_bytes(ota)
    routines, _ = build_routines()
    print(f"Per-key firmware: {args.output} ({len(image)} bytes)")
    print(f"OTA wrapper: {args.ota_output}")
    print(f"Code cave: {len(routines)}/{CAVE_END-CAVE} bytes; owned RGB storage: {RGB_BUFFER:#x}")
    print(f"SHA256: {hashlib.sha256(image).hexdigest()}")
    print(f"Firmware CRC: 0x{int.from_bytes(image[-4:], 'little'):08X}")
    print("Built only; no device was opened or flashed.")


if __name__ == "__main__":
    main()
