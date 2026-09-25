"""The real host client talks to the actual patched machine code in these tests."""
import unittest

from test_per_key_firmware import BASE, BUFFER, MODE, Machine, candidate
from per_key_rgb import FirmwareError, PerKeyRGB, parse_assignment, parse_color, validate_frame


class EmulatedHID:
    def __init__(self, image):
        self.machine = Machine(image)

    def transact(self, request, timeout=2.0):
        return self.machine.request(request)


class HostTests(unittest.TestCase):
    def test_stock_firmware_cannot_be_mistaken_for_custom_support(self):
        with self.assertRaises(FirmwareError):
            PerKeyRGB(EmulatedHID(BASE))

    def test_full_frame_upload_and_readback_cross_packet_boundaries(self):
        hid = EmulatedHID(candidate())
        client = PerKeyRGB(hid)
        colors = [(i, 255-i, (3*i) & 255) for i in range(92)]
        client.write(0, colors)
        self.assertEqual(client.read(0, 92), colors)
        self.assertEqual(bytes(hid.machine.cpu.mem_read(BUFFER, 276)),
                         bytes(channel for rgb in colors for channel in rgb))

    def test_rejected_input_does_not_partially_update_a_frame(self):
        hid = EmulatedHID(candidate())
        client = PerKeyRGB(hid)
        client.write(0, [(10,20,30)] * 92)
        for colors in ([(1,2,3)] * 8 + [(256,0,0)], [(1,2,3)] * 93):
            with self.assertRaises(ValueError):
                client.write(0, colors)
            self.assertEqual(client.read(0, 92), [(10,20,30)] * 92)
        with self.assertRaises(ValueError):
            client.write(91, [(1,2,3), (4,5,6)])
        self.assertEqual(client.read(91, 1), [(10,20,30)])

    def test_mode_round_trip_preserves_supplied_colors(self):
        hid = EmulatedHID(candidate())
        client = PerKeyRGB(hid)
        client.write(0, [(255,0,0), (0,255,0)])
        client.set_enabled(True)
        self.assertTrue(client.info()['enabled'])
        client.set_enabled(False)
        self.assertFalse(client.info()['enabled'])
        self.assertEqual(client.read(0, 2), [(255,0,0), (0,255,0)])
        self.assertEqual(bytes(hid.machine.cpu.mem_read(MODE, 4)), bytes(4))

    def test_color_and_assignment_boundaries(self):
        self.assertEqual(parse_color('#00FF80'), (0,255,128))
        self.assertEqual(parse_assignment('91=ff0000'), (91, (255,0,0)))
        for value in ('12345', 'GG0000', '1234567'):
            with self.assertRaises(ValueError):
                parse_color(value)
        for value in ('-1=ff0000', '92=ff0000', '0=red'):
            with self.assertRaises(ValueError):
                parse_assignment(value)
        for colors in ([], [[0,0,0]] * 91, [[0,0,0]] * 93,
                       [[0,0,0]] * 91 + [[0,False,0]]):
            with self.assertRaises(ValueError):
                validate_frame(colors)


if __name__ == '__main__':
    unittest.main()
