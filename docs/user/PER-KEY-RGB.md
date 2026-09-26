# Wobkey Crush 80: per-key RGB

## Conclusion

**Host-controlled per-key RGB is working on the physical keyboard over USB.**
An initial diagnostic made Esc red and F1 green. The completed patch then
accepted RGB updates from `host/linux/per_key_rgb.py`, and the user physically
confirmed **Esc green and F1 blue** on 2026-09-25.

All 92 supplied RGB slots were written and read back through the real device;
the Esc/F1 check proves that host-supplied values reach the physical LEDs.
Streaming performance, complete physical key mapping, and wireless operation
remain unverified. Per-key data is volatile, not persisted across power cycles.

## Verified device and baseline

| Item | Observed value |
|---|---|
| Device | RDR Crush 80 |
| Wired USB VID:PID | `320F:5055` |
| USB device revision before the diagnostic | `0x0106` |
| VIA protocol | 11 (`0x000B`) |
| VIA interface | Interface 1, usage page `0xFF60`, 32-byte reports, no report ID |
| Initial lighting | Brightness 9; effect 7; speed 2; hue 183; saturation 255 |
| Keymap | Four layers, 8 rows × 16 columns, 1,024 bytes total |
| Baseline image | `firmware/releases/v1.06/v2_patched.bin`, 122,196 bytes |
| Baseline SHA-256 | `1c3d3970cd8430133db1ac7e722749f059cf3a20390c59f18b00d03ddd238561` |
| Baseline firmware CRC footer | `0xCA559215` |

The OEM firmware-information query returned CRC `0xCA559215`, matching the
baseline image. This is a firmware-reported CRC match, not a complete flash
readback or a cryptographic attestation.

Shell history contained a command referencing
`firmware/code_2M_20260618_patched.bin`. That file is present on branch
`v20260618_patch` at commit `3c149019ad9986c8b53eb5fca28a000e50212a81`, but the
connected device's reported CRC matched the older v1.06 hue patch instead.
The June image contains USB revision `0x0108`, has a different code layout,
and is 124,436 bytes long. Its patch changes hue conversion only, not per-key
control. Do not reuse v1.06 patch addresses against it.

## Why `feat/per-key-led` did not work

The old branch was inspected at commit
`13431f2699df03a4fb16d6b81b260dd3e073c758`. Its binary CRC was valid, its OTA
payload matched its standalone image, and its generated code caves matched
the committed machine code. Those integrity checks did not establish correct
behavior.

### 1. The render hook was assigned to the wrong effect

The old notes identify `0xB7CC` and the loop near `0xA6FE` as effect 6
(`LIGHT_MODE`). The actual effect dispatch table disproves that identification:

- `0xA270` reads the effect from `state[1]`.
- `0xA52E..0xA53C` dispatch through the table at `0x2001B0A4`.
- Table entry **6** points to `0x2000A998`.
- Table entry **15** points to `0x2000A6F4`.

Thus the old patch changed an effect-15 path, not the solid-color path the
host selected. The hardware-confirmed diagnostic uses effect 6's actual
per-LED stores at `0xAA3C..0xAA47`.

### 2. The VIA extension intercepted a legacy handler

The old patch intercepts `0xDD60`, the legacy color-handler body. The normal
VIA 11 packet `[0x07, 3, 4, H, S, ...]` takes the color path at `0xDBDE` and
bypasses that interception.

The legacy protocol-9 path uses `[0x07, 0x83, H, S, ...]` and can reach
`0xDD60`. The firmware checks its protocol selector before choosing these
paths. Packet offsets are not interchangeable between the two formats.

The branch instructions were decoded against the primary
[Andes ISA decoder definitions](https://github.com/andestech/qemu/blob/ast-v5_4_2-release/target/riscv/XAndesV5Isa.decode).
Both routes were exercised offline with those vendor branch semantics.

### 3. The old cave destroyed live loop registers

The old render cave treats `t0` and `t1` as scratch registers, even though they
remain live across iterations. In that renderer, `t0` controls color-source
selection and `t1` holds a palette base.

Executing the committed cave for LED index 1 left `t0 = 3` and
`t1 = 0x2001D403`. The next iteration branched to the alternate palette path
at `0xA764`, rather than the hooked path at `0xA760`.

A mid-function patch must preserve **all live registers**, not merely the
callee-saved registers required at ordinary function-call boundaries.

### Other unsafe assumptions

- The old buffer at `0x2001D400` was selected from a highest-static-reference
  scan, not demonstrated RAM ownership. Several `0x2001xxxx` addresses in
  these notes are flash-mapped constant tables, not mutable DLM globals.
- The single-byte mode magic was not an initialization mechanism. Prior
  observations included the magic value already appearing at boot.
- The chunk handler had no bounds checks. An offline packet with
  `start=255, count=2` wrote through the buffer's end and changed the mode byte.
- The old 154-LED count is not a verified physical count; the SignalRGB plugin
  creates a synthetic 22 × 7 canvas. The verified effect-6 table has 92 valid
  LED indices, which still require physical mapping.
- The AW20216S identity/chip-count claims were not established. PCB photographs
  show `DI`/`DO` markings at LEDs, consistent with an addressable chain, but
  the LED component model has not been identified.

## Corrected v1.06 solid-color rendering map

Addresses below are **offsets in `firmware/releases/v1.06/v2_patched.bin`**, unless a full runtime address is given. Code and constant-table references use the
`0x20000000` flash mapping. GP-relative mutable data must not be confused
with those constants.

| Location | Meaning |
|---|---|
| `0x1B0A4` | Effect-dispatch table in the image |
| `0xA998` | Effect-6 renderer entry |
| `0xA9E8` | Chooses palette versus user-color RGB source |
| `0xB854..0xB85F` | Loads the user R/G/B fields for effect 6 |
| `0xAA2C` | Loads a raw LED index from the current table position |
| `0xAA30` | Skips indices above 91 |
| `0xAA34..0xAA3B` | Computes the indexed framebuffer address |
| `0xAA3C..0xAA47` | Three byte stores to that LED's RGB triplet |
| `0xAA48` | Normal continuation after the stores |
| `0x1CDB8..0x1CE47` | Twelve groups of twelve LED-index entries |
| `0x1C198` | Brightness lookup; levels 0..9 give `0,21,42,63,84,105,126,147,168,192` |

The index table contains every index 0..91 exactly once; unused entries are
`0xFF`. At the store splice:

- `a4` is the destination framebuffer address for this LED.
- `t3` is the framebuffer base.
- `t6` is the OEM brightness lookup result.
- The original stores use `a0`, `a1`, and `a3` as RGB components.

The diagnostic determines slots 0 and 1 by comparing `a4` with `t3` and
`t3 + 3`; it does not assume a separate RGB buffer or hard-code a mutable
RAM address.

## Hardware diagnostic and evidence

The throwaway diagnostic uses the baseline image and changes only:

1. A 68-byte routine in previously zero-filled bytes at `0x304..0x347`.
2. The 12-byte store block at `0xAA3C..0xAA47`, replaced with a jump and NOPs.
3. The firmware CRC footer.

It preserves all general-purpose registers, uses no stack or new RAM,
changes no USB commands, and does not select an effect automatically.
In effect 6, slot 0 receives `(brightness, 0, 0)`, slot 1 receives
`(0, brightness, 0)`, and all other key slots receive zero.

| Diagnostic artifact | Value |
|---|---|
| SHA-256 | `89f6dde118f2ad56b0aefffd2dde9b89afd62ad08bd070c54d7ffe0377fe7659` |
| Firmware CRC footer | `0xC06B4B3C` |
| Image length | 122,196 bytes |
| Total changed bytes versus baseline | 65 |

Verification performed:

- The unmodified firmware failed the two-color framebuffer expectation.
- The diagnostic executed through the actual effect-6 dispatch and complete
  renderer in Unicorn, with the required Andes instructions decoded explicitly.
- Exact framebuffer output passed at brightness levels 0, 1, 4, and 9.
- All 92 store-splice cases preserved every general-purpose register and wrote
  only their own RGB triplet.
- Non-framebuffer writes matched the original renderer in the full-loop probe.
- `host/linux/flash_ota.py --dry-run` accepted the image and CRC.
- After explicit user authorization, OTA completed successfully. The rebooted
  device reported the expected CRC `0xC06B4B3C` and VIA protocol 11.
- Effect 6 and brightness 9 were set/read back. The user reported **Esc red,
  F1 green**. Other keys being dark and normal typing were not separately
  confirmed by the user; do not turn the emulator results into those claims.

After observation, effect 7 and brightness 9 were read back. The diagnostic
firmware remained installed at the end of the experiment, so selecting effect
6 still displayed the two-key pattern.

## Configuration preservation and recovery

The OTA update reset lighting settings and changed 19 keymap entries. The
pre-flash backup was used to restore those entries, including a changed
zero/default entry, and all 1,024 keymap bytes were read back and matched.

The existing `host/linux/via_backup.py` JSON format was used. Keep backup files
outside the repository. With no file argument, `via_backup.py` reads or writes
`via_config.json` in the process's current working directory; relative file
arguments are resolved from that directory, not from the script or repository
directory. The commands below pass explicit paths. The tracked
`hardware/layouts/via_config.json` is a sample, not a default restore input.

```text
~/.local/state/wobkey/via-before-perkey-20260925.json
```

Its SHA-256 is
`44ff299bab4d9fb5f888b8ccaaf7b384d76c2e916db643a95c8bb73ca24abc94`.

Do not assume skipped `0x0000` or `0xFFFF` keycodes are always correct after
flashing; restore changed entries and verify the complete result.

The known prior image is `firmware/releases/v1.06/v2_patched.bin`. A forced
procedure has **not** been verified on this board. USB rollback is possible
only while an appropriate update interface remains available; do not promise
that holding Esc guarantees recovery.

### Non-updating firmware identity request

The decompiled vendor flasher's `getFwVersionToolStripMenuItem_Click` sends a
separate identity request, not OTA START:

- Interface with usage page `0xFFEF`, report ID 5.
- 64-byte output: `05 01 00 00`, followed by 29 `FF` bytes, then 31 zero bytes.
- Reply prefix: `05 01 08 00`.
- Little-endian firmware version at bytes 4..7; CRC at bytes 8..11.

The device reported version zero, so the CRC was used for matching. Never
write to the separate `0xFF1C` wireless-control interface. Also do not use
`flash_ota.py --probe` as a read-only identity query: it sends OTA START.

## Working-patch implementation boundary

The next implementation targets **wired USB on the v1.06 baseline**. It must
provide host-supplied RGB values, explicit enable/disable behavior, bounded
packets, and safe boot initialization. Other OEM effects and normal VIA
configuration must remain available. The host tool must verify the custom
firmware protocol before sending RGB data.

A complete working patch additionally requires evidence for buffer ownership
and for the route from a real VIA 11 request to the new handler. The old
branch's scratch addresses, magic-byte assumptions, renderer name, and
protocol offsets are not an implementation specification.

Saved per-key profiles, Bluetooth, 2.4 GHz throughput, and a SignalRGB plugin
are separate capabilities; the diagnostic proves none of them.

## Working USB patch design

### Owned storage and startup

The baseline initializes `gp = 0x80800` and a downward-growing stack at
`0xA0000`. Its startup code clears BSS only through `0x827F8`. Rather than
treating an unreferenced address as an allocation, the patch reserves the top
288 bytes of the original stack region:

- New initial SP: `0x9FEE0`, in both startup paths.
- Per-key RGB array: `0x9FEE0..0x9FFF3` (92 × 3 bytes).
- Enable flag: `0x9FFFC` (zero or one).
- Startup clears the entire reserved 288-byte region before calling the
  original main entry at `0x36D4`.
- The original BSS bounds, GP, framebuffers, and constant tables are unchanged.

The two SP setup instructions at image offsets `0x44` and `0x1CE` are adjusted.
The two startup call sequences at `0x172..0x189` and `0x2D0..0x2E7` are routed
through an absolute-address initialization routine, accounting for the second
startup sequence being copied to instruction memory at address zero.

This is an explicit stack reservation, not a claim that a static-reference
scan proves arbitrary RAM free. Offline execution must check both startup
paths. Wired operation is the acceptance target; wireless power-state behavior
is not established by this design.

### Rendering behavior

The hardware-proven effect-6 store splice at `0xAA3C` remains the interception
point. Disabled: replay the original RGB stores. Enabled: use the raw LED
offset implied by `a4 - t3` to load the reserved RGB triplet, scale each
component by the OEM brightness byte in `t6` using `(component * t6) >> 8`,
and write to the existing framebuffer. Preserve every modified register and
restore SP on both paths. Do not apply the global saturation overlay to
host-provided RGB values.

Other effects remain OEM-controlled. Returning to effect 6 while enabled
resumes the supplied colors. Explicit disable returns effect 6 to its normal
color source. A cold boot initializes disabled mode and a black RGB array.
Per-key RGB data is not saved to flash.

### VIA 11 wire contract, version 1

Use normal 32-byte VIA reports, **vendor channel `0x7F`**. Intercept the
custom-SET entry at `0xDBAC` and custom-GET entry at `0xDC34`, before the OEM
channel and legacy-format dispatch. All other channels replay the displaced
instruction and continue through the OEM handler.

Every custom response has `[command, 0x7F, operation, status, ...]`.
Commands are SET `0x07` and GET `0x08`. Status is zero for success, 1 for an
unsupported operation, 2 for an invalid LED range, and 3 for an invalid mode.
All validation precedes writes to the RGB array or mode flag.

| Operation | Request bytes after operation | Successful response |
|---|---|---|
| GET 0: capabilities | Zero padding | Bytes 4..7 `PKRG`; byte 8 protocol version 1; byte 9 LED count 92; byte 10 chunk limit 8; byte 11 enabled flag |
| GET 1: mode | Zero padding | Byte 4 enabled flag |
| SET 1: mode | Byte 3 zero; byte 4 mode, exactly 0 or 1 | Byte 4 accepted mode |
| GET 2: RGB chunk | Byte 3 zero; byte 4 start; byte 5 count | Same start/count; RGB triplets at byte 6 onward |
| SET 2: RGB chunk | Byte 3 zero; byte 4 start; byte 5 count; RGB triplets at byte 6 onward | Same start/count and RGB data, status zero |

Valid RGB ranges satisfy `1 <= count <= 8`, `start < 92`, and
`start + count <= 92`. The maximum data length is 24 bytes, so neither reads
nor writes exceed the 32-byte report. Capability discovery is read-only; a
host must require the signature/version/count before attempting custom SETs.

### Implementation plan

**Goal:** host-supplied wired USB per-key colors with deterministic startup,
explicit enable/disable, bounded transfers, and preserved OEM configuration.

**Architecture:** extend the existing binary patch workflow, preserve the OEM
LED transport, and expose the array through the early VIA 11 custom handlers.
The host CLI uses the repository's existing HID discovery/report framing.

**Tech stack:** Python standard library for build/control; RV32I/M injection;
Unicorn for machine-code regression checks.

1. `firmware/tools/patching/patch_firmware_per_key.py`: build an image only from the exact
   baseline SHA-256, verify splice bytes and empty caves, assemble startup,
   renderer, and GET/SET routines, and update the CRC. Expose
   `build_image(source: bytes) -> bytes` for offline verification. Output
   `firmware/releases/v1.06-per-key/firmware_per_key_v2.bin` and its matching OTA-wrapped image.
2. `tests/test_per_key_firmware.py`: execute generated instructions, not mocked
   behavior. Cover both reset paths, zeroed storage, fallback stores, register
   preservation, independent RGB output, capability discovery, mode changes,
   chunk round trips, boundary rejection without mutation, and OEM-channel
   fallback. Run the RGB behavior against the baseline first to observe the
   missing behavior, then against the patch.
3. `host/linux/per_key_rgb.py`: provide `info`, `enable`, `disable`, `fill`,
   `set`, `read`, and full `frame` upload commands. Require capability
   discovery, validate complete inputs before writes, match acknowledgements,
   and verify chunk writes through RGB readback. `enable` selects effect 6;
   `disable` changes only the override flag. No automatic flash or SAVE.
4. Build and exercise the actual CLI, run the machine-code regressions, and
   dry-run the existing OTA flasher. Back up configuration before hardware
   updates; verify the firmware CRC after reboot; restore and read back the
   complete keymap after flashing. Prove host-driven changes on Esc/F1 with
   the user and record the observed result below. Do not claim streaming
   throughput or complete physical key mapping from this two-key check.

## Build and use the wired patch

Build from the repository's exact v1.06 hue-patched baseline:

```sh
python3 firmware/tools/patching/patch_firmware_per_key.py
```

Outputs:

- `firmware/releases/v1.06-per-key/firmware_per_key_v2.bin`: standalone firmware, 122,196 bytes.
- `firmware/releases/v1.06-per-key/code_2M_per_key_v2.bin`: matching 2 MiB OTA wrapper.
- Firmware CRC footer: `0xE5BE2E50`.
- Standalone SHA-256:
  `f7c1a736b8172db26ba07f6eba2b094b607e8067c91e2f1807eba767e630a7cc`.
- Injected code uses 632 of the verified 1,220 cave bytes.

The builder rejects other baseline images rather than guessing new offsets.
It does not connect to or flash the keyboard.

Before a flash, close other keyboard-control software and save a fresh backup
under a filename that does not overwrite an earlier backup:

```sh
python3 host/linux/via_backup.py save /path/to/pre-per-key-backup.json
python3 host/linux/flash_ota.py --dry-run firmware/releases/v1.06-per-key/firmware_per_key_v2.bin
# Only after accepting the recovery risk described above:
python3 host/linux/flash_ota.py firmware/releases/v1.06-per-key/firmware_per_key_v2.bin
python3 host/linux/via_backup.py restore /path/to/pre-per-key-backup.json
```

The restore command now compares current keycodes, restores changed entries
including `0x0000` and `0xFFFF`, and verifies every restored keymap byte. A
failed restore produces a failing exit status. Keep the original backup even
after a successful restore.

The following commands use the wired VIA interface only. Capability discovery
must succeed before the tool sends any per-key SET request:

```sh
python3 host/linux/per_key_rgb.py info
python3 host/linux/per_key_rgb.py fill 000000
python3 host/linux/per_key_rgb.py set 0=00ff00 1=0000ff
python3 host/linux/per_key_rgb.py read 0 2
python3 host/linux/per_key_rgb.py disable
python3 host/linux/per_key_rgb.py enable
```

`fill`, `set`, and `frame` verify supplied RGB values through firmware
readback, enable the override, and select effect 6. `set` retains other LED
colors. `disable` leaves the selected OEM effect unchanged and restores its
normal rendering. `enable` uses the existing RGB buffer and selects effect 6.
Brightness remains the keyboard's current brightness setting.

Full frames use a JSON array of exactly 92 `[R, G, B]` triplets, with integer
channels in `0..255`:

```sh
python3 host/linux/per_key_rgb.py frame /path/to/frame.json
python3 host/linux/per_key_rgb.py read
```

Uploads are chunked, not atomically double-buffered. No frame-rate guarantee
has been measured. `read` returns supplied RGB values, before global
brightness scaling; maximum firmware brightness is 192/256 of the supplied
channel value. Black remains black, and neutral RGB values stay neutral.

### Automated verification

```sh
python -m venv /tmp/wobkey-per-key-tests
/tmp/wobkey-per-key-tests/bin/python -m pip install -r tests/requirements.txt
/tmp/wobkey-per-key-tests/bin/python -m unittest discover -s tests -v
```

Seventeen regressions passed during implementation. They cover generated
machine code, both startup paths, full effect-6 rendering, register
preservation, protocol bounds, host-client frame round trips, stock-firmware
rejection, and exact restoration of disabled/default keys. The original
firmware failed capability discovery and stack-reservation expectations; the
original restore routine failed the disabled/default-key regression.

The actual host CLI also rejected the previously installed diagnostic image
without sending RGB changes, and the existing flasher accepted the working
image in dry-run mode. These are offline/transport-preflight results, not
physical confirmation of the working host-driven firmware.

## Working-patch hardware verification, 2026-09-25

With explicit user authorization, the working image was flashed successfully.
The OEM identity query after reboot reported **CRC `0xE5BE2E50`**, matching the
generated image. The device remained available through its normal VIA
interface.

Results exercised on the connected keyboard:

1. `per_key_rgb.py info` returned signature/version-compatible capabilities:
   protocol 1, 92 LEDs, eight LEDs per chunk, override **disabled**.
2. Reading all 92 RGB slots after boot returned zero triplets.
3. The actual CLI uploaded a varying full frame through twelve bounded
   packets, then read back all **276 bytes** correctly, including the last
   partial chunk.
4. Explicit disable and enable were acknowledged and read back. The supplied
   frame was preserved across both transitions.
5. `fill 000000` followed by `set 0=00ff00 1=0000ff` produced exact supplied-RGB
   readback: Esc green, F1 blue, and the other 90 logical slots black.
   The user then physically confirmed **Esc green and F1 blue**. This proves
   the working host-command → RGB storage → renderer → LED path, rather than
   only the earlier fixed-color diagnostic.

A fresh pre-flash backup was saved to:

```text
~/.local/state/wobkey/via-before-working-perkey-20260925T212713.json
```

The current restore command restored the keymap and verified all 1,024 bytes.
The most recent pre-flash lighting was effect **4**, brightness 9, speed 2,
hue 183, saturation 255; it was restored and read back before the RGB tests.
The working patch was then left in effect 6 with the green/blue inspection
pattern. No per-key profile was saved to flash.

The working firmware remains installed with per-key mode enabled and the
green/blue pattern active. RGB values are volatile: power cycling starts with
a cleared array and the override disabled. Complete physical key mapping,
wireless behavior, and animation throughput remain unverified.

## SignalRGB V3 per-key plugin

`plugins/signalrgb/wired/WobkeyCrush80_v3.js` is a standalone **wired USB** plugin for the
working per-key firmware above. It does not support the 2.4 GHz dongle or
Bluetooth and does not flash firmware. V1/V2 plugins remain available for
older firmware; do not run an older wired plugin alongside V3.

### Install

1. Keep the working per-key firmware installed; the hue-only images do not
   expose the required `PKRG` protocol.
2. In SignalRGB, open the Crush 80's **Device Information → Plugins** folder.
3. Move older wired custom plugins for this keyboard out of that folder.
   Renaming them while leaving a `.js` extension in the same folder is not
   sufficient: they can still compete for the device.
4. Copy `plugins/signalrgb/wired/WobkeyCrush80_v3.js` into the custom plugins folder and
   restart SignalRGB completely.
5. Use **Wobkey Crush 80 (Wired) V3 Per-Key**, enable streaming, and place it
   on the SignalRGB canvas. Adjust its size/position in the layout as needed.

The Windows installer catalog includes wired and experimental wireless V3 plugin
entries. Older firmware images do not expose the per-key protocol; keep the
working per-key image selected when using V3.

### Behavior

- Opens only interface 1, usage page `0xFF60`, usage `0x61`.
- Requires the capability signature, protocol version, 92-slot count, and
  eight-slot chunk limit before sending any lighting writes.
- Captures the current OEM effect, brightness, per-key enable state, and all
  92 RGB values before taking control.
- Uses direct RGB from `device.color(x, y)`: black is transmitted as black,
  white remains white, and colors are not averaged or reduced to HSV.
- Uses effect 6 and hardware brightness 9 while streaming. SignalRGB already
  applies its brightness setting to sampled colors, so V3 does not apply it
  a second time.
- Sends only changed eight-slot chunks, including the final four-slot chunk,
  and checks every acknowledgement. It does not cache a failed write as sent.
- On graceful shutdown or streaming disable, restores the captured RGB array,
  enable state, effect, and brightness. A transport error stops streaming and
  attempts restoration; unplugged hardware cannot be restored until reachable.
- Does not send SAVE, OTA, wireless-control, or reset commands.

Do not run VIA, the Python RGB tool, or another RGB controller against the
same interface while SignalRGB is streaming. Transport errors are reported
in the device console; fix the conflict and toggle streaming to reinitialize.

### Layout evidence

The v1.06 switch-to-LED table at image offset `0x1BC74` maps 91 of the 92
slots. Its consumer at `0x12A64..0x12A68` indexes that table from the scanned
switch index before handling reactive effects. This supplies actual firmware
LED indices rather than the old synthetic 22 × 7 canvas.

The remaining slot, **52**, was illuminated by itself through the working
firmware. The user identified **Caps Lock**. Its canvas position is shared
with the firmware-mapped Caps Lock slot 51. The three Space slots and the
alternate Enter-area slot are represented in the ANSI TKL canvas geometry.

Esc 0, F1 1, and Caps Lock 52 have physical observations. The other switch
names come from the firmware map and the repository's VIA layout; the canvas
geometry is ANSI-oriented and has not been individually checked on every key
or alternate PCB layout. Underglow is outside this 92-slot renderer.

### Verification and runtime limit

```sh
node --experimental-vm-modules --test tests/test_signalrgb_v3.mjs
```

Eight JavaScript regressions pass for each V3 variant: independent
black/white/RGB rendering, incompatible-firmware rejection without writes,
restoration of prior OEM and per-key states, changed-chunk behavior, error
recovery, functional key bindings/endpoint selection, transport identity,
and missing capability replies. Node emits its expected experimental-VM warning.

The unchanged V3 JavaScript was also executed through a temporary native-HID
bridge against the connected keyboard:

- Three distinct canvas frames each produced correct readback for all 92
  supplied RGB values, using 12 RGB packets per changed full frame.
- Repeating an unchanged frame sent zero RGB packets.
- `Shutdown()` restored the complete previous RGB frame, enable state,
  effect, and brightness; all were read back and matched.
- The user's original colors/settings were restored after the mapping and
  lifecycle checks.

**The Windows SignalRGB application itself was not run in this environment.**
The user subsequently reported that wired V3 works flawlessly. This is a
user-confirmed application result, separate from the automated native-HID
bridge checks; no sustained frame-rate measurement was recorded.

Primary API references:

- [SignalRGB plugin lifecycle and exports](https://docs.signalrgb.com/developer/plugins/)
- [HID writes, reads, actual read size, and buffer clearing](https://docs.signalrgb.com/developer/plugins/writes-and-reads/)
- [Canvas sampling and brightness semantics](https://docs.signalrgb.com/developer/plugins/utilities/)
- [Functional keyboard names](https://docs.signalrgb.com/developer/plugins/key-names/)
- [Installing a custom plugin](https://docs.signalrgb.com/troubleshooting/advanced-troubleshooting/replacing-plugin/)

## SignalRGB Wireless V3: experimental 2.4 GHz variant

`plugins/signalrgb/wireless/WobkeyCrush80Wireless_v3.js` targets the **2.4 GHz dongle** at
VID:PID `320F:5088`, interface 1, usage page `0xFF60`, usage `0x61`.
It uses the same per-key protocol, layout, changed-chunk handling, and state
restoration as wired V3. Only device identity, connection guidance, and log
labels differ. It is a standalone plugin; no shared JavaScript module is
required in the custom Plugins folder.

### Install and try

1. Leave the working per-key firmware installed on the **keyboard**.
   No new firmware is needed for this experiment.
2. Open SignalRGB's **Device Information → Plugins** folder and move older
   Crush 80 **wireless** custom plugins out of it.
3. Copy `WobkeyCrush80Wireless_v3.js` into that folder.
4. Connect the dongle, select 2.4 GHz mode using the keyboard's normal controls,
   and restart SignalRGB. The device name is
   **Wobkey Crush 80 (Wireless) V3 Per-Key**.
5. Try a static color first, then an animated effect. Do not stream from the
   wired and wireless plugins to the same keyboard simultaneously; disconnect
   the keyboard's USB data cable for the wireless trial.

The wired V3 file can remain installed because it targets a different PID.
Both V3 variants are available in the Windows installer catalog; these steps
are the manual-install alternative. Bluetooth is not supported.

**Never flash a keyboard firmware image onto the dongle.** Its update
interface updates the receiver, not the keyboard. This plugin never opens
that update interface or writes wireless-mode commands.

### Failure behavior and acceptance limit

Wireless support is **unverified**. The dongle must forward the complete
vendor-channel `0x7F` request and response, including the RGB payload.
An unsupported or missing capability reply prevents all lighting writes.
During streaming, a failed acknowledgement stops rendering and attempts to
restore the captured state; restoration itself may fail if the radio link is
unavailable. The device console reports these failures without silently
falling back to whole-board color.

The variant retains the wired plugin's 100 ms per-request timeout and does
not introduce retries, a new buffering protocol, or a promised frame rate.
Twelve acknowledged packets are needed for a completely changed 92-slot
frame; radio round-trip latency may reduce animation smoothness. Static and
unchanged colors generate much less traffic. Sleep/wake behavior and battery
impact have not been measured.

If it does not work acceptably, remove or disable the wireless V3 plugin,
reconnect through USB, and use wired V3. Failure of this experiment does not
require changing the working wired firmware.

### Checks performed

```sh
SIGNALRGB_TEST_PLUGIN=plugins/signalrgb/wireless/WobkeyCrush80Wireless_v3.js \
  node --experimental-vm-modules --test tests/test_signalrgb_v3.mjs
node --experimental-vm-modules --test tests/test_signalrgb_v3.mjs
```

All eight lifecycle/protocol regressions passed for wireless V3 and all eight
passed for wired V3. A separate offline lifecycle smoke run exercised the
actual wireless JavaScript through a stateful HID simulation: three varying
92-slot frames, an unchanged frame with zero RGB writes, and complete prior
state restoration on shutdown.

No `320F:5088` dongle was available to this environment, so these checks are
**not** proof of radio forwarding, over-air throughput, or wireless operation
inside SignalRGB. No device lighting or firmware was changed for these checks.
