"""Benchmark behavior against real PKRG machine code; only HID/OS I/O is modeled."""
import sys
import unittest
from pathlib import Path

from test_per_key_firmware import BASE, BUFFER, MODE, Machine, candidate

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'host/windows'))
from benchmark_per_key_rgb import HidTransport, choose_device, run_benchmark
from per_key_rgb import FirmwareError


class NativeDevice:
    def __init__(self, image=None, prefix=False):
        self.machine = Machine(candidate() if image is None else image)
        self.machine.cpu.mem_write(BUFFER, bytes([10, 20, 30]) * 92)
        self.machine.cpu.mem_write(MODE, (1).to_bytes(4, 'little'))
        self.oem = {1: 5, 2: 4}
        self.pending = []
        self.prefix = prefix
        self.clock = 0
        self.lighting_writes = 0
        self.active_frames = []
        self.drop_fragment = False
        self.stale_ack = False
        self.short_write = False
        self.reply_lost_once = False

    def write(self, report):
        if self.short_write:
            self.short_write = False
            return len(report) - 1
        self.clock += 1_000_000
        payload = bytes(report[1:])
        command, channel, operation = payload[:3]
        if command == 7:
            self.lighting_writes += 1
        if channel == 127:
            if self.drop_fragment and operation == 3 and payload[4] == 5:
                self.drop_fragment = False
                return len(report)
            reply = self.machine.request(payload)
            if operation == 4 and reply is None:
                self.machine.boundary()
                reply = self.machine.take_reply()
            if operation == 4 and reply is not None and reply[3] == 0:
                self.active_frames.append(self.machine.colors())
        else:
            if channel != 3 or operation not in (1, 2):
                raise AssertionError('Unexpected configuration command')
            reply = bytearray(payload)
            if command == 7:
                self.oem[operation] = payload[3]
            reply[3] = self.oem[operation]
            reply = bytes(reply)
        if reply is not None:
            if self.stale_ack and operation == 4:
                self.stale_ack = False
                wrong = bytearray(reply)
                wrong[4] ^= 255
                self.pending.append(bytes(wrong))
            self.pending.append(reply)
        return len(report)

    def read(self, length, timeout_ms):
        # cython-hidapi 0.15.0 calls blocking hid_read when timeout_ms <= 0.
        # Fail deterministically instead of hanging the suite on an empty queue.
        if timeout_ms <= 0 and not self.pending:
            raise RuntimeError('Untimed blocking HID read on an empty input queue')
        self.clock += 2_000_000
        if self.reply_lost_once and self.pending and self.pending[0][:3] == bytes([7, 127, 4]):
            self.reply_lost_once = False
            return []
        if not self.pending:
            return []
        reply = self.pending.pop(0)
        return list(b'\0' + reply if self.prefix else reply)

    def snapshot(self):
        return (self.machine.colors(), bytes(self.machine.cpu.mem_read(MODE, 4)), dict(self.oem))


class BenchmarkTests(unittest.TestCase):
    def test_only_the_wired_via_interface_can_be_selected(self):
        via = {'vendor_id': 0x320F, 'product_id': 0x5055, 'usage_page': 0xFF60,
               'usage': 0x61, 'interface_number': 1, 'path': b'via'}
        for field, value in [('vendor_id', 1), ('product_id', 0x5088),
                             ('usage_page', 0xFFEF), ('usage_page', 0xFF1C),
                             ('usage', 0), ('interface_number', 3)]:
            unsafe = {**via, field: value}
            self.assertEqual(choose_device([unsafe, via]), via)
            with self.assertRaises(ValueError):
                choose_device([unsafe])
        with self.assertRaises(ValueError):
            choose_device([via, {**via, 'path': b'second'}])

    def test_stale_input_is_drained_before_capturing_and_restoring_state(self):
        device = NativeDevice()
        before = device.snapshot()
        device.pending = [bytes([7, 3, 1, 9]) + bytes(28),
                          bytes([7, 127, 4, 0, 99]) + bytes(27)]
        result = run_benchmark(HidTransport(device), frames=1, warmup=0,
                               clock=lambda: device.clock)
        self.assertEqual(device.active_frames[0][-3:], bytes([93, 95, 97]))
        self.assertEqual(result['frame_ms']['mean'], 14.0)
        self.assertEqual(device.snapshot(), before)

    def test_measures_transfers_without_readback_time_and_restores_lighting(self):
        for prefix in (False, True):
            device = NativeDevice(prefix=prefix)
            before = device.snapshot()
            result = run_benchmark(HidTransport(device), frames=2, warmup=0,
                                   clock=lambda: device.clock)
            self.assertEqual(device.active_frames[0][:3], bytes([0, 0, 0]))
            self.assertEqual(device.active_frames[0][-3:], bytes([93, 95, 97]))
            self.assertEqual(device.active_frames[1][:3], bytes([1, 1, 1]))
            self.assertEqual(result['write_ms']['mean'], 12.0)
            self.assertEqual(result['ack_ms']['mean'], 2.0)
            self.assertEqual(result['frame_ms']['mean'], 14.0)
            self.assertEqual(device.snapshot(), before)

    def test_zero_frame_id_after_wrap_is_acknowledged_and_restored(self):
        device = NativeDevice()
        before = device.snapshot()
        result = run_benchmark(HidTransport(device), frames=1, warmup=255,
                               clock=lambda: device.clock)
        self.assertEqual(device.active_frames[255][:3], bytes([75, 75, 75]))
        self.assertEqual(result['frame_ms']['mean'], 14.0)
        self.assertEqual(device.snapshot(), before)

    def test_incompatible_firmware_cannot_receive_lighting_changes(self):
        device = NativeDevice(BASE)
        before = device.snapshot()
        with self.assertRaises(FirmwareError):
            run_benchmark(HidTransport(device), frames=1, warmup=0)
        self.assertEqual(device.snapshot(), before)
        self.assertEqual(device.lighting_writes, 0)

    def test_failed_frame_is_reported_and_previous_lighting_is_restored(self):
        for fault in ('drop_fragment', 'stale_ack', 'reply_lost_once'):
            device = NativeDevice()
            before = device.snapshot()
            setattr(device, fault, True)
            with self.assertRaises(FirmwareError):
                run_benchmark(HidTransport(device), frames=2, warmup=0)
            self.assertEqual(device.snapshot(), before, fault)

    def test_partial_hid_write_is_not_accepted_as_a_delivered_report(self):
        device = NativeDevice()
        device.short_write = True
        with self.assertRaises(OSError):
            HidTransport(device).write(bytes([8, 127, 0]))
        self.assertEqual(device.lighting_writes, 0)


if __name__ == '__main__':
    unittest.main()
