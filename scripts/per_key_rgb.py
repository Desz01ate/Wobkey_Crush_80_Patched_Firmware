#!/usr/bin/env python3
"""Control wired Crush 80 per-key firmware (VIA vendor channel 0x7F).

Colors are six-digit RGB hex. LED indices are 0..91; Esc=0 and F1=1 are
hardware-confirmed. This tool never flashes firmware or saves RGB to EEPROM.
"""
import argparse
import json
import sys
from pathlib import Path

from via_backup import ViaHID, find_via_device

CHANNEL = 0x7F
LED_COUNT = 92
CHUNK_LEDS = 8


class FirmwareError(RuntimeError):
    pass


def parse_color(value):
    text = value.removeprefix('#')
    if len(text) != 6 or any(c not in '0123456789abcdefABCDEF' for c in text):
        raise ValueError('Color must be six RGB hex digits, for example ff0080')
    return tuple(int(text[i:i+2], 16) for i in (0, 2, 4))


def parse_assignment(value):
    try:
        index, color = value.split('=', 1)
        index = int(index)
    except ValueError as exc:
        raise ValueError('Use LED=RRGGBB, for example 0=ff0000') from exc
    if not 0 <= index < LED_COUNT:
        raise ValueError('LED index must be 0..91')
    return index, parse_color(color)


def validate_colors(colors):
    result = list(colors)
    for color in result:
        if (not isinstance(color, (list, tuple)) or len(color) != 3
                or any(type(c) is not int or not 0 <= c <= 255 for c in color)):
            raise ValueError('Each RGB value must contain three integers in 0..255')
    return [tuple(color) for color in result]


def validate_frame(colors):
    result = validate_colors(colors)
    if len(result) != LED_COUNT:
        raise ValueError('A frame must contain exactly 92 RGB triplets')
    return result


def validate_range(start, count):
    if (type(start) is not int or type(count) is not int or count < 1
            or start < 0 or start + count > LED_COUNT):
        raise ValueError('LED range must fit entirely within indices 0..91')


class PerKeyRGB:
    def __init__(self, hid):
        self.hid = hid
        self.info()  # Reject stock/old firmware before issuing any custom SET.

    def _request(self, command, operation, payload=()):
        request = bytes([command, CHANNEL, operation, 0, *payload])
        if len(request) > 32:
            raise ValueError('RGB request exceeds one VIA report')
        response = self.hid.transact(request)
        if response is None:
            raise FirmwareError('Timed out waiting for the keyboard')
        if len(response) != 32 or response[:3] != request[:3]:
            raise FirmwareError('Unexpected VIA response; stop other keyboard-control software')
        if response[3]:
            reason = {1: 'unsupported operation', 2: 'invalid LED range',
                      3: 'invalid mode'}.get(response[3], 'unknown firmware error')
            raise FirmwareError(f'{reason} (status {response[3]})')
        return response

    def info(self):
        reply = self._request(8, 0)
        if reply[4:11] != b'PKRG' + bytes([1, LED_COUNT, CHUNK_LEDS]):
            raise FirmwareError('Compatible per-key firmware not detected; no RGB changes sent')
        if reply[11] not in (0, 1):
            raise FirmwareError('Firmware returned an invalid enable flag')
        return {'protocol': 1, 'led_count': LED_COUNT, 'chunk_leds': CHUNK_LEDS,
                'enabled': bool(reply[11])}

    def set_enabled(self, enabled):
        if type(enabled) is not bool:
            raise ValueError('Enable state must be a boolean')
        reply = self._request(7, 1, [int(enabled)])
        if reply[4] != int(enabled):
            raise FirmwareError('Mode acknowledgement did not match the request')
        actual = self._request(8, 1)[4]
        if actual != int(enabled):
            raise FirmwareError('Mode readback did not match the request')

    def read(self, start=0, count=LED_COUNT):
        validate_range(start, count)
        colors = []
        for offset in range(0, count, CHUNK_LEDS):
            size = min(CHUNK_LEDS, count - offset)
            reply = self._request(8, 2, [start + offset, size])
            if reply[4:6] != bytes([start + offset, size]):
                raise FirmwareError('RGB readback returned a different LED range')
            colors.extend(tuple(reply[i:i+3]) for i in range(6, 6+3*size, 3))
        return colors

    def write(self, start, colors):
        # Validate the entire operation before the first write: no partial
        # frame caused by discovering an invalid color in a later chunk.
        colors = validate_colors(colors)
        validate_range(start, len(colors))
        for offset in range(0, len(colors), CHUNK_LEDS):
            chunk = colors[offset:offset+CHUNK_LEDS]
            data = bytes(c for rgb in chunk for c in rgb)
            reply = self._request(7, 2, [start + offset, len(chunk), *data])
            if reply[4:6] != bytes([start + offset, len(chunk)]) or reply[6:6+len(data)] != data:
                raise FirmwareError('RGB acknowledgement did not match the write')
        if self.read(start, len(colors)) != colors:
            raise FirmwareError('RGB buffer readback did not match the supplied colors')

    def show(self):
        self.set_enabled(True)
        response = self.hid.transact([7, 3, 2, 6])
        if response is None or response[:4] != bytes([7, 3, 2, 6]):
            raise FirmwareError('Could not select effect 6')
        response = self.hid.transact([8, 3, 2])
        if response is None or response[:4] != bytes([8, 3, 2, 6]):
            raise FirmwareError('Effect 6 readback failed')


def verify_via_device(path):
    node = Path(path).resolve()
    if not node.name.startswith('hidraw') or not node.is_char_device():
        raise ValueError('Device must be a hidraw character device')
    sysdev = Path('/sys/class/hidraw') / node.name / 'device'
    identity = (sysdev / 'uevent').read_text()
    descriptor = (sysdev / 'report_descriptor').read_bytes()
    if ('HID_ID=0003:0000320F:00005055' not in identity
            or b'\x06\x60\xff' not in descriptor
            or b'\x06\x1c\xff' in descriptor or b'\x06\xef\xff' in descriptor):
        raise ValueError('Refusing a device that is not the wired Crush 80 VIA interface')
    return str(node)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--device', help='Verified VIA hidraw device; auto-detected by default')
    commands = parser.add_subparsers(dest='command', required=True)
    for command in ('info', 'enable', 'disable'):
        commands.add_parser(command)
    fill = commands.add_parser('fill', help='Set all 92 LEDs, verify, and select per-key mode')
    fill.add_argument('color', type=parse_color)
    single = commands.add_parser('set', help='Update selected LEDs, retaining the other colors')
    single.add_argument('assignments', nargs='+', type=parse_assignment, metavar='LED=RRGGBB')
    readback = commands.add_parser('read', help='Read supplied RGB values, before brightness scaling')
    readback.add_argument('start', type=int, nargs='?', default=0)
    readback.add_argument('count', type=int, nargs='?', default=LED_COUNT)
    frame = commands.add_parser('frame', help='Upload a JSON array of 92 [R,G,B] triplets')
    frame.add_argument('file', type=Path)
    args = parser.parse_args()
    hid = None
    try:
        frame_colors = None
        if args.command == 'frame':
            frame_colors = validate_frame(json.loads(args.file.read_text()))
        elif args.command == 'read':
            validate_range(args.start, args.count)
        path = args.device or find_via_device()
        if path is None:
            raise FirmwareError('No wired Crush 80 VIA interface found')
        hid = ViaHID(verify_via_device(path))
        client = PerKeyRGB(hid)
        if args.command == 'info':
            print(json.dumps(client.info(), indent=2))
        elif args.command == 'read':
            print(json.dumps(client.read(args.start, args.count)))
        elif args.command == 'disable':
            client.set_enabled(False)
            print('Per-key override disabled; current OEM effect retained.')
        else:
            if args.command == 'fill':
                client.write(0, [args.color] * LED_COUNT)
            elif args.command == 'set':
                for index, color in args.assignments:
                    client.write(index, [color])
            elif args.command == 'frame':
                client.write(0, frame_colors)
            client.show()
            print('Per-key override enabled; effect 6 selected.')
            if args.command != 'enable':
                print('Supplied RGB values verified by readback.')
        return 0
    except (OSError, ValueError, TypeError, FirmwareError) as exc:
        print(f'Error: {exc}', file=sys.stderr)
        return 1
    finally:
        if hid is not None:
            hid.close()


if __name__ == '__main__':
    sys.exit(main())
