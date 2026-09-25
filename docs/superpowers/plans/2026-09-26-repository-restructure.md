# Repository Restructuring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reorganize the repository by deliverable and responsibility while preserving supported commands, installer payloads, firmware bytes, user changes, and local data.

**Architecture:** Keep one repository. Move Linux tools, Windows installer, plugins, hardware configuration, firmware sources/releases, and reverse-engineering material into the approved homes. Update each path consumer in the same task as its files move; finish by reconciling docs and repository ignore rules.

**Tech Stack:** Python 3, unittest, Node.js `node:test`, .NET 10 WPF, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-26-repository-structure-design.md`

## Global Constraints

- Preserve exact firmware release/source bytes; do not reflash hardware or change plugin/protocol behavior.
- Preserve all pre-existing local changes and unknown/untracked data; specifically retain the edits to `docs/PER-KEY-RGB.md` and `tests/test_signalrgb_v3.mjs`.
- Keep `via_backup.py`'s default backup filename relative to the caller's current working directory.
- Keep SignalRGB installation destination filenames and the installer's output layout stable.
- Do not add tests whose only assertion is that a file moved or a path string changed. Use existing behavior suites and smoke the real supported entry points.

---

### Task 1: Establish baseline and relocate firmware, hardware, and device tools

**Files:**
- Move device-facing scripts `flash_ota.py`, `per_key_rgb.py`, `via_backup.py`, `via_debug.py`, and `via_dump_raw.py` to `host/linux/`.
- Move stock inputs to `firmware/sources/`, vendor updater executables to `firmware/vendor/`, and patched firmware to the three versioned `firmware/releases/` folders according to verified provenance.
- Move patch builders `patch_firmware.py`, `patch_firmware_v2.py`, `patch_firmware_per_key.py`, and the local `patch_firmware_v3.py` to `firmware/tools/patching/`.
- Move `firmware/Crush80-RGB-USB.JSON` and tracked `via_config.json` to `hardware/layouts/`; move the udev rule to `hardware/udev/`.
- Update tests, builder/tool defaults, and direct-user path references affected by these moves.

**Interfaces:**
- Preserve direct script execution as `python3 host/linux/<tool>.py ...` and `python3 firmware/tools/patching/<builder>.py ...`.
- Keep `via_backup.py`'s backup path relative to the caller's current working directory.
- Preserve exact input/release bytes; change only paths and generated-output destinations.

- [ ] **Step 1: Run baseline behavior suites before moving files**

Run: `python3 -m unittest discover -s tests -v`
Expected: existing unittest suite passes; record any environment/dependency failure before proceeding.

Run: `node --test tests/test_signalrgb_v3.mjs`
Expected: existing plugin behavior tests pass.

- [ ] **Step 2: Verify firmware provenance before classifying release files**

Use `sha256sum` on current files and compare against installer catalog and `docs/PER-KEY-RGB.md`. Classify `firmware_patched.bin`/`code_2M_patched.bin` as v1.04 only if matching catalog provenance; classify `v2_patched.bin`/`code_2M_v2_patched.bin` as v1.06; classify the `*_per_key_v2.bin` pair as v1.06-per-key. Do not move any artifact whose provenance is contradicted by its hash or docs until resolved from repository evidence.

- [ ] **Step 3: Move classified firmware, source, vendor, and hardware configuration files**

Keep source binary basenames unchanged. Move only firmware inputs/release outputs with verified provenance; keep the VIA JSON and udev rule at their specified hardware paths.

- [ ] **Step 4: Fix device and patch-builder path defaults**

Update device tools' repository-root calculations for `host/linux/`. Update patch builders' repository-root calculations and defaults to read `firmware/sources/` and write `firmware/releases/`. Keep direct-script imports working and `via_backup.py`'s current-working-directory backup semantics unchanged.

- [ ] **Step 5: Update tests and Linux-user references**

Update test imports to load the host client from `host/linux/`, the patch builder from `firmware/tools/patching/`, and the firmware fixture from `firmware/releases/v1.06/v2_patched.bin`. Keep behavior assertions unchanged.

- [ ] **Step 6: Run the relocated Linux smoke checks**

Run: `python3 -m unittest discover -s tests -v`
Run: `python3 host/linux/flash_ota.py --help`
Run: `python3 host/linux/per_key_rgb.py --help`
Expected: tests pass and help commands exit without opening a device.

### Task 2: Relocate plugins and VIA browser tool

**Files:**
- Move tracked `SignalRGB/WobkeyCrush80*.js` variants to `plugins/signalrgb/wired/` or `plugins/signalrgb/wireless/` based on wired/dongle target.
- Move `SignalRGB/via-test.html` to `plugins/tools/via-test.html`.
- Update `tests/test_signalrgb_v3.mjs`, installer catalog source references, project content links, and user docs.

**Interfaces:**
- Preserve destination plugin basenames used by SignalRGB and the installer.
- Preserve wired and wireless device distinctions and plugin contents.

- [ ] **Step 1: Move plugin files into wired/wireless directories**

Keep v1/v2/v3 variants and filenames intact. Do not modify plugin logic.

- [ ] **Step 2: Update plugin test default discovery**

Change the test's default wired plugin path to `plugins/signalrgb/wired/WobkeyCrush80_v3.js`; preserve `SIGNALRGB_TEST_PLUGIN` override behavior.

- [ ] **Step 3: Move the WebHID tool and update user-facing links**

Move the HTML tool under `plugins/tools/` and update all tracked instructions that link to it.

- [ ] **Step 4: Run SignalRGB behavior suite**

Run: `node --test tests/test_signalrgb_v3.mjs`
Expected: all behavior tests pass against the moved default plugin.

### Task 3: Flatten installer and preserve distribution contract

**Files:**
- Move `installer/Crush80FirmwareInstaller/Crush80FirmwareInstaller.sln` and the inner project source files/catalog to `host/windows/Crush80FirmwareInstaller/` (solution and `.csproj` directly in this directory; remove duplicated project-name nesting).
- Update `Crush80FirmwareInstaller.csproj`, `firmware-catalog.json`, `installer-tag-artifact.yml`, and README run/build instructions.
- Replace the nested `.gitignore` with root-level ignore rules in Task 4 after confirming equivalent build coverage.

**Interfaces:**
- Installer catalog still resolves firmware under output `firmware/` and plugins under output `SignalRGB/`.
- Keep the same firmware/plugin payload basenames, SHA-256 values, catalog IDs, and published artifact layout.

- [ ] **Step 1: Move solution/project sources to the flattened Windows host directory**

Move WPF sources, manifest, catalog, solution, and project together; do not move generated `bin/` or `obj/` artifacts.

- [ ] **Step 2: Update project links and catalog source paths**

Update `.csproj` source `Include` paths to `firmware/releases/...` and `plugins/signalrgb/{wired,wireless}/...`. Keep output `Link` names (`firmware/...`, `SignalRGB/...`) and all catalog `file` and `destinationFile` values unchanged, because those paths describe the stable installer output layout. Validate each catalog path has a corresponding staged file.

- [ ] **Step 3: Update the release workflow project path**

Set the workflow `PROJECT` to `host/windows/Crush80FirmwareInstaller/Crush80FirmwareInstaller.csproj`; keep runtime, artifact naming, and publish directory behavior unchanged.

- [ ] **Step 4: Validate installer metadata and build**

On Linux, validate every project `Include` path and catalog source resolves to a file and run `dotnet publish` only if the .NET 10 WPF targeting environment supports it. If not available, state that platform limitation and validate the Windows workflow's path and project metadata without claiming a Windows build.

### Task 4: Organize research/docs, add root ignore rules, and reconcile entry points

**Files:**
- Move tracked `INVESTIGATION.md` and `TECHNICAL_REPORT.md` to `research/docs/`; `v2_update.md` to `research/notes/`; `analyze_firmware.py`, `disasm_targets.py`, `disasm_via.py`, `extract_firmware.py`, `extract_fw_code.py`, `extract_gpio_pins.py`, `ghidra_analyze.py`, and `GhidraHSV.java` to `research/tools/`; move `_old_scripts/Ghidra*.java` to `research/tools/legacy/`.
- Move tracked `decompiled/` source/resource project to `research/vendor-flasher/`.
- Move `docs/PER-KEY-RGB.md` to `docs/user/PER-KEY-RGB.md`, preserving its current user edits.
- Add root `.gitignore`; remove tracked Python bytecode caches only. Leave unknown local/untracked paths and data untouched.
- Rewrite root `README.md` structure and commands to match the final paths; update all tracked Markdown links and examples.

**Interfaces:**
- Keep technical findings and unmodified vendor source content intact.
- Exclude only known generated caches/build outputs; preserve `tools/`, `images/`, `.claude/`, `.omx/`, local JSON backups, `issue.md`, PCB files, and `scripts/diag/` untouched.

- [ ] **Step 1: Move research code/resources and documentation**

Keep the decompiled `.resx` and its C# project together. Update the extraction script's explicit source/output paths. Preserve tracked Ghidra legacy files rather than deleting them. Preserve existing edits in the per-key guide while moving it.

- [ ] **Step 2: Add root `.gitignore` and remove tracked bytecode caches**

Ignore `__pycache__/`, `*.py[cod]`, Python virtual environments, .NET `bin/`, `obj/`, and standard local build output. Confirm the root policy applies to both installer and decompiled projects before removing the nested installer `.gitignore`. Remove only tracked Python bytecode caches; do not delete untracked caches or local artifacts.

- [ ] **Step 3: Update extraction, research, and user documentation paths**

Update internal Ghidra/resource paths where repository-relative references occur. Update `README.md`, `docs/user/PER-KEY-RGB.md`, research docs/notes links, test commands, install commands, firmware build/flash examples, plugin install paths, installer invocation, and udev setup. Keep safety warnings and verification status unchanged.

- [ ] **Step 4: Run repository-wide path and artifact checks**

Search tracked source/docs for stale old roots (`scripts/`, `SignalRGB/`, old installer path, old firmware root paths, `decompiled/`, `_old_scripts/`). Inspect remaining matches and allow only references that describe historical paths. Confirm no tracked `__pycache__`, `bin/`, or `obj/` outputs remain.

### Task 5: End-to-end smoke verification

**Files:** No additional source changes expected; fix any path mismatch in the owning task before completion.

- [ ] **Step 1: Run all available behavior tests**

Run: `python3 -m unittest discover -s tests -v`
Run: `node --test tests/test_signalrgb_v3.mjs`
Expected: all tests pass after path relocation.

- [ ] **Step 2: Verify firmware builders without replacing releases**

Build each currently reproducible patched image to a temporary output directory using the relocated tools and compare byte-for-byte with the corresponding checked-in release artifact. Do not overwrite existing tracked release files. Report any builder whose inputs are absent or whose historical output cannot be reproduced; do not invent a passing result.

- [ ] **Step 3: Smoke supported non-hardware entry points**

Run the relocated tool `--help` commands and OTA dry-run against an existing release image. Do not send device HID traffic or flash firmware.

- [ ] **Step 4: Validate installer release layout**

Inspect output/package files if `dotnet publish` is supported locally; otherwise validate catalog, project item links, and workflow project path statically. Preserve the distinction between static validation and an actual Windows publish.

- [ ] **Step 5: Confirm workspace preservation**

Review changed paths for accidental edits to pre-existing user changes/local files; confirm existing in-progress test and guide modifications remain intact, and no unknown untracked assets were removed or relocated.
