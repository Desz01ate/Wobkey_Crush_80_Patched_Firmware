# Wobkey Crush 80: per-key RGB

## Conclusion

**Independent per-key color output is demonstrated on the physical keyboard.**
A diagnostic patch made **Esc red** and **F1 green** simultaneously. The user
confirmed both colors on 2026-09-25.

This proves the firmware-to-LED rendering path. It does **not** by itself prove
host-supplied RGB updates, streaming performance, complete physical key mapping,
wireless operation, or persistence across power cycles.

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
| Baseline image | `firmware/v2_patched.bin`, 122,196 bytes |
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

Addresses below are **offsets in `firmware/v2_patched.bin`**, unless a full
runtime address is given. Code and constant-table references use the
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
- `scripts/flash_ota.py --dry-run` accepted the image and CRC.
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

The existing `scripts/via_backup.py` JSON format was used. The diagnostic
session's durable backup is outside the repository:

```text
~/.local/state/wobkey/via-before-perkey-20260925.json
```

Its SHA-256 is
`44ff299bab4d9fb5f888b8ccaaf7b384d76c2e916db643a95c8bb73ca24abc94`.

Do not assume skipped `0x0000` or `0xFFFF` keycodes are always correct after
flashing; restore changed entries and verify the complete result.

The known prior image is `firmware/v2_patched.bin`. A forced bootloader-entry
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
