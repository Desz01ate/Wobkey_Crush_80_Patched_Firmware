import json
import os
from pathlib import Path
import unittest

from test_per_key_firmware import Machine, candidate

ROOT = Path(__file__).resolve().parents[1]
CORPUS = ROOT / "sdk/conformance/pkrg-v1.json"


class SdkConformanceTests(unittest.TestCase):
    def _assert_protocol_cases(self, image):
        corpus = json.loads(CORPUS.read_text())
        for case in corpus["protocolCases"]:
            with self.subTest(case=case["name"]):
                reply = Machine(image).request(case["request"])
                self.assertIsNotNone(reply)
                assert reply is not None
                expected_prefixes = case.get("expectedPrefixes")
                if expected_prefixes is None:
                    if case.get("name") == "capabilities-disabled" and "compatibleCapabilityResponses" in corpus:
                        expected_prefixes = [
                            item["responsePrefix"]
                            for item in corpus["compatibleCapabilityResponses"]
                            if "responsePrefix" in item
                        ]
                    elif "expectedPrefix" in case:
                        expected_prefixes = [case["expectedPrefix"]]

                if expected_prefixes:
                    self.assertTrue(
                        any(list(reply[:len(p)]) == p for p in expected_prefixes),
                        f"Reply {list(reply)} did not match any expected prefix in {expected_prefixes}",
                    )
                if "expectedStatus" in case:
                    self.assertEqual(reply[3], case["expectedStatus"])

    def test_protocol_vectors_execute_against_patched_machine_code(self):
        corpus = json.loads(CORPUS.read_text())
        self.assertEqual(corpus["schemaVersion"], 1)
        self.assertEqual(corpus["protocol"], "PKRG")
        self.assertEqual(corpus["protocolVersion"], 1)
        self.assertEqual(corpus["ledCount"], 92)
        self.assertEqual(corpus["chunkLimit"], 8)

        self._assert_protocol_cases(candidate())

    def test_protocol_vectors_execute_against_v1_firmware_release(self):
        if os.environ.get("WOBKEY_TEST_IMAGE"):
            self.skipTest("WOBKEY_TEST_IMAGE override active")
        v1_release = ROOT / "firmware/releases/v1.06-per-key/firmware_per_key_v2.bin"
        if not v1_release.exists():
            self.skipTest("v1.06-per-key/firmware_per_key_v2.bin artifact not present")
        self._assert_protocol_cases(v1_release.read_bytes())

