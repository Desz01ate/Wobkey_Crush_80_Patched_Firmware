import json
import unittest
from pathlib import Path

from test_per_key_firmware import Machine, candidate

ROOT = Path(__file__).resolve().parents[1]
CORPUS = ROOT / "sdk/conformance/pkrg-v1.json"


class SdkConformanceTests(unittest.TestCase):
    def test_protocol_vectors_execute_against_patched_machine_code(self):
        corpus = json.loads(CORPUS.read_text())
        self.assertEqual(corpus["schemaVersion"], 1)
        self.assertEqual(corpus["protocol"], "PKRG")
        self.assertEqual(corpus["protocolVersion"], 1)

        for case in corpus["protocolCases"]:
            with self.subTest(case=case["name"]):
                reply = Machine(candidate()).request(case["request"])
                self.assertEqual(list(reply[:len(case["expectedPrefix"])]),
                                 case["expectedPrefix"])
                if "expectedStatus" in case:
                    self.assertEqual(reply[3], case["expectedStatus"])
