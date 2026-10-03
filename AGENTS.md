# AGENTS.md — Agent & Contributor Operating Guide

Welcome to the **Wobkey Crush 80 Patched Firmware & RGB Ecosystem** repository (`Desz01ate/Wobkey_Crush_80_Patched_Firmware`). This guide provides technical architecture, safety constraints, toolchains, build recipes, and test workflows for autonomous agents and human developers.

---

## 1. Project Overview & Architecture

The **Wobkey Crush 80** (USB VID `0x320F`, PID `0x5055`) is an 80% custom mechanical keyboard powered by a **Telink TLSR RISC-V microcontroller** (RV32IC core with Andes V5 ISA extensions).

### Core Problem Solved
- **Stock Firmware Hue Bug**: The stock VIA SET color handler at firmware address `0xDA20` stores the incoming hue (`H`) byte into internal state but overwrites RGB color registers with stale RAM values instead of performing an HSV-to-RGB conversion. Any color set via VIA or lighting suites (like SignalRGB) is ignored.
- **Per-Key RGB Extension**: Stock firmware only natively supports whole-board lighting presets. This repository extends the firmware with binary code cave injections that hook into Effect 6 (`LIGHT_MODE`) rendering at `0xAA3C..0xAA47` to enable true host-controlled per-key RGB across all 92 matrix LEDs.

### Protocol Generations
- **Hue Patch (v1.04 / v1.06)**: Patches the VIA HSV conversion cave. Works with whole-board SignalRGB V1/V2 plugins.
- **PKRG v1 (Per-Key v1.06)**: Introduces the `PKRG` VIA extension. 92 LEDs, transfers up to 8 LEDs per chunk (VIA command 8 / sub-op 2), sequential acknowledged transfers.
- **PKRG v2 (Optimized Per-Key)**: Extends capabilities to advertise frame streaming metadata (9 LEDs × 11 fragments) and silent streaming commit / acknowledgement. Used by SignalRGB V3 wired plugin and .NET SDK.

---

## 2. Hardware Specifications & Critical Safety Rules

> ⚠️ **CRITICAL HARDWARE INTEGRITY DIRECTIVES**
>
> 1. **NEVER WRITE TO INTERFACE 3**:
>    - Interface 0 (`0x0001`): Standard keyboard HID.
>    - Interface 1 (`0xFF60`, Usage `0x61`, 32-byte reports): VIA protocol (RGB control & lighting).
>    - Interface 2 (`0xFFEF`, Report ID `5`): Telink OTA firmware flasher.
>    - Interface 3 (`0xFF1C`): Wireless mode switch — **NEVER WRITE TO THIS INTERFACE**. Writing to interface 3 can disrupt hardware wireless routing.
> 2. **EXCLUSIVE HID ACCESS**:
>    - Always terminate SignalRGB, VIA, and other host tools before opening HID sessions. Simultaneous writers invalidate state capture and corrupt readback verification.
> 3. **VOLATILITY OF RGB STATE**:
>    - The per-key RGB buffer and override flags live in MCU RAM; they reset to OEM defaults on keyboard disconnect, reset, or reboot.
> 4. **SAFE LEASE & RESTORATION PATTERN**:
>    - When capturing control via SDK (`AcquireControlAsync`) or host scripts, always snapshot initial mode, effect, brightness, and RGB colors before applying custom frames, and restore them upon release or disposal.
> 5. **NEVER RUN HARDWARE-WRITING TESTS AUTOMATICALLY**:
>    - Do NOT execute `--smoke` or interactive hardware-writing scripts in CI, automated loops, or without an authorized user present.
> 6. **FIRMWARE RECOVERY SAFETY**:
>    - Retain stock binaries (`firmware/sources/firmware.bin` and `firmware/sources/code_2M.bin`) for recovery. Recovery relies on the Telink OTA bootloader remaining reachable.

---

## 3. Repository Directory Structure

```text
Wobkey_Crush_80_Patched_Firmware/
├── .github/
│   └── workflows/
│       ├── installer-tag-artifact.yml   # Windows installer build on release tags
│       └── sdk-dotnet.yml               # Multi-OS .NET SDK CI and Python tests
├── docs/
│   ├── user/
│   │   └── PER-KEY-RGB.md              # Hardware findings, benchmark data, wire protocol
│   └── superpowers/                     # Architecture designs, specs, and migration plans
├── emulator/                            # Hardware-free .NET SDK visualizer & transport
│   ├── app/                             # Wobkey.Crush80.Emulator.App (interactive demo)
│   ├── client/                          # Wobkey.Crush80.Emulator.Client.Sample (TCP sample)
│   ├── server/                          # Wobkey.Crush80.Emulator.Server (TCP + Web server)
│   ├── src/                             # Wobkey.Crush80.Emulator core library
│   └── tests/                           # Wobkey.Crush80.Emulator.Tests (.NET tests)
├── firmware/
│   ├── releases/                        # Tested patched firmware and OTA images (v1.04, v1.06, v1.06-per-key)
│   ├── sources/                         # Extracted stock images and OTA parameters
│   ├── tools/patching/                  # Python patch builders (GPL-2.0-only)
│   ├── vendor/                          # Original vendor Windows updater executables
│   └── GPL-COMPLIANCE.md                # Licensing analysis and source provenance
├── hardware/
│   ├── layouts/                         # VIA definitions and reference keymaps
│   └── udev/                            # Linux udev rules (99-wobkey-crush80.rules)
├── host/
│   ├── linux/                           # Linux Python utilities (flash_ota.py, per_key_rgb.py, via_backup.py)
│   └── windows/
│       ├── Crush80FirmwareInstaller/    # WPF .NET 10 firmware flasher application
│       └── benchmark_per_key_rgb.py     # Native Windows HIDAPI benchmark tool
├── plugins/
│   ├── signalrgb/
│   │   ├── wired/                       # SignalRGB wired plugins (V1, V2, V3)
│   │   └── wireless/                    # SignalRGB 2.4 GHz dongle plugins (V1, V2, V3)
│   └── tools/
│       └── via-test.html                # WebHID in-browser VIA test utility
├── research/
│   ├── docs/                            # Reverse engineering reports and technical analysis
│   ├── notes/                           # Version comparisons and firmware analysis notes
│   ├── tools/                           # Static analysis, disassembly, and extraction scripts
│   └── vendor-flasher/                  # Decompiled vendor flasher resources
├── sdk/
│   ├── conformance/
│   │   └── pkrg-v1.json                 # PKRG v1 protocol test vectors
│   └── dotnet/
│       ├── adapters/                    # Wobkey.Crush80.Adapter (37x12 sparse grid API)
│       ├── licenses/                    # HidSharp and third-party notices
│       ├── samples/                     # Wobkey.Crush80.Sample (CLI test & smoke tool)
│       ├── src/                         # Wobkey.Crush80.Sdk core library (Apache-2.0)
│       └── tests/                       # Wobkey.Crush80.Sdk.Tests (.NET 10 unit tests)
└── tests/                               # Offline test runner suites (Python, Node.js)
```

---

## 4. Toolchains & Environment Setup

### Prerequisites
- **Python 3.10+**: Host scripts, patch builders, and CPU emulator tests.
  - Requires `unicorn==2.1.4` for assembly tests. Run via `uv run --with "unicorn==2.1.4" python ...` or inside a dedicated virtual environment.
- **.NET 10 SDK**: Required for SDK, Adapter, Emulator, and Windows Installer solutions.
- **Node.js (v20+)**: Required for running the SignalRGB V3 plugin test runner.
- **Linux udev**: For accessing HID devices (`/dev/hidraw*`) without root:
  ```sh
  sudo cp hardware/udev/99-wobkey-crush80.rules /etc/udev/rules.d/
  sudo udevadm control --reload-rules && sudo udevadm trigger
  ```

---

## 5. Build & Generation Workflows

### Firmware Patching
Generate patched firmware binaries using the reproducible Python patch tools:
```sh
# Generate standard v1.04 hue-patched firmware:
python3 firmware/tools/patching/patch_firmware.py

# Generate v1.06 per-key RGB firmware (PKRG v2):
python3 firmware/tools/patching/patch_firmware_per_key.py
```
Outputs are verified by CRC and written into `firmware/releases/`.

### .NET 10 SDK & Libraries
```sh
# Restore dependencies:
dotnet restore sdk/dotnet/Wobkey.Crush80.Sdk.sln

# Build entire SDK solution:
dotnet build sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release

# Pack NuGet package:
dotnet pack sdk/dotnet/src/Wobkey.Crush80.Sdk/Wobkey.Crush80.Sdk.csproj -c Release
```

### SDK Emulator
Run the hardware-free visualizer emulator:
```sh
# Launch standalone server (Browser UI on http://127.0.0.1:5080, TCP on port 5081):
dotnet run --project emulator/server/Wobkey.Crush80.Emulator.Server -- --open

# In another terminal, run client sample against emulator:
dotnet run --project emulator/client/Wobkey.Crush80.Emulator.Client.Sample -- --seconds 5
```

### Windows Flasher Installer
```sh
dotnet publish host/windows/Crush80FirmwareInstaller/Crush80FirmwareInstaller.csproj \
  -c Release -r win-x64 --self-contained true
```

---

## 6. Testing & Offline Verification

All code changes must be verified offline before submission. **None of the following offline commands touch physical USB hardware.**

### 1. Python Unit Tests (with Unicorn CPU Emulator)
The test suite validates RISC-V and Andes V5 instructions executed by the firmware patches inside Unicorn:
```sh
# Run firmware patch & cave execution tests:
uv run --with "unicorn==2.1.4" python -m unittest discover -s tests -p 'test_per_key_firmware.py' -v

# Run host packet framing & chunking tests:
uv run --with "unicorn==2.1.4" python -m unittest discover -s tests -p 'test_per_key_host.py' -v

# Run host-side benchmark simulation tests:
uv run --with "unicorn==2.1.4" python -m unittest discover -s tests -p 'test_per_key_benchmark.py' -v

# Run VIA restore behavior tests:
uv run --with "unicorn==2.1.4" python -m unittest discover -s tests -p 'test_via_restore.py' -v

# Run PKRG v1 protocol conformance test (requires PKRG v1 image):
WOBKEY_TEST_IMAGE=firmware/releases/v1.06-per-key/firmware_per_key_v2.bin \
  uv run --with "unicorn==2.1.4" python -m unittest discover -s tests -p 'test_sdk_conformance.py' -v
```

### 2. SignalRGB JavaScript Plugin Tests
Runs mock HID tests against `WobkeyCrush80_v3.js` using Node.js:
```sh
node --experimental-vm-modules --test tests/test_signalrgb_v3.mjs
```

### 3. .NET Unit Tests
```sh
# Test .NET SDK and Adapter:
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release

# Test Emulator:
dotnet test emulator/Wobkey.Crush80.Emulator.sln -c Release
```

---

## 7. Development Guidelines for Agents

### Firmware & RISC-V Assembly Caves
- **Register Preservation**: Mid-function caves must preserve **all** live registers (including scratch registers `t0`, `t1`, etc., if they remain live across loop iterations).
- **Instruction Boundaries**: Respect 16-bit RV32IC compressed instructions (`c.j`, `c.li`, etc.) and ensure branch offsets remain within range.
- **Andes ISA Compatibility**: The core uses Andes V5 extensions; refer to Andes ISA decoder specifications when modifying opcode patterns.

### .NET SDK & Adapter Coding Standards
- Maintain `#nullable enable` across all C# sources.
- Adhere to the lease model (`AcquireControlAsync` returning `IAsyncDisposable`).
- Respect transport ownership: passing an `IHidTransport` to `Crush80RgbSession.OpenAsync` transfers lifecycle ownership to the session.
- Keep the sparse grid mapping (`37 × 12`) aligned with the physical layout verified in `docs/user/PER-KEY-RGB.md`.

### Host Scripts & SignalRGB Plugins
- Keep Linux host utilities dependency-free (standard library only) except for optional hardware access modules (`hidapi` / `pyudev`).
- SignalRGB plugins must handle packet sequence wrapping safely and drain pending frames before shutdown restoration.

---

## 8. Git & Pull Request Guidelines

1. **Branching**:
   - Always create a dedicated branch off `main`:
     - `feat/<feature-name>`
     - `fix/<issue-description>`
     - `chore/<task-name>`
2. **Commit Identity**:
   - Use authorized contributor credentials:
     - Name: `KafraAgent`
     - Email: `337309732+KafraAgent@users.noreply.github.com`
3. **Pull Request Discipline**:
   - Link the PR to the relevant issue using standard GitHub keywords (e.g. `Resolves #13`).
   - Include a concise description of changes, list verified offline test commands, and confirm hardware safety rules were observed.
   - Do NOT commit generated build artifacts (`bin/`, `obj/`, `__pycache__/`, `.vs/`).
