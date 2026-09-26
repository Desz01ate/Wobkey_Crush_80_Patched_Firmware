"""Restoration must preserve intentionally disabled/default keys after flashing."""
import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'host/linux'))
from via_backup import cmd_restore


class KeyboardStorage:
    """Stateful replacement for physical USB storage; no timing or hardware I/O."""
    def __init__(self, keys, discard_writes=False):
        self.keys = list(keys)
        self.discard_writes = discard_writes

    def get_buffer(self, offset, size):
        return b''.join(k.to_bytes(2, 'big') for k in self.keys)[offset:offset+size]

    def set_keycode(self, layer, row, column, code):
        if not self.discard_writes:
            self.keys[layer * 128 + row * 16 + column] = code
        return True

    def transact(self, request):
        if request != [9]:
            raise AssertionError(f'Unexpected request: {request}')
        return bytes([9]) + bytes(31)


class RestoreTests(unittest.TestCase):
    def restore(self, keyboard, wanted):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'backup.json'
            path.write_text(json.dumps({'keymap': [wanted]}))
            # Only remove the physical-device pacing delay; storage remains real
            # mutable state, and assertions concern restored key behavior.
            with patch('via_backup.time.sleep'), contextlib.redirect_stdout(io.StringIO()):
                return cmd_restore(keyboard, str(path))

    def test_disabled_and_default_keys_replace_reset_keycodes(self):
        wanted = [4] * 128
        wanted[0], wanted[1], wanted[127] = 0, 0xFFFF, 0x5260
        keyboard = KeyboardStorage([4] * 128)
        self.assertTrue(self.restore(keyboard, wanted))
        self.assertEqual(keyboard.keys, wanted)

    def test_acknowledged_but_discarded_writes_are_not_reported_as_success(self):
        keyboard = KeyboardStorage([4] * 128, discard_writes=True)
        wanted = [4] * 128
        wanted[5] = 0
        self.assertFalse(self.restore(keyboard, wanted))


if __name__ == '__main__':
    unittest.main()
