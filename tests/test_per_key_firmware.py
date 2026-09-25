"""Behavioral checks of the generated RV32 firmware, not its Python encoders.

Install tests/requirements.txt; run python -m unittest discover -s tests -v.
WOBKEY_TEST_IMAGE can select an existing image for a failing-before check.
"""
import os
import struct
import sys
import unittest
from pathlib import Path

import unicorn as uc
from unicorn import riscv_const as rv

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
FLASH = 0x20000000
GP = 0x80800
STATE = GP + 0x74
FRAMEBUFFER = GP + 0x1474
BUFFER = 0x9FEE0
MODE = 0x9FFFC
PACKET = GP + 0x398
NEW_SP = 0x9FEE0
BASE = (ROOT / "firmware/v2_patched.bin").read_bytes()


def reg(index):
    return getattr(rv, f"UC_RISCV_REG_X{index}")


def signed(value, width):
    return value - (1 << width) if value & (1 << (width - 1)) else value


def fields(word, chunks, sign=False):
    value = width = 0
    for bit, count in chunks:
        value = (value << count) | ((word >> bit) & ((1 << count) - 1))
        width += count
    return signed(value, width) if sign else value


class Machine:
    """Unicorn plus only the Andes instructions used by the exercised OEM paths.

    Vendor fields are defined by andestech/qemu XAndesV5Isa.decode. Ordinary
    RV32I/M/C instructions, including all injected code, execute in Unicorn.
    """
    def __init__(self, image):
        self.cpu = uc.Uc(uc.UC_ARCH_RISCV, uc.UC_MODE_RISCV32)
        self.cpu.mem_map(FLASH, 0x20000)
        self.cpu.mem_write(FLASH, image)
        self.cpu.mem_map(0, 0x10000)
        self.cpu.mem_write(0, image[0x190:0x2E8])
        self.cpu.mem_map(0x80000, 0x20000)
        self.cpu.reg_write(reg(3), GP)
        self.cpu.reg_write(reg(2), NEW_SP)
        self.cpu.mem_write(STATE + 0x1F, b"\x0b")
        self.writes = []
        self.cpu.hook_add(uc.UC_HOOK_CODE, self._vendor)
        self.cpu.hook_add(uc.UC_HOOK_MEM_WRITE, self._write)

    def _write(self, cpu, access, address, size, value, user):
        self.writes.append((address, size, value))

    def _vendor(self, cpu, pc, size, user):
        word = int.from_bytes(cpu.mem_read(pc, 4), "little")
        opcode = word & 127
        function = (word >> 12) & 7
        if opcode == 0x0B:
            subtype = (word >> 12) & 3
            gp = cpu.reg_read(reg(3))
            if subtype == 3:
                offset = fields(word, [(31, 1), (15, 2), (17, 3), (7, 1),
                                       (25, 6), (8, 4), (14, 1)], True)
                value = cpu.reg_read(reg((word >> 20) & 31)) & 255
                address = (gp + offset) & 0xFFFFFFFF
                cpu.mem_write(address, bytes([value]))
                self.writes.append((address, 1, value))
            else:
                offset = fields(word, [(31, 1), (15, 2), (17, 3), (20, 1),
                                       (21, 10), (14, 1)], True)
                address = (gp + offset) & 0xFFFFFFFF
                value = address if subtype == 1 else cpu.mem_read(address, 1)[0]
                if subtype == 0:
                    value = signed(value, 8) & 0xFFFFFFFF
                destination = (word >> 7) & 31
                if destination:
                    cpu.reg_write(reg(destination), value)
            cpu.reg_write(rv.UC_RISCV_REG_PC, pc + 4)
        elif opcode == 0x5B:
            source = (word >> 15) & 31
            if function in (5, 6):
                offset = fields(word, [(31, 1), (25, 5), (8, 4)], True) << 1
                immediate = fields(word, [(30, 1), (7, 1), (20, 5)])
                equal = cpu.reg_read(reg(source)) == immediate
                taken = equal if function == 5 else not equal
                destination = pc + offset if taken else pc + 4
                cpu.reg_write(rv.UC_RISCV_REG_PC, destination)
            elif function == 0 and (word >> 25) & 127 in (5, 6):
                shift = ((word >> 25) & 127) - 4
                value = cpu.reg_read(reg(source))
                value += cpu.reg_read(reg((word >> 20) & 31)) << shift
                cpu.reg_write(reg((word >> 7) & 31), value & 0xFFFFFFFF)
                cpu.reg_write(rv.UC_RISCV_REG_PC, pc + 4)
            elif function == 2:
                high, low = (word >> 26) & 63, (word >> 20) & 63
                if not 0 <= low <= high < 32:
                    raise AssertionError(f"Invalid vendor bitfield at {pc:#x}")
                value = (cpu.reg_read(reg(source)) >> low) & ((1 << (high-low+1))-1)
                cpu.reg_write(reg((word >> 7) & 31), value)
                cpu.reg_write(rv.UC_RISCV_REG_PC, pc + 4)
            else:
                raise AssertionError(f"Unsupported vendor instruction {word:#x} at {pc:#x}")

    def run(self, start, end):
        self.cpu.emu_start(start, end, count=30000)
        if self.cpu.reg_read(rv.UC_RISCV_REG_PC) != end:
            raise AssertionError("Execution did not reach the normal continuation")

    def request(self, packet):
        self.cpu.mem_write(PACKET, bytes(packet).ljust(32, b"\0"))
        self.cpu.reg_write(reg(9), PACKET)
        self.run(FLASH + (0xDBAC if packet[0] == 7 else 0xDC34), FLASH + 0xD930)
        return bytes(self.cpu.mem_read(PACKET, 32))

    def frame(self, brightness=9, mode=1):
        self.cpu.reg_write(reg(9), STATE)
        self.cpu.reg_write(reg(13), 6)
        self.cpu.mem_write(STATE + 1, bytes([6, brightness, 0, 0, 0, 128, 64, 32]))
        self.cpu.mem_write(STATE + 0x1E, b"\0")
        self.cpu.mem_write(GP + 0x35B, b"\x28")
        self.cpu.mem_write(GP + 0x2B6, b"\xff\xff")
        self.cpu.mem_write(MODE, struct.pack("<I", mode))
        self.cpu.mem_write(FRAMEBUFFER, b"\xa5" * 276)
        self.run(FLASH + 0xA51C, FLASH + 0xA550)
        return bytes(self.cpu.mem_read(FRAMEBUFFER, 276))


def candidate():
    override = os.environ.get("WOBKEY_TEST_IMAGE")
    if override:
        return Path(override).read_bytes()
    from patch_firmware_per_key import build_image
    return build_image(BASE)


class FirmwareTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.image = candidate()

    def test_capabilities_are_distinct_from_stock_echo(self):
        machine = Machine(self.image)
        reply = machine.request([8, 127, 0])
        self.assertEqual(reply[:12], bytes([8, 127, 0, 0]) + b"PKRG" + bytes([1, 92, 8, 0]))

    def test_both_startup_paths_reserve_and_initialize_storage(self):
        for stack_start, stack_end, init_start in [
            (FLASH + 0x40, FLASH + 0x4A, FLASH + 0x172),
            (0x3A, 0x44, 0x140),
        ]:
            with self.subTest(start=stack_start):
                machine = Machine(self.image)
                machine.cpu.mem_write(BUFFER, b"\xa5" * 288)
                machine.run(stack_start, stack_end)
                self.assertEqual(machine.cpu.reg_read(reg(2)), NEW_SP)
                machine.run(init_start, 0x36D4)
                self.assertEqual(bytes(machine.cpu.mem_read(BUFFER, 288)), bytes(288))
                self.assertEqual(machine.cpu.reg_read(reg(3)), GP)
                self.assertEqual(machine.cpu.reg_read(reg(2)), NEW_SP)

    def test_independent_colors_and_brightness_through_actual_renderer(self):
        for brightness, expected in [(0, 0), (1, 20), (4, 83), (9, 191)]:
            with self.subTest(brightness=brightness):
                machine = Machine(self.image)
                machine.cpu.mem_write(BUFFER, bytes([255, 0, 0, 0, 255, 0]) + bytes(270))
                pixels = machine.frame(brightness)
                self.assertEqual(pixels, bytes([expected, 0, 0, 0, expected, 0]) + bytes(270))

    def test_disabled_renderer_preserves_oem_behavior(self):
        old, new = Machine(BASE), Machine(self.image)
        self.assertEqual(new.frame(mode=0), old.frame(mode=0))

    def test_render_splice_preserves_every_live_register(self):
        for mode in (0, 1):
            for index in range(92):
                machine = Machine(self.image)
                for register in range(1, 32):
                    machine.cpu.reg_write(reg(register), 0x13570000 + register * 0x101)
                machine.cpu.reg_write(reg(2), NEW_SP)
                machine.cpu.reg_write(reg(14), FRAMEBUFFER + index * 3)
                machine.cpu.reg_write(reg(28), FRAMEBUFFER)
                machine.cpu.reg_write(reg(31), 192)
                machine.cpu.mem_write(MODE, struct.pack("<I", mode))
                machine.cpu.mem_write(BUFFER + index * 3, bytes([255, 127, 1]))
                before = [machine.cpu.reg_read(reg(r)) for r in range(32)]
                machine.run(FLASH + 0xAA3C, FLASH + 0xAA48)
                after = [machine.cpu.reg_read(reg(r)) for r in range(32)]
                self.assertEqual(after, before, (mode, index))
                allowed = set(range(FRAMEBUFFER + index*3, FRAMEBUFFER + index*3+3))
                allowed.update(range(NEW_SP - 16, NEW_SP))
                self.assertTrue(all(a in allowed for a, _, _ in machine.writes))

    def test_chunks_round_trip_including_last_led(self):
        machine = Machine(self.image)
        for start, count in [(0, 8), (8, 8), (84, 8), (91, 1)]:
            data = bytes((i * 17 + start) & 255 for i in range(count * 3))
            request = bytes([7, 127, 2, 0, start, count]) + data
            self.assertEqual(machine.request(request)[:len(request)], request)
            readback = machine.request([8, 127, 2, 0, start, count])
            self.assertEqual(readback[:6], bytes([8, 127, 2, 0, start, count]))
            self.assertEqual(readback[6:6+len(data)], data)
        self.assertEqual(bytes(machine.cpu.mem_read(MODE, 4)), bytes(4))

    def test_invalid_chunks_do_not_mutate_rgb_or_mode(self):
        for command in (7, 8):
            for start, count in [(0, 0), (0, 9), (91, 2), (92, 1), (255, 255)]:
                with self.subTest(command=command, start=start, count=count):
                    machine = Machine(self.image)
                    initial = b"\x5a" * 288
                    machine.cpu.mem_write(BUFFER, initial)
                    reply = machine.request([command, 127, 2, 0, start, count] + [255] * 24)
                    self.assertEqual(reply[3], 2)
                    self.assertEqual(bytes(machine.cpu.mem_read(BUFFER, 288)), initial)

    def test_mode_is_explicit_and_invalid_modes_are_rejected(self):
        machine = Machine(self.image)
        for value in (1, 0, 1):
            self.assertEqual(machine.request([7, 127, 1, 0, value])[:5], bytes([7,127,1,0,value]))
            self.assertEqual(machine.request([8, 127, 1])[:5], bytes([8,127,1,0,value]))
        reply = machine.request([7, 127, 1, 0, 2])
        self.assertEqual(reply[3], 3)
        self.assertEqual(bytes(machine.cpu.mem_read(MODE, 4)), b"\x01\0\0\0")

    def test_unsupported_operations_cannot_change_state(self):
        for request in ([7,127,0], [7,127,255], [8,127,255]):
            machine = Machine(self.image)
            machine.cpu.mem_write(BUFFER, b"\x5a" * 288)
            self.assertEqual(machine.request(request)[3], 1)
            self.assertEqual(bytes(machine.cpu.mem_read(BUFFER, 288)), b"\x5a" * 288)

    def test_other_channels_resume_original_handlers(self):
        for entry, resume, command in [(0xDBAC, 0xDBB0, 7), (0xDC34, 0xDC38, 8)]:
            machine = Machine(self.image)
            for r in range(1, 32):
                machine.cpu.reg_write(reg(r), 0x100000 + r * 256)
            machine.cpu.reg_write(reg(2), NEW_SP)
            machine.cpu.reg_write(reg(3), GP)
            machine.cpu.reg_write(reg(9), PACKET)
            machine.cpu.mem_write(PACKET, bytes([command, 3, 4, 128, 255]).ljust(32, b"\0"))
            before = [machine.cpu.reg_read(reg(r)) for r in range(32)]
            machine.run(FLASH + entry, FLASH + resume)
            after = [machine.cpu.reg_read(reg(r)) for r in range(32)]
            before[15] = STATE
            self.assertEqual(after, before)
            self.assertEqual(bytes(machine.cpu.mem_read(PACKET, 5)), bytes([command,3,4,128,255]))


if __name__ == "__main__":
    unittest.main()
