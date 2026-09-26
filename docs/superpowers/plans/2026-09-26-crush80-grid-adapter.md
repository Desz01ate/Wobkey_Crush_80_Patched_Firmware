# Crush 80 grid adapter implementation plan

**Goal:** Deliver `Wobkey.Crush80.Adapter` as a separate .NET 10 package with a provisional sparse keyboard grid, enum-key edits, black-on-open initialization, explicit `ApplyAsync`, and SDK-backed restoration.

**Approved design:** `docs/superpowers/specs/2026-09-26-crush80-grid-adapter-design.md`.

**Boundary:** The adapter references `Wobkey.Crush80.Sdk`. The SDK alone owns HID discovery, firmware negotiation, serialized RGB writes, state capture, and restoration. No effects engine, custom layout, string-key API, wireless support, or firmware changes.

**Workflow:** Write a behavior test before each behavioral change; observe it fail, implement, then pass. Prefer the existing SDK fake firmware transport over mocks of forwarding calls. Do not run a real-device write without explicit authorization. Run the complete offline .NET solution tests and an executable adapter smoke after integration.

## Task 1 — Package and test harness

**Files:** Create `sdk/dotnet/adapters/Wobkey.Crush80.Adapter/Wobkey.Crush80.Adapter.csproj`, `sdk/dotnet/tests/Wobkey.Crush80.Adapter.Tests/Wobkey.Crush80.Adapter.Tests.csproj`, and update `sdk/dotnet/Wobkey.Crush80.Sdk.sln`.

- [ ] Add a .NET 10 adapter library with nullable reference types, XML documentation, an SDK project reference, and package metadata/licensing consistent with the existing SDK. Reference the existing `sdk/dotnet/LICENSE.txt`; do not introduce another license or bundled SDK implementation.
- [ ] Add an xUnit test project mirroring SDK test dependency versions. Reuse the existing `FakeFirmwareTransport.cs` by linking its source into this separate test assembly, rather than cloning a second firmware emulator. Link under its existing namespace. Add both projects to the solution.
- [ ] Establish an **internal** transport-backed open path for tests: open `Crush80RgbSession` from an injected `IHidTransport` and pass that owned session through the same acquisition path as both public overloads. Allow test access via `InternalsVisibleTo` or a comparable existing convention; do not expose a third public overload merely for testing.
- [ ] Check project restore/compile. No functional API is considered complete at this stage.

## Task 2 — Fixed, provisional key layout and in-memory grid

**Files:** Create focused layout, enum, and grid sources under `sdk/dotnet/adapters/Wobkey.Crush80.Adapter/`; create grid behavior tests under `sdk/dotnet/tests/Wobkey.Crush80.Adapter.Tests/`.

- [ ] First write failing tests for `SetKey`, `SetAt`, and `Fill` against observable frame colors: Esc slot 0, F1 slot 1, Caps Lock's two emitters, Space's three emitters, a neighboring key remaining unchanged, and last-write-wins when editing the same slot via key and coordinate. Test a gap, out-of-range coordinate, and undefined enum; each must reject the input without changing any colors.
- [ ] Implement one fixed 92-slot table derived from `plugins/signalrgb/wired/WobkeyCrush80_v3.js` `LEDS` (ordered slot index, enum identity, integer canvas coordinates). Define `Crush80Key` with descriptive C# identifiers, including punctuation, navigation, ISO, and media keys; collapse duplicate logical names into one enum member without collapsing their LED slots. Assert table length 92, coordinate bounds 37 × 12, and valid key identities in tests; compare the table to the source plugin during development to avoid skipped or reordered slots.
- [ ] Implement `Crush80Grid` with `Width = 37`, `Height = 12`, `SetKey(Crush80Key,Rgb24)`, `SetAt(int,int,Rgb24)`, and `Fill(Rgb24)`. Keep exactly one owned 92-element `Rgb24` frame; edits do no I/O. Use a simple fixed lookup or scan appropriate for 92 entries; avoid per-edit temporary arrays and strings. Gap/invalid inputs fail before any mutation.
- [ ] Mark the built-in mapping **provisional** in XML docs. Only Esc, F1, and Caps Lock currently have physical-key confirmation; no test against fake firmware can establish the others.

## Task 3 — Open, apply, and cleanup lifecycle

**Files:** Create `Crush80Keyboard.cs` under the adapter project; create lifecycle tests under the adapter test project.

- [ ] Write failing fake-firmware tests for opening through the internal test path: seed nonblack colors and nondefault mode/brightness/effect; check that opening snapshots and displays all-black, the new grid starts black, and disposing restores all original values and disposes the transport. Test opening failure still closes the acquired session without losing the original failure.
- [ ] Implement static `EnumerateDevices()` by delegating to `Crush80DeviceLocator.Enumerate()`. Implement `OpenAsync(CancellationToken)` using the first descriptor, and `OpenAsync(Crush80DeviceDescriptor,CancellationToken)` selecting exactly that descriptor. Both call one shared acquisition path with a zeroed 92-slot frame, own the resulting lease/session, and close the session on acquisition failure. Reuse the SDK's not-found behavior; do not reimplement discovery or packet handling.
- [ ] Write a failing end-to-end fake-firmware test that edits a key and a coordinate without any intervening device RGB writes, then calls `ApplyAsync` and observes the intended LED colors while untouched slots remain black. Apply the same frame again and verify the SDK lease sends no unchanged RGB chunks. Implement `ApplyAsync(CancellationToken)` with the grid's stable frame and `RgbControlLease.WriteFrameAsync`; do not add another chunk cache.
- [ ] Write failure-path tests: canceled apply leaves in-memory edits intact, rejected firmware writes propagate their SDK exception, and an in-flight apply rejects grid mutations/overlapping applies. Pause an SDK fake write to exercise the race rather than relying on timing. Once disposal begins, edits/apply are invalid; disposal waits for an active apply and then attempts uncancelled lease restoration **and** session closure. Test a restoration failure plus transport disposal failure preserves both errors. Implement synchronization around grid mutation and frame submission without copying a frame on each apply.
- [ ] Do not promise retry after transport/protocol faults, atomic frames, or recovery after physical disconnect. Only an SDK-controlled successful write is treated as applied; a failed write leaves the desired grid intact.

## Task 4 — Consumer sample, docs, and verification

**Files:** Add a small adapter sample under `sdk/dotnet/samples/`, add adapter-specific README/package readme in `sdk/dotnet/adapters/Wobkey.Crush80.Adapter/`, and update `sdk/dotnet/README.md` only to link the new package and its limits. Add the sample project to the .NET solution if consistent with the existing layout.

- [ ] Show `EnumerateDevices()`, `OpenAsync()` and descriptor selection, enum-key and coordinate edits, explicit `ApplyAsync`, and `await using` restoration. Make any sample command that writes to hardware opt-in and confirmed **before** device opening; `--help`/`--list` must not mutate lighting.
- [ ] Document wired-only scope, .NET 10, SDK dependency, black-on-open, explicit apply, exclusive access, disposal, and provisional mapping. State that Esc/F1/Caps Lock have physical confirmation and the other names do not. Do not label a fake-transport test or readback as physical-key verification. Package the adapter README and Apache-2.0 license through existing SDK conventions.
- [ ] Run adapter tests and the full `dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release`. Launch the adapter sample's `--help` and `--list` paths as a safe executable smoke; on a host without device permissions, report the discovery limitation rather than running a write command.
- [ ] Run `dotnet pack sdk/dotnet/adapters/Wobkey.Crush80.Adapter/Wobkey.Crush80.Adapter.csproj -c Release` and inspect package metadata/files. Verify no generated binaries or throwaway scripts enter the change. Report only checks actually observed; any physical key verification requires a separately authorized real-device run.
