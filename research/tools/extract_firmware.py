#!/usr/bin/env python3
"""Extract firmware resources (code_2M, param_128K) from the decompiled .resx file."""

import base64
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
RESX = ROOT / "research/vendor-flasher/WindowsFormsApplication1.Properties.Resources.resx"
OUTPUT = ROOT / "firmware/sources"
OUTPUT.mkdir(parents=True, exist_ok=True)

tree = ET.parse(RESX)
root = tree.getroot()

for data in root.findall("data"):
    name = data.get("name")
    if name in ("code_2M", "param_128K"):
        value_el = data.find("value")
        raw = base64.b64decode(value_el.text)
        out = OUTPUT / f"{name}.bin"
        out.write_bytes(raw)
        print(f"{name}: {len(raw)} bytes -> {out}")
