# Wobkey Crush 80 — Patched Firmware & SignalRGB Plugin

The Wobkey Crush 80 (VID `0x320F`, PID `0x5055`) ships with a firmware bug: setting the hue (H byte) via the VIA USB protocol has no effect on the displayed color. This repository contains a binary patch that fixes the bug and a custom SignalRGB plugin that syncs the keyboard's backlight color to SignalRGB effects.

> **Firmware licensing notice:** QMK identifies WOBKEY firmware as GPLv2-derived without complete corresponding source. This repository currently retains extracted and patched binaries temporarily, but does **not** claim that their distribution is GPLv2-compliant. The community patch tooling is GPL-2.0-only. See [`firmware/GPL-COMPLIANCE.md`](firmware/GPL-COMPLIANCE.md).

## Per-key RGB (v1.06)

The per-key patch, Linux host tool, and wired SignalRGB V3 plugin are documented
in [docs/user/PER-KEY-RGB.md](docs/user/PER-KEY-RGB.md), including hardware findings,
installation, wire protocol, and verification status.

- Build: `python3 firmware/tools/patching/patch_firmware_per_key.py`
- Control: `python3 host/linux/per_key_rgb.py --help`
- Native Windows frame benchmark: `py host/windows/benchmark_per_key_rgb.py --frames 600`
  (requires `hidapi==0.15.0`; close SignalRGB/VIA first). Setup and interpretation:
  [Windows HID benchmark](docs/user/PER-KEY-RGB.md#native-windows-hid-benchmark).
- SignalRGB per-key: `plugins/signalrgb/wired/WobkeyCrush80_v3.js` — now requires
  `firmware/releases/v1.06-per-key/firmware_per_key_v3.bin` (**PKRG v2**).
  This is a firmware upgrade, not just a plugin replacement. Wired streaming
  uses 11 silent fragments and one commit/acknowledgement per complete frame.
  Wired USB frame delivery/readback is hardware-verified. Windows SignalRGB
  2.5.54 showed no throughput improvement; a native Windows HIDAPI run also
  measured about 25 ms per transfer. The separate SignalRGB gap remains about
  33 ms. See [findings and hypotheses](docs/user/PER-KEY-RGB.md#performance-investigation-wrap-up-2026-09-26).
  Physical appearance, normal typing, and sustained performance still need confirmation.
- V1/V2 plugins remain whole-board color controllers for hue-patched firmware.
  Wireless V3 requires the same PKRG v2 image but keeps acknowledged chunk
  writes; wireless behavior remains experimental and unverified.

- Windows installer project: `host/windows/Crush80FirmwareInstaller/` includes the
  optional PKRG v2 firmware and both matching V3 plugin installers. Keep the
  older `firmware_per_key_v2.bin` and its matching older plugin for rollback.

### C# RGB SDK

The [.NET 10 SDK](sdk/dotnet/README.md) and [sample app](sdk/dotnet/samples/Wobkey.Crush80.Sample/) target **wired** per-key v1.06 firmware (USB `320F:5055`, VIA usage page `0xFF60`, usage `0x61`). Discovery and opening perform no lighting writes; opening accepts exact PKRG v1 capabilities (92 LEDs, eight per transfer) or optimized PKRG v2 capabilities with additional 9-LED/11-fragment stream metadata, and rejects other formats before lighting writes. The SDK exposes v2 streaming capability but **uses only sequential operation-2 chunk writes** on both versions, without atomic streaming. Future host-rendered effects and v2 atomic streaming remain separate capabilities. The SDK provides explicit `session.Advanced` controls and an `AcquireControlAsync` lease that captures colors, override, brightness and effect before taking control.

Run `dotnet run --project sdk/dotnet/samples/Wobkey.Crush80.Sample/Wobkey.Crush80.Sample.csproj -- --help` for non-writing usage, or `--list` for non-writing discovery. Only `--smoke` writes lighting: it requires typing `SMOKE` before opening, verifies a temporary Esc/F1 pattern through all-92 readback, and explicitly restores. **Do not run it without hardware authorization.** Close other keyboard controllers first. Frames are volatile, sequential/non-atomic, with no FPS guarantee; restoration after faults or disconnection cannot be guaranteed. Windows wired PKRG v2 common-subset hardware verification is recorded below; Linux and macOS SDK hardware paths remain implementation targets pending separate authorized smoke runs. See the SDK README for permissions, cancellation, restoration failures, package notices, and the release-smoke checklist.

Windows wired PKRG v2 common-subset SDK behavior was hardware-verified on 2026-09-26 (SDK commit `501b802`, optimized PKRG v2 firmware): `PKRG v2: 92 LEDs, 8 per chunk; override initially False.` The sample reported `Full 92-color pattern readback and restored mode/effect/brightness/RGB verified.` The user physically confirmed Esc red and F1 green during the two-second pattern. This verifies capability negotiation, sequential operation-2 writes, full readback, physical output, and restoration only on Windows. Linux and macOS SDK hardware paths remain implementation targets requiring separate authorized smoke runs. V2 atomic streaming operations 3/4 remain unimplemented and unverified in the SDK.


## Licensing

The C# SDK under [`sdk/dotnet/`](sdk/dotnet/) is licensed under the [Apache License 2.0](sdk/dotnet/LICENSE.txt). Its NuGet package includes the SDK license and the complete HidSharp license and attribution. This scoped SDK license does not apply to firmware images, vendor executables, or other repository material.

## The Problem

The VIA SET handler at `0xDA20` stores the H byte to the internal state struct but then overwrites the RGB fields with stale cached values from global RAM instead of converting H to RGB. The result is that any color set through VIA (including SignalRGB) is ignored — the keyboard stays on whatever color was last set locally.

## Repository Structure

```
docs/user/                User-facing installation and per-key guide
firmware/
  sources/                Stock firmware, OTA image, and OTA parameters
  vendor/                 Original Windows updater executables
  releases/               Versioned patched firmware and OTA images
  tools/patching/         Reproducible firmware patch builders

host/
  linux/                  OTA, VIA backup, and per-key RGB utilities
  windows/Crush80FirmwareInstaller/
                          Windows WPF firmware installer

sdk/dotnet/               .NET 10 wired per-key SDK, sample, and tests

plugins/signalrgb/
  wired/                  Wired keyboard plugin variants
  wireless/               2.4 GHz dongle plugin variants
plugins/tools/via-test.html
                          Browser-based WebHID test tool

hardware/
  layouts/                VIA definitions and backup reference sample
  udev/                   Linux hidraw access rule

research/
  docs/                   Reverse-engineering reports
  tools/                  Extraction and analysis utilities
  vendor-flasher/         Decompiled vendor flasher and resources
  notes/                  Firmware version comparison
tests/                    Offline firmware, host, restore, and plugin tests
```

---

## Flashing the Patched Firmware

The patched firmware fixes the VIA color handler by inserting an HSV-to-RGB conversion routine into unused padding space in the firmware image. It is **required** for the SignalRGB plugin (or any VIA-based color control) to work.

### 1. Generate the patched firmware

Pre-built v1.04 binaries are included in `firmware/releases/v1.04/`; the extracted stock image and OTA wrapper are in `firmware/sources/`. Regenerate the v1.04 hue patch with:

```sh
python3 firmware/tools/patching/patch_firmware.py
```

The builder verifies the patch site and CRC, then writes the standalone and OTA outputs to `firmware/releases/v1.04/`.

### 2. Flash via `flash_ota.py` (recommended)

`flash_ota.py` is a standalone Python 3 script (no dependencies) that flashes firmware directly over USB using the Telink OTA protocol. It auto-detects the keyboard's OTA HID interface.

**Prerequisites:**
- Linux (uses `/dev/hidraw*`)
- The keyboard connected via USB (not Bluetooth/2.4G)
- Read/write access to the hidraw device (see [udev rule](#linux-udev-rule) below)

**Flash the patched firmware:**

```sh
python3 host/linux/flash_ota.py firmware/releases/v1.04/firmware_patched.bin
```

Or using the full OTA image:

```sh
python3 host/linux/flash_ota.py firmware/releases/v1.04/code_2M_patched.bin
```

The script shows firmware details, asks for confirmation, then flashes with a progress bar. The keyboard reboots automatically after a successful flash.

**Other options:**

```sh
# Dry run — show what would be sent without flashing
python3 host/linux/flash_ota.py --dry-run firmware/releases/v1.04/firmware_patched.bin

# Specify device manually
python3 host/linux/flash_ota.py --device /dev/hidraw4 firmware/releases/v1.04/firmware_patched.bin

# Probe the OTA interface without flashing
python3 host/linux/flash_ota.py --probe
```

### Flash via the Windows installer

`host/windows/Crush80FirmwareInstaller/` contains the WPF installer targeting .NET 10 on Windows. It auto-detects the OTA HID interface by VID, PID, and usage page, validates the selected firmware, then performs the same response-driven Telink OTA transfer as `host/linux/flash_ota.py`.

```powershell
cd host/windows/Crush80FirmwareInstaller
dotnet run --project Crush80FirmwareInstaller.csproj
```

The output directory contains `firmware-catalog.json` and a `firmware/` folder beside the executable. Firmware releases are external data, not embedded resources. To ship a newer patched binary without recompiling:

1. Copy the `.bin` file into the output `firmware/` folder.
2. Add or update its entry in `firmware-catalog.json` with `name`, `version`, `file`, target device ID, and optional SHA-256.
3. Restart the installer or click **Reload catalog**.

Device targets in the same JSON file configure VID, PID, HID usage page, report ID, and OTA timeouts. The supplied catalog selects wired Crush 80 devices at `320F:5055`, usage page `0xFFEF`, report ID `5`, and offers patched firmware versions 1.06 and 1.04.

> **Warning:** Flashing custom firmware carries risk. Keep the stock `firmware/sources/firmware.bin` and `firmware/sources/code_2M.bin` available for recovery. Recovery depends on the OTA interface remaining available; do not assume a physical key sequence guarantees bootloader entry.

---

## Installing the SignalRGB Plugin

Choose the plugin that matches both firmware and connection mode:

| Firmware / connection | Plugin |
|---|---|
| Hue patch, wired USB (legacy) | `plugins/signalrgb/wired/WobkeyCrush80.js` |
| Hue patch, wired USB (recommended) | `plugins/signalrgb/wired/WobkeyCrush80_v2.js` |
| Hue patch, 2.4 GHz dongle | `plugins/signalrgb/wireless/WobkeyCrush80Wireless_v2.js` |
| Hue patch, 2.4 GHz dongle (legacy) | `plugins/signalrgb/wireless/WobkeyCrush80Wireless.js` |
| PKRG v2 per-key firmware, wired USB | `plugins/signalrgb/wired/WobkeyCrush80_v3.js` |
| PKRG v2 per-key firmware, 2.4 GHz dongle (experimental) | `plugins/signalrgb/wireless/WobkeyCrush80Wireless_v3.js` |

### Steps

1. Open **SignalRGB**.
2. Go to the **Devices** page and find the Wobkey Crush 80.
3. Open the device's **Device Information** page.
4. Click the **Plugins** button to open the custom plugins folder.
5. Copy the matching plugin from the table above into that folder.
6. **Restart SignalRGB** completely (close and relaunch).

The keyboard should now appear as a controllable device. SignalRGB effects will be synced to the keyboard's backlight.

### Legacy wired V1 plugin behavior

- On **Initialize**, the plugin switches the keyboard to solid-color mode (Effect 6 / `LIGHT_MODE`)
- On each **Render** frame, it averages all LED positions on the SignalRGB canvas into a single RGB color, converts it to HSV, and sends the hue + saturation via VIA channel 3 (command `0x07`). Brightness is derived from the V component and mapped to the keyboard's 0–9 range
- On **Shutdown**, brightness is restored to maximum (9)
- Values are only sent when they change, to avoid flooding the USB bus

### Removing the Plugin

Delete the same matching plugin file you installed from the table above and restart SignalRGB. The keyboard will revert to default behavior.

> **Note:** While a custom plugin is installed, SignalRGB will not apply its own updates or fixes for that device. Remove the plugin file to receive upstream improvements again.

---

## VIA Test Tool

`plugins/tools/via-test.html` is a standalone browser-based tool for testing VIA communication with the keyboard over WebHID. Open it in Chrome or Edge, click **Connect**, and use the controls to send color, brightness, and effect commands. This is useful for verifying the firmware patch before setting up SignalRGB.

### USB Interface Map

| Interface | Usage Page | Purpose |
|---|---|---|
| 0 | `0x0001` | Standard keyboard HID |
| 1 | `0xFF60` | VIA protocol (use this one) |
| 2 | `0xFFEF` | OTA firmware flasher |
| 3 | `0xFF1C` | Wireless mode switch — **never write to this** |

---

## Linux udev Rule

For hidraw access without root (required by `host/linux/flash_ota.py` and useful for the VIA test tool):

```sh
sudo cp hardware/udev/99-wobkey-crush80.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules
sudo udevadm trigger
```
