# Wobkey Crush 80 — Patched Firmware & SignalRGB Plugin

The Wobkey Crush 80 (VID `0x320F`, PID `0x5055`) ships with a firmware bug: setting the hue (H byte) via the VIA USB protocol has no effect on the displayed color. This repository contains a binary patch that fixes the bug and a custom SignalRGB plugin that syncs the keyboard's backlight color to SignalRGB effects.

## Per-key RGB (v1.06)

The per-key patch, Linux host tool, and wired SignalRGB V3 plugin are documented
in [docs/user/PER-KEY-RGB.md](docs/user/PER-KEY-RGB.md), including hardware findings,
installation, wire protocol, and verification status.

- Build: `python3 firmware/tools/patching/patch_firmware_per_key.py`
- Control: `python3 host/linux/per_key_rgb.py --help`
- SignalRGB per-key: `plugins/signalrgb/wired/WobkeyCrush80_v3.js` — requires the per-key
  firmware; copy it into SignalRGB's custom Plugins folder and remove older
  wired custom plugins for this device before restarting SignalRGB.
- V1/V2 plugins remain whole-board color controllers for hue-patched firmware.
  Wired V3 is the per-key plugin; wireless V3 is experimental and unverified.

- Windows installer project: `host/windows/Crush80FirmwareInstaller/` includes the
  optional per-key v1.06 firmware and both V3 plugin installers. Wireless V3
  support remains experimental and unverified.

### C# RGB SDK

The `sdk/dotnet/` solution provides `Wobkey.Crush80.Sdk` for applications using
the wired per-key RGB firmware. An injected `IHidTransport` can be opened with
`Crush80RgbSession.OpenAsync(transport)`; opening performs only a read-only PKRG
capability handshake and rejects firmware that is not version 1 with 92 LEDs
and eight LEDs per transfer. The session owns and closes the injected transport.

`session.Advanced` supports full-frame and range RGB writes, readback, override
mode, OEM brightness/effect, and ordered state capture/restoration. Await each
operation before changing its supplied memory. Session operations are serialized;
after a timeout, malformed response, or transport failure, dispose and reopen
the session instead of retrying on the faulted request stream.

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
| Per-key firmware, wired USB | `plugins/signalrgb/wired/WobkeyCrush80_v3.js` |
| Per-key firmware, 2.4 GHz dongle (experimental) | `plugins/signalrgb/wireless/WobkeyCrush80Wireless_v3.js` |

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
