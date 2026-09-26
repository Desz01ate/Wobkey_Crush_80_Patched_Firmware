#!/usr/bin/env python3
"""Measure PKRG v2 frame transfers through native Windows HIDAPI, outside SignalRGB.

Requires Python 3 and `hidapi==0.15.0` (import name: hid). Close SignalRGB,
VIA, and other keyboard-control software first. Lighting changes temporarily;
restoration is attempted on errors/Ctrl+C but cannot be guaranteed after
unplugging or forcibly terminating the process. No SAVE, keymap, or OTA commands.
"""
import argparse
import importlib.metadata
import math
import platform
import statistics
import sys
import time
from pathlib import Path

# Reuse the existing platform-independent protocol client. Its Linux discovery
# and CLI entry point are not invoked; this adapter supplies the Windows I/O.
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'host/linux'))
from per_key_rgb import CHANNEL, LED_COUNT, STREAM_LEDS, STREAM_CHUNKS, FirmwareError, PerKeyRGB

TIMEOUT = 0.1


def choose_device(devices):
    matches = [device for device in devices
               if device.get('vendor_id') == 0x320F
               and device.get('product_id') == 0x5055
               and device.get('usage_page') == 0xFF60
               and device.get('usage') == 0x61
               and device.get('interface_number') == 1]
    if len(matches) != 1:
        raise ValueError(f'Expected one wired Crush 80 VIA interface; found {len(matches)}. '
                         'Use wired USB, disconnect extra Crush 80 keyboards, and close other controllers.')
    return matches[0]


class HidTransport:
    def __init__(self, device):
        self.device = device
        self.pending_frame = None

    def write(self, payload):
        if self.pending_frame is not None:
            raise FirmwareError('Previous frame acknowledgement is still outstanding')
        payload = bytes(payload)
        if len(payload) > 32:
            raise ValueError('VIA payload exceeds 32 bytes')
        report = b'\0' + payload.ljust(32, b'\0')
        written = self.device.write(report)
        if written != len(report):
            raise OSError(f'Incomplete HID write: {written} of {len(report)} bytes')
        if payload[:3] == bytes([7, CHANNEL, 4]):
            self.pending_frame = payload[4]

    def read(self, timeout=TIMEOUT):
        # cython-hidapi maps zero to hid_read(), which blocks in this mode.
        # A positive timeout keeps drains bounded without changing ACK timing.
        raw = bytes(self.device.read(33, max(1, math.ceil(timeout * 1000))))
        if not raw:
            return None
        if len(raw) == 33 and raw[0] == 0:
            raw = raw[1:]
        if len(raw) != 32:
            raise FirmwareError(f'Unexpected HID reply length: {len(raw)}')
        if (self.pending_frame is not None and raw[:3] == bytes([7, CHANNEL, 4])
                and raw[4] == self.pending_frame):
            self.pending_frame = None
        return raw

    def transact(self, payload, timeout=TIMEOUT):
        self.write(payload)
        return self.read(timeout)

    def clear(self):
        for _ in range(32):
            if self.read(0) is None:
                return
        raise FirmwareError('Input queue did not empty; close other keyboard-control software')

    def finish_pending(self):
        deadline = time.perf_counter() + TIMEOUT
        while self.pending_frame is not None:
            remaining = deadline - time.perf_counter()
            if remaining <= 0 or self.read(remaining) is None:
                raise FirmwareError('Frame ACK still unavailable; restoration cannot safely proceed')
        self.clear()


def get_oem(hid, setting):
    reply = hid.transact(bytes([8, 3, setting]))
    if reply is None or reply[:3] != bytes([8, 3, setting]):
        raise FirmwareError('Could not read OEM lighting state')
    if reply[3] > (9 if setting == 1 else 18):
        raise FirmwareError('Invalid OEM lighting value')
    return reply[3]


def set_oem(hid, setting, value):
    request = bytes([7, 3, setting, value])
    reply = hid.transact(request)
    if reply is None or reply[:4] != request:
        raise FirmwareError('OEM lighting update was not acknowledged')


def summarize(values):
    ordered = sorted(values)
    return {'mean': statistics.fmean(values), 'median': statistics.median(values),
            'p95': ordered[math.ceil(len(ordered) * 0.95) - 1], 'max': ordered[-1]}


def run_benchmark(hid, frames=120, warmup=10, clock=time.perf_counter_ns):
    if not 1 <= frames <= 10000 or not 0 <= warmup <= 1000:
        raise ValueError('Frames must be 1..10000; warmup must be 0..1000')
    hid.clear()
    client = PerKeyRGB(hid)  # Read-only protocol check before any lighting SET.
    saved = {'enabled': client.info()['enabled'], 'colors': client.read(),
             'brightness': get_oem(hid, 1), 'effect': get_oem(hid, 2)}
    writes, acknowledgements, totals = [], [], []
    readbacks = 0
    try:
        client.set_enabled(False)
        set_oem(hid, 1, 9)
        set_oem(hid, 2, 6)
        client.set_enabled(True)
        for frame_number in range(warmup + frames):
            sequence = (frame_number + 1) & 255
            colors = [((i*3 + frame_number) % 180, (i*5 + frame_number) % 180,
                       (i*7 + frame_number) % 180) for i in range(LED_COUNT)]
            packets = []
            for index in range(STREAM_CHUNKS):
                chunk = colors[index*STREAM_LEDS:(index+1)*STREAM_LEDS]
                packets.append(bytes([7, CHANNEL, 3, sequence, index])
                               + bytes(component for rgb in chunk for component in rgb))
            commit = bytes([7, CHANNEL, 4, 0, sequence])
            # Packet generation and readback are outside the timed region.
            # Writes remain synchronous: exactly the plugin's 12 OUT reports.
            started = clock()
            for packet in packets:
                hid.write(packet)
            hid.write(commit)
            written = clock()
            reply = hid.read()
            finished = clock()
            if reply is None:
                raise FirmwareError(f'Frame {sequence} acknowledgement timed out')
            if reply[:3] != commit[:3] or reply[4] != sequence:
                raise FirmwareError(f'Frame {sequence} received an unrelated acknowledgement')
            if reply[3] != 0:
                raise FirmwareError(f'Frame {sequence} rejected with status {reply[3]}')
            if frame_number >= warmup:
                writes.append((written - started) / 1_000_000)
                acknowledgements.append((finished - written) / 1_000_000)
                totals.append((finished - started) / 1_000_000)
            if ((frame_number + 1) % 30 == 0 or frame_number == warmup + frames - 1):
                if client.read() != colors:
                    raise FirmwareError('Committed RGB readback differs from the submitted frame')
                readbacks += 1
        hid.clear()
        result = {'frames': frames, 'warmup': warmup, 'readback_checks': readbacks,
                  'write_ms': summarize(writes), 'ack_ms': summarize(acknowledgements),
                  'frame_ms': summarize(totals)}
    finally:
        original_error = sys.exc_info()[1]
        try:
            hid.finish_pending()
            client.set_enabled(False)
            client.write(0, saved['colors'])
            set_oem(hid, 1, saved['brightness'])
            set_oem(hid, 2, saved['effect'])
            client.set_enabled(saved['enabled'])
            if (client.read() != saved['colors'] or client.info()['enabled'] != saved['enabled']
                    or get_oem(hid, 1) != saved['brightness'] or get_oem(hid, 2) != saved['effect']):
                raise FirmwareError('Restored lighting readback differs from the captured state')
        except Exception as restore_error:
            cause = f'Benchmark failed: {original_error}. ' if original_error else ''
            raise FirmwareError(cause + f'Lighting restoration failed: {restore_error}') from restore_error
    result['restored'] = True
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--frames', type=int, default=120, help='Measured frames (default: 120)')
    parser.add_argument('--warmup', type=int, default=10, help='Untimed warmup frames (default: 10)')
    args = parser.parse_args()
    if sys.platform != 'win32':
        parser.error('Run this diagnostic on Windows to isolate the Windows HID path')
    if not 1 <= args.frames <= 10000 or not 0 <= args.warmup <= 1000:
        parser.error('Frames must be 1..10000; warmup must be 0..1000')
    try:
        import hid
    except ImportError:
        print('Install the binding in this Python environment: python -m pip install hidapi==0.15.0', file=sys.stderr)
        return 1

    device = None
    try:
        endpoint = choose_device(hid.enumerate(0x320F, 0x5055))
        device = hid.device()
        device.open_path(endpoint['path'])
        device.set_nonblocking(0)
        print(f'Platform: {platform.platform()} / Python {platform.python_version()}')
        print(f'HIDAPI Python binding: {importlib.metadata.version("hidapi")}')
        print('Device: 320F:5055, interface 1, usage FF60:0061')
        print('SignalRGB and VIA must be closed. Lighting will change temporarily.')
        print(f'Measuring {args.frames} full frames after {args.warmup} warmup frames...')
        result = run_benchmark(HidTransport(device), args.frames, args.warmup)
        print(f'\nFrames: {result["frames"]}; 12 OUT reports + 1 ACK per frame')
        print(f'Full RGB readback checks: {result["readback_checks"]} (outside timed regions)')
        print(f'{"Phase (ms)":<16}{"Mean":>10}{"Median":>10}{"P95":>10}{"Max":>10}')
        for label, key in [('Write', 'write_ms'), ('ACK', 'ack_ms'), ('Total transfer', 'frame_ms')]:
            row = result[key]
            print(f'{label:<16}' + ''.join(f'{row[field]:>10.3f}' for field in ('mean','median','p95','max')))
        print('Lighting restoration: verified (RGB, enable state, brightness, effect).')
        print('No artificial frame delay. These are transfer timings, not SignalRGB FPS or LED refresh.')
        return 0
    except KeyboardInterrupt:
        print('Interrupted; the benchmark attempted lighting restoration.', file=sys.stderr)
        return 130
    except (OSError, ValueError, FirmwareError) as error:
        print(f'Error: {error}', file=sys.stderr)
        return 1
    finally:
        if device is not None:
            device.close()


if __name__ == '__main__':
    sys.exit(main())
