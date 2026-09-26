# Wobkey Crush 80: per-key RGB

> **Licensing:** Complete corresponding source for the QMK-derived Wobkey firmware is not available. Retained stock and patched binaries are not represented as GPLv2-compliant distributions. See [`firmware/GPL-COMPLIANCE.md`](../../firmware/GPL-COMPLIANCE.md). The community patch builders are GPL-2.0-only.

## Conclusion

**Host-controlled per-key RGB is working on the physical keyboard over USB.**
An initial diagnostic made Esc red and F1 green. The completed patch then
accepted RGB updates from `host/linux/per_key_rgb.py`, and the user physically
confirmed **Esc green and F1 blue** on 2026-09-25.

All 92 supplied RGB slots were written and read back through the real device;
the Esc/F1 check proves that host-supplied values reach the physical LEDs.
The original Windows plugin measured about 14 FPS; host-side batching reached
17–18 FPS. The new **PKRG v2 frame-streaming firmware** was flashed with user
approval and verified over real wired USB on 2026-09-26. Native Linux frame
delivery averaged 13.96 ms in a 120-frame sample; this is not Windows FPS or
physical LED refresh. It requires matching updated plugins/host tools.
SignalRGB 2.5.54+e862907d subsequently reported 24.67 ms work and 33.36 ms gap
for 120 frames, with no meaningful throughput gain. The user's native Windows
HIDAPI benchmark then averaged **24.983 ms per transfer**, also outside SignalRGB;
the dominant write cost therefore persists without the plugin. The separate
33 ms SignalRGB gap remains a constraint. See the
[investigation wrap-up](#performance-investigation-wrap-up-2026-09-26) for evidence,
hypotheses, and remaining questions. Physical appearance/typing, complete key
mapping, sustained performance, and wireless remain unverified for PKRG v2.
RGB data remains volatile.

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

## Original working USB patch design (PKRG v1, historical)

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

The shared corpus at `sdk/conformance/pkrg-v1.json` is the cross-language
observable contract.

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

## Frame streaming (PKRG v2)

The current builder emits `firmware_per_key_v3.bin`. It preserves the existing
32-byte VIA HID reports, USB descriptors, ordinary VIA configuration, hue fix,
and OTA interface. It does **not** change SignalRGB's frame scheduling.
Current host tools/plugins require protocol version 2 before any lighting
writes; the old `firmware_per_key_v2.bin` remains a rollback artifact, not the
target of the current builder.

### Frame ownership and activation

Both startup paths reserve and zero **608 bytes**, lowering initial SP to
`0x9FDA0`. There are two 276-byte RGB buffers at `0x9FDA0` and `0x9FEB4`.
Metadata begins at `0x9FFC8`: active-buffer offset (0 or 276), expected next
fragment, frame ID, and pending state. A private 32-byte ACK buffer occupies
`0x9FFD8..0x9FFF7`; the mode flag remains at `0x9FFFC`.

Fragments fill only the inactive buffer. A complete commit while enabled is
queued until the OEM lighting-function entry at `0xA104`, before per-LED work.
That hook switches the active offset once, then attempts the ACK through the
OEM endpoint writer at `0xCA78`. Busy/unavailable USB retains the pending ACK
for a later invocation; the hook never spins waiting for the endpoint. An
ACK means the complete RGB buffer is active, **not** that the physical LED
scanout has completed. With per-key mode disabled, commit switches immediately
and responds through the normal VIA path; no lighting callback is required.

The store splice at `0xAA3C` reads from the active buffer and preserves OEM
brightness scaling. The entry hook preserves all registers it and the USB
helper modify, then replays the displaced OEM prologue. The existing BSS,
OEM framebuffer, and USB report buffer are unchanged. This is an explicit
stack reservation, not proof of worst-case hardware stack usage; the added
reservation and firmware timing still need physical validation.

### Wire contract

Requests below are **32-byte payloads**, zero padded. HID host APIs add the
existing leading report-ID zero, making a 33-byte host buffer. Capability
GET `[8,127,0]` returns `PKRG` at bytes 4..7, version **2** at byte 8, 92 LEDs
at byte 9, configuration chunk limit 8 at byte 10, enable state at byte 11,
stream fragment capacity **9 LEDs** at byte 12, and **11 fragments** at byte 13.
Mode GET/SET and RGB chunk GET/SET retain their layouts from the historical
table; chunk accesses use the active buffer. SET mode/chunk is rejected with
status 5 while a frame commit or its ACK remains pending.

| Request | Payload | Response |
|---|---|---|
| SET 3: fragment | `[7,127,3,frame_id,index,RGB...]` | None on the wired route, including malformed fragment sequences |
| SET 4: commit | `[7,127,4,0,frame_id]` | `[7,127,4,status,frame_id,...]`; successful enabled commits reply only after activation |

Fragment indices are 0..10, in order, with the same one-byte frame ID.
Indices 0..9 carry 27 RGB bytes each; index 10 carries the final six RGB bytes.
Fragment 0 starts/restarts a staged frame. Missing, duplicate nonzero,
out-of-order, mixed-ID, or out-of-range fragments cannot complete it. Invalid
sequences stay invalid until a new fragment 0. A commit requires all 11
fragments and a matching ID. Status **4** means incomplete/invalid frame;
status **5** means an earlier commit/ACK is pending. Existing statuses 1..3
retain their meanings. Streaming operations are rejected on the wireless
firmware route; its configuration chunk operations remain available.

One changed frame uses **11 silent fragments + one COMMIT = 12 OUT reports**
and **one IN acknowledgement**. The host waits for that acknowledgement before
starting another frame; no unbounded queue or implicit retry is introduced.
Frame IDs wrap modulo 256. USB retains its own transfer error detection and
handshakes; only the extra per-fragment application responses are removed.

Wired V3 skips an entirely unchanged frame but sends a complete frame when
any color changes. This trades sparse-update bandwidth for complete-frame
activation. The Python client's partial `set` reads the active frame, replaces
the selected colors, and commits/readbacks the whole frame. Ordinary chunk
SET remains non-atomic for the experimental wireless client; do not run
multiple controllers against the interface concurrently.

## Build and use the wired patch

Build from the repository's exact v1.06 hue-patched baseline:

```sh
python3 firmware/tools/patching/patch_firmware_per_key.py
```

Outputs:

- `firmware/releases/v1.06-per-key/firmware_per_key_v3.bin`: standalone firmware, 122,196 bytes.
- `firmware/releases/v1.06-per-key/code_2M_per_key_v3.bin`: matching 2 MiB OTA wrapper.
- Firmware CRC footer: `0x5954FF66`.
- Standalone SHA-256:
  `1e23e5352c4c9780728503fe989521437c380a39046c26ae2bbd8c1119353362`.
- Injected code uses 1,132 of the verified 1,220 cave bytes.

The builder rejects other baseline images rather than guessing new offsets.
It does not connect to or flash the keyboard.

Before a flash, close other keyboard-control software and save a fresh backup
under a filename that does not overwrite an earlier backup:

```sh
python3 host/linux/via_backup.py save /path/to/pre-per-key-backup.json
python3 host/linux/flash_ota.py --dry-run firmware/releases/v1.06-per-key/firmware_per_key_v3.bin
# Only after accepting the recovery risk described above:
python3 host/linux/flash_ota.py firmware/releases/v1.06-per-key/firmware_per_key_v3.bin
python3 host/linux/via_backup.py restore /path/to/pre-per-key-backup.json
```

The restore command now compares current keycodes, restores changed entries
including `0x0000` and `0xFFFF`, and verifies every restored keymap byte. A
failed restore produces a failing exit status. Keep the original backup even
after a successful restore.

The v2 streaming image has passed wired USB flash/readback checks; physical
appearance, normal typing, and sustained Windows behavior still need confirmation.
Preserve the previous plugin file outside SignalRGB's Plugins folder and keep
`firmware/releases/v1.06-per-key/firmware_per_key_v2.bin` for rollback. A rollback
also requires the older PKRG v1 plugin/host revision; current clients reject it.
Flashing remains an explicit operator action, not part of plugin initialization.

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

Wired uploads use the complete-frame activation protocol described above.
No frame-rate guarantee has been measured for this firmware. `read` returns
supplied RGB values, before global brightness scaling; maximum firmware
brightness is 192/256 of the supplied channel value. Black remains black,
and neutral RGB values stay neutral.

The [C#/.NET SDK and safe sample](../../sdk/dotnet/README.md) provide a separate
wired per-key client. Their Windows/Linux/macOS builds and CI are hardware-free;
the Python hardware evidence in this guide does not verify the SDK on any OS.

### Automated verification

```sh
python -m venv /tmp/wobkey-per-key-tests
/tmp/wobkey-per-key-tests/bin/python -m pip install -r tests/requirements.txt
/tmp/wobkey-per-key-tests/bin/python -m unittest discover -s tests -v
```

Regressions execute generated RV32 instructions: both startup paths, full
effect-6 rendering, register preservation, configuration bounds, silent
fragments, incomplete/mixed/reordered frame rejection, deferred ACKs, USB
unavailability, frame-ID wrap, and commits arriving during an existing render.
The real host client uses that emulator for full-frame and partial updates.
VIA restore tests retain exact restoration of disabled/default keycodes.

The actual wired JavaScript was also driven against the generated RV32 handler,
activation hook, USB ACK helper, and effect-6 renderer. Three distinct frames
each used 12 OUT reports and one IN reply; all 92 raw RGB slots and all 276
scaled framebuffer bytes matched. An unchanged frame sent no reports, and
shutdown restored RGB, mode, effect, and brightness. Host USB scheduling and
OEM configuration calls were modeled; this does not establish hardware timing.

The generated standalone image reproduces exactly from the builder, its CRC
passes the OTA loader, and the OTA wrapper differs from the baseline only in
its firmware payload. The USB configuration descriptor is unchanged. The
subsequent approved hardware flash and real-device results are recorded below.

## PKRG v2 hardware verification, 2026-09-26

After explicit user approval, the OTA tool transferred all **2,546 packets**
in **18.2 seconds**, reported OTA success, and the keyboard re-enumerated.
The non-updating OEM identity request reported CRC **`0x5954FF66`**, matching
the new image. VIA remained protocol 11. PKRG discovery returned version 2,
92 slots, configuration chunks of eight, and 11 streaming fragments of nine
LEDs. Startup had the override disabled and all 92 supplied RGB slots zero.

Fresh backups were saved outside the repository:

```text
~/.local/state/wobkey/via-before-streaming-20260926T125040-f619d8fc.json
~/.local/state/wobkey/perkey-before-streaming-20260926T125040-f619d8fc.json
```

VIA backup SHA-256:
`3f48c1df0ffef3cc8c49898d10ceb1c7dad3744f8fe29bbdb325428eed60c000`.
Per-key backup SHA-256:
`b567e5f821f2d088a24eeb0017777be84f9dd6734ec87f6f0424d63b1fd24add`.
The prior image reported CRC `0xE5BE2E50` (PKRG v1).

The flash changed **31 keymap entries**. Restoration wrote those entries and
verified all **1,024 keymap bytes**. Brightness 9, effect 6, speed 2, hue 165,
and saturation 255 were restored/read back. The separately backed-up per-key
colors and disabled override state were also restored.

Real-device checks:

- A complete frame committed while disabled and read back correctly for all
  92 slots; no lighting callback was required.
- Three enabled frames committed and read back correctly.
- Omitting fragment 5 produced COMMIT status 4 and left the preceding active
  frame unchanged.
- A 120-frame native Linux hidraw sample received exactly 120 matching frame
  ACKs, with 12 OUT reports per frame and no extra replies. Four full-frame
  readback probes also matched. Mean write time was **12.78 ms**, mean ACK wait
  **1.17 ms**, and mean complete transfer **13.96 ms** (range 13.91–14.95 ms).
  These timings exclude the readback probes and any SignalRGB scheduling gap.
- The actual wired V3 JavaScript ran through a direct Linux hidraw adapter,
  not a simulated endpoint. Three distinct frames each produced **12 writes
  and one reply**, and all 92 RGB values matched. An unchanged frame performed
  no I/O. Shutdown restored the exact captured RGB/mode/effect/brightness.
- After all probes, every keymap byte, OEM lighting value, per-key color, and
  override state was checked again against the pre-flash backups and matched.

The keyboard was left with PKRG v2 installed and its prior lighting state
restored, including **override disabled**. No streaming profile was persisted.
Physical LED appearance and normal typing require user confirmation. These
Linux results do not prove Windows SignalRGB FPS, physical scanout cadence,
long-duration stability, or wireless behavior. The 30 FPS target is not yet
verified inside SignalRGB.

## Original PKRG v1 hardware verification, 2026-09-25

This section records the earlier `firmware_per_key_v2.bin` and its matching
host/plugin revision. It does not validate the new streaming image.

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
PKRG v2 streaming firmware above. It does not support the 2.4 GHz dongle or
Bluetooth and does not flash firmware. V1/V2 plugins remain available for
hue-patched firmware; do not run an older wired plugin alongside V3.

### Install

1. Back up VIA configuration before upgrading to `firmware_per_key_v3.bin`.
   The original per-key image exposes PKRG v1 and is rejected by this plugin.
2. In SignalRGB, open the Crush 80's **Device Information → Plugins** folder.
3. Move older wired custom plugins for this keyboard out of that folder.
   Renaming them while leaving a `.js` extension in the same folder is not
   sufficient: they can still compete for the device.
4. Copy `plugins/signalrgb/wired/WobkeyCrush80_v3.js` into the custom plugins folder and
   restart SignalRGB completely.
5. Use **Wobkey Crush 80 (Wired) V3 Per-Key**, enable streaming, and place it
   on the SignalRGB canvas. Adjust its size/position in the layout as needed.

The Windows installer catalog includes wired and experimental wireless V3 plugin
entries. Both now require PKRG v2; select the matching streaming image rather
than the older per-key or hue-only images. Visual and sustained runtime checks remain pending.

### Behavior

- Opens only interface 1, usage page `0xFF60`, usage `0x61`.
- Requires PKRG version 2, 92 LEDs, eight-slot configuration chunks, and the
  nine-LED/11-fragment streaming format before sending any lighting writes.
- Captures the current OEM effect, brightness, per-key enable state, and all
  92 RGB values before taking control.
- Uses direct RGB from `device.color(x, y)`: black is transmitted as black,
  white remains white, and colors are not averaged or reduced to HSV.
- Uses effect 6 and hardware brightness 9 while streaming. SignalRGB already
  applies its brightness setting to sampled colors, so V3 does not apply it
  a second time.
- Sends a complete frame only when colors change, using 11 silent fragments
  and one acknowledged COMMIT. The cache advances only after a successful
  reply matching the submitted frame ID; no unacknowledged frame is cached.
- On graceful shutdown or streaming disable, restores the captured RGB array,
  enable state, effect, and brightness. It disables per-key mode before
  restoring the RGB frame so commit does not depend on lighting callbacks.
- A transport error stops streaming. Before restoration, it waits for any
  outstanding commit reply with the correct frame ID; stale/foreign replies
  do not count as completion. Recovery is bounded by the existing 100 ms
  timeout. If the reply remains unavailable, restoration is deferred rather
  than confusing a late frame ACK with a MODE reply. Unplugged hardware may
  remain unrestorable; reconnection is not proof that an old reply will arrive.
- Does not send SAVE, OTA, wireless-control, or reset commands.

Do not run VIA, the Python RGB tool, or another RGB controller against the
same interface while SignalRGB is streaming. Transport errors are reported
in the device console; fix the conflict and toggle streaming to reinitialize.

### Windows measurements and the 30 FPS target

The user verified the original wired plugin on Windows: about **14 FPS**,
**35 ms frame work**, and **32 ms inter-frame delay**. Host-side batching on
the same PKRG v1 firmware improved the Inspector trace to **17–18 FPS**.
Five timing windows (600 full frames) then averaged **23.40 ms write**,
**1.14 ms ACK**, **24.54 ms total work**, and **33.38 ms outside Render()**.
These are measurements of the older batched plugin, not this new firmware.

The new protocol keeps 12 OUT reports per full frame but reduces firmware
responses from 12 to one. In SignalRGB **2.5.54+e862907d**, the user then supplied
a 120-frame sample: **23.40 ms write**, **1.27 ms ACK**, **24.67 ms work**, and
**33.36 ms gap**, with 11 data chunks plus COMMIT per frame. This implies about
17.23 FPS: no meaningful improvement over the earlier batching sample.
Consequently, firmware response backpressure is not supported as the dominant
Windows bottleneck. The same firmware's native Linux probe averaged 13.96 ms
per complete transfer. The subsequent native Windows HIDAPI run averaged
24.983 ms, showing that the roughly 25 ms transfer cost persists without
SignalRGB. Neither native benchmark isolates SignalRGB's outside-Render gap;
the [wrap-up below](#performance-investigation-wrap-up-2026-09-26) separates those
two constraints and records the remaining hypotheses.

To measure after an explicitly authorized firmware upgrade:

1. Back up VIA settings, install the matching PKRG v2 firmware and wired V3
   plugin, restore/read back configuration, then restart SignalRGB.
2. First check independent colors, black/white, normal typing, and restoration
   when streaming is disabled. Stop on transport errors or incorrect output.
3. Use the same animated effect/layout as the baseline. Enable **Log frame
   timing (diagnostic)**. Every 120 successful Render calls, the device console
   reports `chunks/frame`, `prepare`, `write`, `ack`, `work`, and `gap`.
   A fully changing effect now reports **11 data chunks/frame**, excluding its
   additional COMMIT report. An unchanged frame sends no reports.
4. Run for at least 60 seconds and capture the Inspector graphs plus several
   timing lines. `write` includes packing and COMMIT; `ack` includes activation
   waiting, reply validation, and cache updates. `gap` includes work/waiting
   outside Render(), not just a precisely measured thread sleep. Measurements
   use JavaScript's millisecond clock and are averaged.
5. Turn timing diagnostics off after measurement; they are off by default.

**30 FPS remains a target, not a verified result.** The total frame budget is
33.3 ms, so the measured 33.38 ms outside Render() remains an independent
constraint. This firmware does not override SignalRGB scheduling or promise
the physical LED scanout rate. Do not shorten response timeouts or discard
frame acknowledgements to improve the displayed number.

### Native Windows HID benchmark

`host/windows/benchmark_per_key_rgb.py` sends the same 11 data fragments plus
COMMIT through [HIDAPI's Python binding](https://pypi.org/project/hidapi/), outside
SignalRGB. It opens only `320F:5055`, interface 1, usage page `0xFF60`, usage
`0x61`, and requires PKRG v2 before changing lighting. It does not open the OTA
or wireless-control interface, change drivers, flash, write keymaps, or SAVE.

Use an **updated Windows copy of the repository**, not just a downloaded
plugin: the benchmark imports the existing protocol client in `host/linux/`.
Python 3.10 or newer is required. Close SignalRGB completely from its tray menu
and close VIA/other keyboard controllers. Keep the keyboard on the same wired
USB port used for the SignalRGB measurement.

Open PowerShell in the repository root and run:

```powershell
py -3 -m venv "$env:LOCALAPPDATA\WobkeyBench"
& "$env:LOCALAPPDATA\WobkeyBench\Scripts\python.exe" -m pip install --only-binary=:all: hidapi==0.15.0
& "$env:LOCALAPPDATA\WobkeyBench\Scripts\python.exe" .\host\windows\benchmark_per_key_rgb.py --frames 600
```

The script temporarily uses per-key lighting and snapshots/restores all 92
RGB values, enable state, brightness, and effect. Normal completion verifies
the restored state. Ctrl+C/errors attempt restoration, but unplugging or
force-closing the process can prevent it. Let it finish and retain any error
output. No additional firmware flash is needed.

If an older benchmark stops at `Measuring ...` before changing any lighting,
replace `host/windows/benchmark_per_key_rgb.py` with the current version.
The original buffer drain passed timeout zero to `hid.device.read`, but
[cython-hidapi 0.15.0](https://github.com/trezor/cython-hidapi/blob/0.15.0/hid.pyx)
uses the untimed `hid_read()` for zero, not `hid_read_timeout(..., 0)`.
With blocking mode enabled, the initial empty-queue drain never returned.
The current adapter uses a minimum positive **1 ms** timeout for drains;
the timed frame ACK timeout remains **100 ms**. Drains are outside the measured
frame interval. Stop the old Python process before restarting the corrected
script; this particular startup hang occurs before any lighting writes.

Output includes Windows/Python/HIDAPI versions and mean/median/p95/max for
**Write**, **ACK**, and **Total transfer**, plus restoration verification.
Ten warmup frames are excluded by default. Frame generation, setup, periodic
full RGB readback probes, and restoration are outside the timed regions;
there is no artificial inter-frame sleep. These are host transfer timings,
not SignalRGB FPS or physical LED refresh.

The benchmark CLI and restoration/failure paths were exercised against the
actual firmware instruction emulator on Linux. The user then completed the
Windows hardware run recorded below; this was not an assistant-side Windows run.

### Performance investigation wrap-up, 2026-09-26

**Status: paused with a measured bottleneck split, not a 30 FPS result.**
Host-side batching improved the original Windows plugin from about 14 FPS to
17–18 FPS. The subsequent PKRG v2 firmware provides complete-frame activation
and one commit acknowledgement, but did not materially increase Windows
SignalRGB throughput. The native Windows comparison now establishes that
the approximately 25 ms transfer cost also occurs outside SignalRGB.

#### What we investigated

1. **Original plugin and host-side batching.** The original plugin performed
   12 sequential write/acknowledgement exchanges per fully changed frame.
   Batching on PKRG v1 reduced observed frame work from about 35 ms to
   24.54 ms; the approximately 33 ms gap outside `Render()` remained.
2. **Firmware response backpressure.** PKRG v2 retained 12 OUT reports but
   replaced per-fragment replies with 11 silent fragments and one COMMIT ACK.
   Double buffering and frame-boundary activation prevent partial frames from
   becoming visible. Emulator checks and wired hardware probes exercised
   complete delivery, rejected incomplete frames, RGB readback, and restoration.
   SignalRGB still measured 24.67 ms work plus a 33.36 ms gap: removing those
   replies did not remove the dominant Windows cost.
3. **Native Linux comparison.** The same firmware accepted 120 measured
   frames at 13.96 ms mean complete-transfer time, with matching ACKs and RGB
   readback. This argues against an unavoidable 25 ms firmware transfer floor;
   it does not establish physical LED refresh or isolate host/controller differences.
4. **Native Windows comparison.** A separate HIDAPI Python CLI sent the same
   11 fragments plus COMMIT, excluding color/packet generation and readback
   from the measured interval. This tests whether the transfer cost requires
   SignalRGB, not whether every Windows backend or USB topology behaves alike.
5. **Benchmark startup hang.** The first CLI version incorrectly treated
   timeout zero as a non-blocking read. In cython-hidapi 0.15.0 it instead
   selects untimed `hid_read()`, hanging the initial empty-queue drain in
   blocking mode before lighting writes. The adapter now uses a minimum
   positive 1 ms timeout. Regression coverage models that binding behavior;
   drains remain outside frame timing and the frame ACK timeout stays 100 ms.

#### User's native Windows result

The user supplied this completed run of `python .\benchmark_per_key_rgb.py`
after the drain fix. Its console output is retained here rather than relying
on the temporary log file:

```text
Platform: Windows-11-10.0.26200-SP0 / Python 3.14.5
HIDAPI Python binding: 0.15.0
Device: 320F:5055, interface 1, usage FF60:0061
SignalRGB and VIA must be closed. Lighting will change temporarily.
Measuring 120 full frames after 10 warmup frames...

Frames: 120; 12 OUT reports + 1 ACK per frame
Full RGB readback checks: 5 (outside timed regions)
Phase (ms)            Mean    Median       P95       Max
Write               23.927    23.928    23.969    24.014
ACK                  1.056     1.032     1.064     2.084
Total transfer      24.983    24.960    24.997    26.004
Lighting restoration: verified (RGB, enable state, brightness, effect).
No artificial frame delay. These are transfer timings, not SignalRGB FPS or LED refresh.
```

All five full RGB readback checks completed, and the benchmark verified the
captured RGB values, override state, brightness, and effect after restoration.
The measured transfer time was stable in this sample, with p95 24.997 ms.
It does not establish long-duration reliability or visually verified scanout.

#### Hypotheses and disposition

| Hypothesis | Evidence and current disposition |
|---|---|
| Sequential per-chunk host exchanges cost significant time. | Supported by the initial batching improvement: about 35 ms to 24.54 ms of frame work on PKRG v1. This optimization is already implemented. |
| Firmware per-fragment reply backpressure dominates the remaining Windows cost. | Not supported as the dominant cause: PKRG v2 reduced replies from 12 to one without a meaningful Windows throughput gain. |
| SignalRGB-specific JavaScript or packet preparation dominates the approximately 25 ms transfer. | Not supported as the dominant cause: the native Windows benchmark excludes packet generation and still takes 24.983 ms, versus SignalRGB's 24.67 ms work interval. The intervals are not identical, so this is not a precise measurement of plugin overhead. |
| The final COMMIT ACK is the main bottleneck. | Not supported: native Windows ACK wait averaged 1.056 ms; writes account for 95.8% of the measured transfer. ACK time is residual waiting after writes, not an isolated firmware execution measurement. |
| Windows HID/USB scheduling, synchronous report submission/completion, the controller/hub, or device readiness under that path impose a per-report cadence. | **[INFERENCE] Plausible, not isolated.** Writes average 23.927 / 12 = 1.994 ms per report. This is an aggregate, not a per-report trace or proof of a 2 ms endpoint polling interval. Both Windows applications share lower layers. The faster Linux result does not by itself distinguish OS, backend, or topology effects. |
| SignalRGB adds a separate scheduling/work gap outside `Render()`. | The approximately 33 ms interval is measured and remains an independent constraint. Its internal cause is unresolved; `gap` includes all work/waiting outside the callback, not a proven fixed sleep. |

#### Frame budget and remaining investigation

Using the native Windows transfer mean and the previous SignalRGB gap gives
`1000 / (24.983 + 33.36) = 17.14 FPS`, consistent with the roughly 17 FPS
observations. This combines separate samples, not a new measured SignalRGB run.
The reciprocal of the transfer interval alone is approximately 40 frames/s;
that is a transfer-only budget, not observed application FPS or LED scanout.

At 30 FPS, the total budget is 33.33 ms. With a 24.983 ms transfer, everything
else must fit in approximately **8.35 ms**. Even Linux-like 13.96 ms transfers
would yield only approximately **21.13 FPS** if the 33.36 ms gap remained.

When investigation resumes:

1. Prioritize SignalRGB's outside-`Render()` scheduling/work interval, keeping
   the same animated effect and layout. Identify a supported way to reduce
   that interval rather than assuming a particular sleep or undocumented API.
2. Capture Windows USB submission/completion timing for the VIA interface
   during the native benchmark. Inspect the actual descriptors and transfer
   route, and correlate report timing with synchronous write durations before
   attributing the approximately 2 ms average to endpoint polling.
3. Control host/controller/port/hub differences in subsequent comparisons.
   Any faster transport or firmware proposal must retain complete-frame
   delivery checks and restoration, then demonstrate a measured improvement.

No USB trace or scheduling fix has been validated in this investigation.
These results do not justify another firmware redesign or flash on their own.
Shorter response timeouts or discarding the final ACK would not address the
dominant measured write cost or the independent SignalRGB gap.

#### Wrap-up verification

Pre-commit checks passed 35 Python regressions, 17 wired-plugin regressions,
and nine wireless-plugin regressions (eight wired-only cases were skipped).
The firmware builder reproduced both release binaries exactly, and the OTA
loader accepted both CRCs. The benchmark CLI completed setup, frame delivery,
readback, and restoration against the firmware emulator. These offline checks
are separate from the user's Windows hardware timing above.

The Windows installer cross-built on Linux with zero warnings/errors; its
packaged PKRG v2 firmware and both V3 plugin hashes matched the catalog.
The Windows GUI was not exercised. A broader packaging check also found
stale catalog hashes for the four legacy V1/V2 plugins. Each mismatch was
already present in `d4e1b6d`, with both the plugin source and its catalog hash
unchanged by this work. That unrelated metadata issue was left unchanged,
not counted as a passing integrity check. No keyboard was opened or flashed
during wrap-up.

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

JavaScript regressions cover independent black/white/RGB rendering,
incompatible-firmware rejection without writes, restoration of prior OEM and
per-key states, unchanged-frame skipping, functional key bindings/endpoint
selection, and missing or malformed replies. Wired-specific cases cover lost
fragments, stale/late/unavailable frame ACKs, frame-ID wrap, old-protocol
rejection, and failed fragment writes. Node emits its expected experimental-VM warning.

Before batching was introduced, the original V3 JavaScript was also executed
through a temporary native-HID bridge against the connected keyboard:

- Three distinct canvas frames each produced correct readback for all 92
  supplied RGB values, using 12 RGB packets per changed full frame.
- Repeating an unchanged frame sent zero RGB packets.
- `Shutdown()` restored the complete previous RGB frame, enable state,
  effect, and brightness; all were read back and matched.
- The user's original colors/settings were restored after the mapping and
  lifecycle checks.

**The Windows SignalRGB application itself was not run in this environment.**
The user supplied real measurements for both the original sequential plugin
and the host-batched PKRG v1 plugin. PKRG v2 has real Linux USB frame and
plugin-lifecycle verification, plus the user's 120-frame Windows timing sample
above. Visual/typing confirmation and sustained runtime checks remain outstanding.

Primary API references:

- [SignalRGB plugin lifecycle and exports](https://docs.signalrgb.com/developer/plugins/)
- [HID writes, reads, actual read size, and buffer clearing](https://docs.signalrgb.com/developer/plugins/writes-and-reads/)
- [Canvas sampling and brightness semantics](https://docs.signalrgb.com/developer/plugins/utilities/)
- [Functional keyboard names](https://docs.signalrgb.com/developer/plugins/key-names/)
- [Installing a custom plugin](https://docs.signalrgb.com/troubleshooting/advanced-troubleshooting/replacing-plugin/)

## SignalRGB Wireless V3: experimental 2.4 GHz variant

`plugins/signalrgb/wireless/WobkeyCrush80Wireless_v3.js` targets the **2.4 GHz dongle** at
VID:PID `320F:5088`, interface 1, usage page `0xFF60`, usage `0x61`.
It uses the PKRG v2 configuration operations, the same layout, and acknowledged
eight-slot RGB chunks with state restoration. It **does not use** the wired
silent-fragment/commit commands, which firmware rejects on the wireless route.
It is a standalone plugin; no shared JavaScript module is needed.

### Install and try

1. This plugin revision requires `firmware_per_key_v3.bin` (PKRG v2) on the
   **keyboard**. Do not install the image on the dongle. Hardware support is unverified.
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

The shared lifecycle/protocol checks run against the wireless endpoint model;
wired-only streaming checks are skipped for this variant. Its packet flow
remains sequential. The capability requirement was updated to PKRG v2; this
does not constitute new evidence of radio forwarding or wireless operation.

No `320F:5088` dongle was available to this environment, so these checks are
**not** proof of radio forwarding, over-air throughput, or wireless operation
inside SignalRGB. No device lighting or firmware was changed for these checks.
