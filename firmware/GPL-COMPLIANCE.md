# Firmware licensing and QMK GPLv2 compliance

## Current status

QMK identifies WOBKEY as distributing tri-mode firmware based on QMK without the complete corresponding source. QMK's guidance states that GPLv2 compliance requires the fully featured source for the as-shipped firmware, including wireless functionality—not a wired-only subset, VIA JSON, extracted binary, decompilation, or binary patch.

This repository currently contains extracted Wobkey firmware, patched firmware images, OTA wrappers, and vendor updater executables. It does **not** contain the complete corresponding source code for those firmware binaries. The binary patch builders reproduce community modifications, but binary patch scripts are not the firmware's preferred form for modification and do not replace the missing corresponding source required by GPLv2 section 3.

Therefore:

- The firmware binaries in `firmware/sources/`, `firmware/releases/`, and `firmware/vendor/` must not be represented as GPL-compliant distributions.
- Adding GPL text to a binary does not cure the missing-source violation.
- This repository cannot grant rights in Wobkey's original firmware or vendor updater binaries that their copyright holders have not supplied.
- The repository owner has chosen to retain these binaries temporarily for research, recovery, and interoperability while the source issue is unresolved. That choice is transparent but **does not constitute GPLv2 compliance**.
- Historical Git objects containing the binaries remain available. History remediation is deferred pending legal or QMK guidance.

Primary references:

- [QMK License Violations](https://docs.qmk.fm/license_violations)
- [QMK Firmware GPLv2 license](https://github.com/qmk/qmk_firmware/blob/master/LICENSE)

## Community-authored patch tooling

The Python programs in `firmware/tools/patching/` are licensed under **GPL-2.0-only**. See [`firmware/tools/patching/LICENSE.txt`](tools/patching/LICENSE.txt). This license applies to the community-authored patch tooling and does not claim ownership of, or relicense, Wobkey's original firmware.

Outputs produced by applying these tools to QMK-derived firmware remain subject to the upstream GPL obligations. Distributing those outputs requires complete corresponding source for the full firmware.

## Requirements for actual remediation

Before patched firmware binaries can be distributed as GPLv2-compliant releases, the distribution needs complete corresponding machine-readable source for the full as-shipped firmware, including at least:

1. All keyboard, matrix, lighting, USB/VIA, Bluetooth, and 2.4 GHz wireless source modules.
2. Board definitions, configuration headers, keymaps, linker scripts, and interface definitions.
3. Any required GPL-compatible libraries or source for linked components.
4. Build scripts, toolchain configuration, and installation/packaging scripts needed to produce the executable firmware.
5. Existing copyright and license notices, plus prominent notices for community modifications and their dates.
6. Equivalent access to the source wherever firmware object code is offered.

A functionally equivalent source release may satisfy QMK's stated remediation practice, but QMK may request exact source and vendors must retain it. Obtain confirmation from QMK maintainers or qualified legal counsel before claiming remediation.

## Distribution policy while source is unavailable

- Do not describe the retained firmware binaries as open source or GPL-compliant.
- Do not publish new firmware binary releases without corresponding source.
- Prefer distributing research notes and GPLv2 patch tooling separately from firmware binaries.
- Request the complete source from Wobkey and direct vendor-license questions to Wobkey.

This document is an engineering compliance record, not legal advice.
