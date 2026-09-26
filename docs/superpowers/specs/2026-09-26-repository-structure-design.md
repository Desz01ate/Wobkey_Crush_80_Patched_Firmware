# Wobkey repository structure design

## Problem

The repository has four distinct deliverables—patched firmware, Linux host utilities, Windows installer, and SignalRGB plugins—mixed with reverse-engineering sources, user documentation, test files, local configuration backups, and generated Python/.NET artifacts. `README.md` describes a prior layout. Moving files without coordinating code defaults, test paths, installer packaging, and the tagged-release workflow would break supported workflows.

## Goals

- Give user-facing tools, plugins, hardware configuration, firmware inputs/releases, and research distinct canonical homes.
- Preserve firmware inputs and release artifacts needed for recovery, reproduction, and installer distribution.
- Update every tracked consumer and documented command in the same migration.
- Keep generated caches/build products out of the tracked tree and add a root-level ignore policy.
- Preserve pre-existing workspace changes and local/untracked data; do not delete or publish device backups, private tool configuration, or unknown local assets as part of this migration.

## Target layout

```text
README.md
.gitignore
.github/workflows/installer-tag-artifact.yml
docs/
  user/                  # installation and per-key operating guides
host/
  linux/                 # Python HID/OTA/VIA tools
  windows/
    Crush80FirmwareInstaller/  # solution, project, and WPF source
plugins/
  signalrgb/
    wired/                # wired plugin variants
    wireless/             # dongle plugin variants
  tools/via-test.html
hardware/
  layouts/                # keyboard layouts and VIA JSON definitions
  udev/                   # Linux udev rule
firmware/
  sources/                # extracted stock images and OTA parameters
  vendor/                 # original vendor updater executables
  releases/
    v1.04/                # legacy patched standalone/OTA images
    v1.06/                # hue-patched standalone/OTA images
    v1.06-per-key/        # per-key standalone/OTA images
  tools/patching/         # reproducible binary patch builders
research/
  docs/                   # investigation and detailed technical report
  vendor-flasher/         # tracked decompiled flasher source/resources
  tools/                  # extraction, disassembly, and analysis utilities
  notes/                  # version comparison and other engineering notes
tests/
```

The installer remains a single project, flattened to `host/windows/Crush80FirmwareInstaller/`; do not preserve the current duplicated project-name directory nesting. SignalRGB wired and wireless variants stay separate because their device IDs and support status differ. Existing file basenames remain unchanged during the first move unless a path collision requires a rename.

The tracked `via_config.json` becomes a sample/config reference under `hardware/layouts/`. Keep `via_backup.py`'s default filename relative to the caller's working directory: it is a local backup convention, not an instruction to restore the checked-in sample. Document this distinction and do not silently change backup/restore semantics.

## File migration and behavior changes

- Move device-facing Python utilities from `scripts/` into `host/linux/`, preserving imports among `per_key_rgb.py` and `via_backup.py`. Move binary patch builders into `firmware/tools/patching/`; move extraction, disassembly, and static analysis scripts into `research/tools/`. Update root-relative inputs/outputs and every test/documentation reference.
- Move the installer project and solution to the flattened Windows host-tool directory. Update its project resource links, embedded catalog paths, `.gitignore` placement/removal, and the GitHub Actions project path. The published output must still contain the same catalog, firmware files, and plugin files at the locations the application expects.
- Move all tracked SignalRGB plugin files under `plugins/signalrgb/wired/` or `wireless/`; update tests, installer packaging entries, README guidance, and catalog source paths. Keep `destinationFile` values unchanged so SignalRGB's install filenames do not change.
- Move the VIA browser tool to `plugins/tools/` and update documentation links.
- Move tracked `via_config.json` and `Crush80-RGB-USB.JSON` to `hardware/layouts/`; move the udev rule to `hardware/udev/` and update the install command.
- Move original updater executables to `firmware/vendor/`, extracted stock images and parameters to `firmware/sources/`, and patched binaries to versioned `firmware/releases/` directories. Preserve exact bytes and filenames. Update patch-builder defaults, test fixture paths, installer catalog `file` entries, and user documentation. Do not regenerate or alter firmware binaries during this change.
- Move tracked decompiler output and resource files together under `research/vendor-flasher/`; adjust the extraction script to resolve the `.resx` from its own location and write outputs to an explicit firmware source directory. Move Ghidra and other analysis utilities under `research/tools/`, technical writeups under `research/docs/`, and the version update note under `research/notes/`. Move the per-key guide to `docs/user/`.
- Remove only tracked Python bytecode caches as generated artifacts. Add a root `.gitignore` for Python caches, virtual environments, .NET `bin/`/`obj/`, and standard local build outputs. Keep the nested .NET ignore rules only if still needed; prefer one root policy after validating it covers the project.
- Preserve pre-existing untracked/ignored data, including `.omx/`, `.claude/`, `tools/`, `images/`, local Python diagnostics, root JSON backups, `issue.md`, PCB markup/images, and unrelated local changes. The untracked `scripts/patch_firmware_v3.py` is a firmware patch builder: move it into `firmware/tools/patching/` and preserve its contents. Preserve the in-progress tracked edits to `docs/PER-KEY-RGB.md` and `tests/test_signalrgb_v3.mjs` while relocating/updating them.

## Non-goals

- No firmware changes, reflashing, plugin behavior changes, protocol changes, or edits to firmware release bytes.
- No new packaging framework, Python package, workspace, repository split, or plugin/runtime abstraction.
- No deletion of vendor source, firmware history, or local data based only on filenames.

## Verification

- Run existing Python firmware/host/VIA restore tests and SignalRGB plugin tests using their relocated paths.
- Run patch-builder output checks against current tracked release images; compare generated bytes/hashes to the existing artifacts without replacing them.
- Run the installer publish/build path if a .NET 10 toolchain is available, and inspect its output for the same catalog, firmware, and plugin filenames. If the environment cannot run Windows WPF, verify project/resource paths and the Windows workflow configuration without claiming a Windows build.
- Exercise Linux CLI `--help`/dry-run paths that do not access hardware; no device flashing or HID writes.
- Check all docs, scripts, tests, catalog paths, project content links, and workflow paths against the target tree.

## Risks and mitigations

- Firmware and plugin paths are consumed by multiple scripts and installer metadata. Use a complete reference update and focused smoke checks before considering moves complete.
- Existing user edits and local-only assets are present. Preserve them; restrict the cleanup to clearly generated tracked caches and tracked project files.
- Version labels and source-image provenance are not uniformly encoded in filenames. Group artifacts by documented provenance and verify SHA-256 values before moving; do not infer a release mapping from names alone.
- Windows WPF cannot be fully validated on Linux. Keep the workflow and project metadata aligned and report that platform limitation explicitly if applicable.
