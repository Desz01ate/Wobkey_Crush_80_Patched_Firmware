# Crush 80 Firmware v2 Analysis

Comparison of `firmware/vendor/v1.exe` (v1.04) vs `firmware/vendor/v2.exe` (v1.06).

Both are .NET PE32 OTA flasher apps. The C# source code and `param_128K.bin` are **identical** between versions. Only `code_2M.bin` (the actual firmware) differs.

## Version Info

| Field | v1 | v2 |
|-------|----|----|
| bcdDevice | 0x0104 (1.04) | 0x0106 (1.06) |
| Firmware size | 121,332 bytes | 122,196 bytes |
| Delta | — | +864 bytes |
| Disassembly lines | 35,612 | 35,850 |
| Instructions | 35,479 | 35,717 (+238) |

VID/PID unchanged: `0x320F` / `0x5055`.

## What's Identical

- **Keymaps**: All 3 layers (base, FN, FN2) are byte-for-byte identical.
- **USB descriptors**: Same HID report descriptors, same endpoint config.
- **VIA protocol handler**: Same command dispatch, same GET/SET logic.
- **Matrix scan**: Same 16x6 scan routine, same pin assignments.
- **LED driver (AW20216S)**: Same SPI init and LED update code.
- **Data section**: Differs only in 3 version bytes + 4 CRC bytes.
- **OTA flasher C# code**: Identical between v1.exe and v2.exe.
- **param_128K.bin**: Identical (same encryption key, VID/PID, etc.).

## What Changed: Telink BLE SDK ROM Update

The primary change is that v2 was compiled against a **newer version of the Telink B91 BLE SDK ROM**. The firmware calls functions baked into the chip's mask ROM at addresses `0xE000xxxx`. These addresses shifted between SDK versions.

### ROM Address Shifts

6 ROM addresses are unchanged between versions (low-level boot/init at `0xE0000000`–`0xE0001204`).

31 ROM functions shifted to new addresses:

| Region | Shift | Example |
|--------|-------|---------|
| `0xE000113C`–`0xE0003484` | +0x240 to +0x250 | `0xE00012C8` → `0xE0001508` (42 calls) |
| `0xE0004B2C`–`0xE0005FF0` | +0x254 | `0xE00050E4` → `0xE0005338` (7 calls) |
| `0xE0006A10`–`0xE0007E94` | +0x184 | `0xE0007058` → `0xE00071DC` (42 calls) |
| `0xE0008800`–`0xE0008902` | +0x200 | Boot config area |

### New ROM Functions in v2

10 ROM addresses appear in v2 that have no v1 equivalent:

| Address | Refs | Context |
|---------|------|---------|
| `0xE00012CC` | 4 | New radio config helper |
| `0xE00014E4` | 11 | RF channel control (was 10-ref `0xE00012A4` in v1, gained 1 extra caller) |
| `0xE0001524` | 6 | **New**: consolidated RF/IRQ configuration, replaces separate calls to `0xE00065B0` and `0xE0001374` in v1 |
| `0xE00015FC` | 2 | **New**: radio disconnect cleanup |
| `0xE000546C` | 2 | New BLE stack helper |
| `0xE00057E0` | 2 | New BLE stack helper |
| `0xE0005AF0` | 2 | New BLE stack helper |
| `0xE0005C24` | 2 | New BLE stack helper |
| `0xE0006804` | 4 | New radio state helper |
| `0xE00078C0` | 2 | New radio state helper |

Total ROM references: 206 (v1) → 215 (v2), unique ROM functions: 46 (v1) → 47 (v2).

## Key Code Change: Radio Disconnect Handler

The most significant functional change is in the **BLE/2.4G radio disconnect handler**.

### v1 handler at `0x8A7C` (simple, 10 instructions):
```asm
8a7c:  lui    x13, 0x80140
8a80:  lb     x15, 322(x13)     ; read radio status register
8a84:  li     x14, 1
8a86:  andi   x15, x15, 127     ; clear top bit
8a8a:  sb     x15, 322(x13)     ; write back
8a8e:  sbgp   x14, 740          ; set disconnect flag
8a92:  sbgp   x14, 744          ; set cleanup flag
8a96:  ret
```

### v2 handler at `0xD774` (expanded, 16 instructions + 2 ROM calls):
```asm
d774:  addi   x2, x2, -16       ; allocate stack frame
d776:  lui    x14, 0x80140
d77a:  sw     x1, 12(x2)        ; save return address
d77c:  sw     x8, 8(x2)         ; save callee-saved reg
d77e:  lb     x15, 322(x14)     ; read radio status register
d782:  li     x8, 1
d784:  andi   x15, x15, 127     ; clear top bit
d788:  sb     x15, 322(x14)     ; write back
d78c:  sbgp   x8, 740           ; set disconnect flag
d790:  call   0xE00015FC         ; *** NEW: ROM radio cleanup ***
d798:  li     x10, 15
d79c:  call   0xE00014E4         ; *** NEW: ROM RF channel reset (arg=15) ***
d7a4:  sbgp   x8, 744           ; set cleanup flag
d7a8:  lw     x1, 12(x2)        ; restore return address
d7aa:  lw     x8, 8(x2)         ; restore callee-saved reg
d7ac:  addi   x2, x2, 16        ; deallocate stack
d7ae:  ret
```

v2 adds two ROM calls between setting the disconnect flag and the cleanup flag:
1. `0xE00015FC` — radio state cleanup (no args)
2. `0xE00014E4` with arg `15` — RF channel/power reset

This is the fix for a **BLE/2.4G radio stability issue**: v1 would signal disconnect and immediately mark cleanup done, potentially leaving the radio in an inconsistent state. v2 properly calls into the ROM to reset the radio hardware before marking cleanup complete.

## HUE Bug: Still Present in v2

The VIA SET handler for command `0x83` (HSV Color) has the same bug in v2.

### v2 bug location at `0xDD58`–`0xDD76`:
```asm
dd58:  li     x13, 131          ; VIA cmd 0x83 (COLOR)
dd5c:  bne    x14, x13, d930    ; skip if not COLOR
dd60:  lbu    x12, 2(x9)        ; H value from VIA packet
dd64:  lui    x14, 0x2001c
dd68:  lea.h  x13, x12, x12     ; index = H * 2
dd6c:  addi   x14, x14, -620    ; base = 0x2001BD94 (stale RGB table)
dd70:  add    x14, x14, x13     ; addr = base + index
dd72:  lb     x13, 3(x9)        ; load next VIA byte
dd76:  j      0xDBF4            ; jump to store handler
```

At `0xDBF4`, the store handler writes the H byte to `state[9]` and then loads RGB from the precomputed table at `0x2001BD94` into `state[6..8]`, instead of performing a live HSV→RGB conversion. This means:

- **SET with H value**: stores H correctly, but R/G/B come from a stale lookup table that was populated at boot
- **GET after SET**: returns whatever stale R/G/B was in the table, not the correct conversion of the new H value
- **Visual effect**: LED color doesn't update to match the hue you set via VIA

The addresses shifted (v1: table at `0x2001BA2C`, store at `0xD8D0`; v2: table at `0x2001BD94`, store at `0xDBF4`) but the logic is identical. The bug was not fixed.

## Summary

v1.04 → v1.06 is a **Telink BLE SDK ROM version update** that:
1. Shifts ROM function addresses throughout (new SDK ROM image on chip)
2. Adds proper radio cleanup on BLE/2.4G disconnect (the actual bugfix)
3. Consolidates some RF configuration calls into a new unified ROM function
4. Adds ~864 bytes of code (mostly updated SDK wrapper functions)

The update targets **wireless connection stability** — specifically, ensuring the radio hardware is properly reset when a BLE or 2.4G connection drops. No changes to keymaps, USB, VIA protocol, LED control, or matrix scanning. The HUE color bug remains unfixed.
