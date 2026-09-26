# Crush 80 C# SDK design

## Problem

The per-key RGB firmware introduced by commit `cee8dc84e9a6a2f485bb0466a60cd2af4de54029` exposes a versioned `PKRG` protocol over the keyboard's wired VIA HID interface. The repository currently has a Linux Python client and a SignalRGB JavaScript plugin, but no reusable C# library for applications that need direct per-key control.

A C/C++ core with C# bindings would centralize protocol code, but the protocol is small and the native boundary would add ABI design, marshalling, architecture-specific assets, signing, packaging, and cross-language lifetime/error translation. The SDK will instead use an idiomatic C# implementation. Future language SDKs will implement the same protocol natively and share language-neutral conformance vectors rather than a binary library.

The first C# SDK must support Windows, Linux, and macOS, hide HID and firmware packet details from normal consumers, preserve the keyboard's prior lighting state, and remain a stable foundation for a later key/effect-oriented developer library.

## Goals

- Provide an idiomatic asynchronous C# API for the wired Crush 80 per-key RGB firmware.
- Support Windows, Linux, and macOS through a replaceable HidSharp transport adapter.
- Reject incompatible firmware through a read-only capability handshake before any mutation.
- Hide HID framing, VIA packets, eight-LED chunking, acknowledgement validation, and request serialization.
- Expose frame- and range-oriented RGB control without embedding an animation scheduler.
- Capture and restore the prior RGB buffer, override mode, OEM brightness, and OEM effect.
- Avoid per-chunk allocations in normal frame submission.
- Permit transport injection for deterministic tests and specialized hosts.
- Establish shared protocol conformance data for future native implementations in other languages.

## Non-goals

- No C or C++ implementation, stable C ABI, P/Invoke wrapper over a project-owned native library, or project-owned native runtime assets.
- No firmware flashing, VIA keymap/configuration management, SignalRGB plugin management, or wireless-control commands.
- No background animation loop, effect engine, frame scheduler, FPS policy, or backpressure policy.
- No key-name, zone, or physical-layout abstraction in the foundational SDK.
- No support claim for the experimental 2.4 GHz protocol path.
- No protocol changes to the firmware.
- No persistence of RGB frames to keyboard flash.
- No atomic-frame guarantee; protocol version 1 updates at most eight LEDs per request.


## Architectural decision

Publish one managed NuGet package, `Wobkey.Crush80.Sdk`, targeting `net10.0`. The package has internal layers but a single deployment unit:

```text
Consumer application
        |
        v
Crush80RgbSession / RgbControlLease
  - capability negotiation
  - operation serialization
  - frame/range operations
  - capture and restoration
        |
        v
Internal PKRG protocol codec
        |
        v
IHidTransport
        |
        +-- HidSharpTransport
        |     +-- Windows HID
        |     +-- Linux hidraw
        |     +-- macOS IOKit
        |
        +-- consumer or test transport
```

The package directly depends on HidSharp for the default transport. HidSharp is isolated behind `IHidTransport`; replacing it must not change the device/session API.

The initial implementation pins HidSharp `2.6.4` and includes its Apache 2.0 attribution in package notices. Updating the dependency requires the same transport tests and per-OS hardware smoke as the initial adapter.

The existing Windows installer HID implementation is specific to the OTA interface and remains in the installer. Its device enumeration and asynchronous I/O patterns may inform the SDK, but the SDK does not reference the WPF project or create a second dependency on installer types.

## Repository layout

```text
sdk/
  conformance/
    pkrg-v1.json
  dotnet/
    Wobkey.Crush80.Sdk.sln
    src/
      Wobkey.Crush80.Sdk/
    tests/
      Wobkey.Crush80.Sdk.Tests/
    samples/
      Wobkey.Crush80.Sample/
```

The SDK source and tests stay independent of the Windows installer. The conformance corpus is repository-level so future Python, Rust, C++, or other language implementations can consume it.

## Layering and future developer API

The foundational SDK is frame-oriented, not effects-oriented:

```text
Future Wobkey.Crush80.Effects
  - key names and zones
  - effects and transitions
  - frame scheduling and backpressure
                  |
Wobkey.Crush80.Sdk
  - control lease and state restoration
  - full-frame and range writes
  - protocol and transport ownership
                  |
PKRG protocol codec
                  |
HID transport
```

A future developer-facing project can offer APIs such as `SetKeyColor`, `SetZoneColor`, or `PlayAsync` by producing 92-slot frames and submitting them through `RgbControlLease`. It must not duplicate HID discovery, packet construction, compatibility checks, or restoration logic.

Firmware-oriented diagnostic operations remain available under an explicit `session.Advanced` surface. They are not mixed with the recommended control-lease workflow.

## Device discovery

`Crush80DeviceLocator` enumerates HID devices and returns immutable `Crush80DeviceDescriptor` values. A supported wired device must match all of:

- Vendor ID: `0x320F`
- Product ID: `0x5055`
- Usage page: `0xFF60`
- Usage: `0x61`

Discovery performs no writes. It must not select the standard keyboard interface, OTA interface `0xFFEF`, or wireless-control interface `0xFF1C`.

The public API includes:

- Enumeration for multi-keyboard applications.
- Opening a specific descriptor.
- `OpenFirstAsync` as a convenience for applications that accept the first compatible device.

No API silently falls back to matching only VID/PID when usage information is available.

## Transport boundary

`IHidTransport` is public because tests and specialized hosts must be able to inject a transport. It exposes payload-oriented operations, not OS report framing:

- Writes accept exactly one canonical 32-byte VIA payload.
- Reads return exactly one canonical 32-byte VIA payload.
- The transport adds or removes report ID zero and any OS-required report framing.
- The transport exposes device identity for diagnostics.
- The transport supports asynchronous write, asynchronous read with timeout, and asynchronous disposal.

The interface keeps writes and reads separate. Protocol version 1 initially uses sequential write/read exchanges on every OS. Separate operations preserve the option to add bounded one-frame pipelining internally after real-device verification without changing the public API.

`HidSharpTransport` is the default implementation. It requests exclusive/interprocess ownership where supported. If ownership cannot be obtained, opening fails with `DeviceBusyException`; the SDK does not knowingly continue with shared access.

Linux access requires an appropriate udev rule. macOS documentation must state applicable HID permission and application-sandbox constraints. The SDK reports permission failures as transport/open errors rather than pretending the device is absent.

## Protocol compatibility

Opening a session is read-only:

1. Open the selected VIA HID interface.
2. Send GET command `0x08`, vendor channel `0x7F`, operation `0`.
3. Require signature `PKRG`.
4. Require protocol version `1`.
5. Require LED count `92`.
6. Require chunk limit `8`.
7. Require a valid enabled flag of zero or one.

If any check fails, opening throws `IncompatibleFirmwareException` and sends no SET request.

NuGet package version and firmware protocol version are independent. The package uses semantic versioning. The first SDK accepts exactly `PKRG` version 1; it does not attempt best-effort writes to unknown versions. Future protocol versions are selected through an internal negotiated strategy while preserving the public frame/session API where semantics remain compatible.

## Primary public model

### `Rgb24`

`Rgb24` is an immutable value type containing three byte channels: red, green, and blue. It has value equality and no heap allocation per color. Frames cross asynchronous API boundaries as `ReadOnlyMemory<Rgb24>` or `Memory<Rgb24>`.

RGB values are the raw supplied values before the firmware's OEM brightness scaling. The firmware currently scales each channel by its brightness lookup value. The SDK does not pre-apply that scaling.

### `PerKeyRgbCapabilities`

The immutable capability value exposes negotiated protocol version, LED count, chunk limit, and enabled state observed during the handshake. Protocol constants remain internal; consumers do not construct raw capability packets.

### `Crush80RgbSession`

`Crush80RgbSession` owns one open HID interface and one protocol request stream. It is `IAsyncDisposable` and serializes complete logical operations. It exposes:

- Device descriptor and negotiated capabilities.
- `AcquireControlAsync` as the recommended mutation path.
- An `Advanced` surface for diagnostics and explicit device-level operations.

Opening the session does not capture state or change lighting.

Default opening APIs accept a `Crush80DeviceDescriptor` and create the HidSharp transport. A separate `OpenAsync(IHidTransport, CancellationToken)` overload performs the same capability negotiation over an injected transport; injection does not bypass compatibility checks.

### `RgbControlLease`

`RgbControlLease` represents temporary exclusive lighting control. It is `IAsyncDisposable` and exposes:

- `WriteFrameAsync(ReadOnlyMemory<Rgb24>, CancellationToken)`
- `WriteRangeAsync(int startIndex, ReadOnlyMemory<Rgb24>, CancellationToken)`
- `FillAsync(Rgb24, CancellationToken)`
- `ReadFrameAsync(Memory<Rgb24>, CancellationToken)`
- `SetHardwareBrightnessAsync(byte, CancellationToken)`
- Negotiated capabilities and current enabled state
- Explicit `RestoreAsync(CancellationToken)`

It does not expose packets, channel numbers, chunk sizes, report IDs, threads, timers, or FPS settings.

Full-frame read and write methods require memory containing exactly the negotiated 92 colors. The caller must not mutate memory supplied to an asynchronous write until the returned operation completes. Packet encoding reads `Rgb24` channels explicitly and does not depend on CLR struct packing.

## Control acquisition

The recommended usage is:

```csharp
await using var session =
    await Crush80RgbSession.OpenFirstAsync(cancellationToken);

var initialFrame = new Rgb24[session.Capabilities.LedCount];

await using var control =
    await session.AcquireControlAsync(
        initialFrame,
        new RgbControlOptions
        {
            HardwareBrightness = 9,
            RestoreStateOnDispose = true
        },
        cancellationToken);

await control.WriteFrameAsync(frame, cancellationToken);
```

`AcquireControlAsync` performs these steps under one session operation lock:

1. Validate the complete initial frame and options before I/O mutation.
2. Read all 92 existing RGB values.
3. Read the per-key override state.
4. Read OEM brightness.
5. Read OEM effect.
6. If any snapshot read fails, stop without mutation.
7. Disable per-key override.
8. Upload the complete initial frame.
9. Set the requested hardware brightness.
10. Select OEM effect 6.
11. Enable per-key override.
12. Return the control lease.

Requiring an initial frame prevents stale volatile RGB data from becoming visible when the override is enabled.

If acquisition fails after mutation begins, the SDK attempts restoration using the completed snapshot before surfacing the acquisition failure. If both acquisition and restoration fail, the thrown restoration exception retains the original acquisition failure as contextual error data.

`RestoreStateOnDispose` defaults to true. Opting out is explicit and affects only volatile device state.

Only one control lease may be active per session. While a lease is active, `session.Advanced` mutation and restoration operations fail with `InvalidOperationException`; callers must use the lease so its acknowledged-frame cache remains authoritative. A restored or disposed lease rejects later operations with `ObjectDisposedException`.

## State restoration

The saved state contains:

- All 92 RGB values.
- Original override enabled state.
- Original OEM brightness.
- Original OEM effect.

Restoration runs in this order:

1. Disable per-key override.
2. Restore all 92 saved RGB values.
3. Restore OEM brightness.
4. Restore OEM effect.
5. Restore the original override enabled state.

This order prevents a partially restored RGB buffer from being displayed through the override. Restoration uses sequential acknowledged commands even if future frame writes gain bounded pipelining.

Restoration attempts every remaining safe step after an individual failure and records each failure. It succeeds only if every requested field is acknowledged. `RestoreAsync` is idempotent after a successful restore. Successful explicit restoration makes lease disposal a no-op.

`RgbControlLease.DisposeAsync` performs restoration when configured and throws `StateRestoreException` if restoration is incomplete. Disposing a session with an active lease first attempts that lease's configured restoration, then closes the transport; transport closure still occurs when restoration fails. The normal nested `await using` order remains the recommended path.

The public immutable `RgbDeviceState` returned by `CaptureStateAsync` owns its captured color storage and exposes it as read-only memory. Reusing a captured state for restoration does not re-read current device values.

If the device disconnects, restoration fails visibly. The SDK does not claim that state was restored. Per-key data remains volatile and firmware cold boot initializes a cleared buffer with the override disabled.

## Advanced surface

`session.Advanced` supports diagnostic and tool scenarios without exposing raw packets. It includes:

- `CaptureStateAsync`
- `RestoreStateAsync`
- `ReadFrameAsync`
- `WriteFrameAsync`
- `WriteRangeAsync`
- `GetEnabledAsync` / `SetEnabledAsync`
- `GetEffectAsync` / `SetEffectAsync`
- `GetBrightnessAsync` / `SetBrightnessAsync`

Advanced writes perform full validation and normal acknowledgement checks. They do not assume ownership of state changed through another process. Changed-chunk caching is therefore limited to an active control lease, which owns a known initial frame and exclusive session.

The SDK does not expose a general `SendPacketAsync` escape hatch. Applications requiring arbitrary VIA commands must use or create a separate VIA protocol library rather than coupling the RGB SDK to undocumented packets.

## Frame validation and transmission

A full frame contains exactly 92 colors in firmware LED-slot order. A range must satisfy:

- At least one color.
- `startIndex >= 0`.
- `startIndex + colorCount <= 92`.

`WriteRangeAsync` may span more than one firmware chunk and chunks the validated range internally. Brightness values are limited to `0..9`; advanced effect values are limited to the firmware's documented `0..18` range. Invalid public arguments use standard .NET argument exceptions and perform no device I/O.

The SDK validates the complete frame or range before sending its first write, preventing client-side validation failures from causing partial mutation.

Within a control lease, frame transmission is:

1. Compare each eight-LED chunk with the last acknowledged frame cache.
2. Skip unchanged chunks.
3. Serialize each changed chunk into a reusable 32-byte request buffer.
4. Send one request and consume its response before the next request in the initial release.
5. Validate command, channel, operation, status, start, count, and echoed RGB payload.
6. Update the cache only after that chunk is acknowledged.

The final chunk contains four LEDs. Padding bytes are zeroed so prior request data cannot leak into a shorter packet.

A full frame is not atomic. Earlier chunks may be visible before later chunks arrive. If an acknowledged chunk is followed by a failed chunk, the keyboard contains a partial new frame and the cache reflects only acknowledged chunks. The next successful submission can reconcile the remaining chunks.

The SDK does not perform a full RGB GET readback after every frame; that would materially reduce streaming throughput. Echo acknowledgement is the normal write contract. Explicit readback remains available through `ReadFrameAsync` for diagnostics and release smoke tests.

## Concurrency and cancellation

The firmware protocol has no request identifiers. Only one ordered request stream is safe. The session serializes whole logical operations, not individual packets, so frame chunks cannot interleave with mode changes, snapshots, or restoration.

Concurrent calls on the same session are queued in invocation order after argument validation. Session disposal prevents new operations, waits for the current complete exchange, applies active-lease restoration as specified above, and then closes the transport.

Cancellation is observed before the first request and between complete request/response exchanges. Once a report has been written, the matching response must be consumed or timed out; cancellation does not abandon that response mid-exchange.

If a response times out, is malformed, or does not match the request, stream alignment is no longer trustworthy. The session becomes faulted and rejects subsequent commands with `SessionFaultedException`. Recovery requires disposal and reopening. The SDK does not automatically retry a timed-out write because a late response could be mistaken for a later command.

A structurally valid firmware error response does not fault the session. It becomes `FirmwareRejectedRequestException`, and later operations remain permitted.

Consumers must not run SignalRGB, VIA, the Python RGB tool, or another SDK session against the same interface. Unexpected structurally valid traffic from another controller is treated as a protocol mismatch or ownership failure, not ignored.

## Bounded pipelining extension point

The existing optimization research indicates that sending at most one frame's changed RGB reports before reading their replies may improve wired throughput. That optimization is not yet verified across Windows, Linux, and macOS.

The first SDK release therefore uses sequential acknowledged exchanges on every platform. The separated transport write/read API permits a later internal optimization with these invariants:

- Queue no more than one frame.
- Consume and validate every reply before beginning another frame.
- Never begin mode, effect, brightness, snapshot, or restoration operations with outstanding RGB replies.
- Advance the frame cache only after all corresponding acknowledgements pass.
- Disable pipelining on any platform/transport without real-device evidence.

This extension must not change the public frame API.

## Allocation and performance rules

- Reuse 32-byte request and response buffers for serialized session operations.
- Use spans for packet encoding/decoding and chunk comparisons.
- Use `Rgb24` value types rather than per-pixel objects.
- Allocate the captured 92-color state once per control lease.
- Do not allocate a new request, response, or flattened RGB array per chunk.
- Do not add background workers or channels when caller-driven asynchronous methods suffice.
- Exceptions may allocate on failure paths.
- HidSharp-internal allocations are outside the protocol layer's control and do not justify a project-owned native transport.

USB request/reply latency, not RGB arithmetic, is expected to dominate frame time.

## Error model

All SDK-specific failures derive from `Crush80SdkException`:

- `DeviceNotFoundException`: no matching VIA interface.
- `DeviceBusyException`: another process owns or is using the interface.
- `IncompatibleFirmwareException`: missing or unsupported `PKRG` capabilities.
- `FirmwareRejectedRequestException`: valid response with nonzero firmware status.
- `ProtocolViolationException`: malformed, mismatched, or out-of-order response.
- `DeviceDisconnectedException`: device removed during the session.
- `SessionFaultedException`: operation attempted after synchronization loss.
- `StateRestoreException`: one or more saved-state fields could not be restored.

Exceptions include operation name, device identity, and protocol status where available. They do not include full RGB payloads by default.

Transport-specific exceptions are translated into this model when the cause is known. Unknown I/O failures retain their original exception as `InnerException`.

## Shared conformance corpus

`sdk/conformance/pkrg-v1.json` defines language-neutral observable scenarios rather than C# implementation details. Scenarios cover:

- Capability negotiation and exact compatibility rejection.
- Mode reads and writes.
- Complete and partial RGB chunks.
- First and final LED boundaries.
- Firmware status errors.
- Malformed and mismatched acknowledgements.
- Snapshot-before-mutation ordering.
- Restoration ordering.
- Timeout and synchronization-loss behavior.

Applicable vectors are executed against the existing patched-firmware machine-code emulator in Python. C# tests execute the same scenarios through an injected transport. Future language SDKs consume the same corpus. This is the compatibility mechanism across native language implementations; duplicated implementations are acceptable, duplicated protocol interpretations are not.

## Verification strategy

### Protocol behavior tests

- Stock or hue-only firmware cannot be mistaken for per-key support.
- Capability mismatch causes zero SET requests.
- Complete inputs are validated before mutation.
- Eight-LED chunks and the final four-LED chunk are encoded correctly.
- Response command/channel/operation/range/data are correlated.
- Firmware status values map to explicit failures.

### Session lifecycle tests

- Snapshot completes before the first mutation.
- Initial frame is uploaded before the override is enabled.
- Unchanged chunks are skipped.
- Failed acknowledgements do not advance the corresponding cache state.
- Partial-frame failure is represented accurately on the next submission.
- Normal disposal restores all saved fields in order.
- Acquisition failure attempts restoration.
- Restoration attempts safe remaining steps and reports every failed field.
- Explicit successful restoration makes disposal a no-op.
- Timeout or malformed replies fault the session.

### Transport adapter tests

- Report ID zero is added and removed correctly.
- Device filtering requires the VIA usage page and usage.
- Permission, busy, cancellation, timeout, and disconnection conditions map correctly.
- Unit and integration tests run in Windows, Linux, and macOS CI without requiring physical hardware.

### Hardware support smoke

Before claiming support for an OS, exercise a real keyboard on that OS:

1. Discover and open the VIA interface.
2. Negotiate supported `PKRG` v1 or optimized v2 capabilities.
3. Capture the current complete state.
4. Display and visually confirm a known Esc/F1 pattern.
5. Submit a changing full frame using the implemented sequential operation-2 path.
6. Read back all 92 supplied RGB values.
7. Restore the prior RGB, override state, effect, and brightness.
8. Read back and confirm the restored state.

Record evidence per OS, including SDK commit, OS, .NET runtime, HidSharp version, firmware-reported CRC (query separately; the SDK sample does not read firmware metadata), PKRG capability response, Esc/F1 visual observation, all-92 readback result, and restored mode/effect/brightness/RGB verification. A readback match is not visual confirmation or proof of restoration. Passing platform-neutral tests alone is not a support claim; every OS claim requires its own authorized physical-device run.

## Documentation requirements

The SDK documentation includes:

- Minimal control-lease quick start.
- Device enumeration and explicit opening.
- Advanced diagnostic operations.
- Linux udev setup.
- macOS HID permission and sandbox constraints.
- Exclusive ownership and conflicts with SignalRGB, VIA, and other tools.
- Firmware prerequisites and read-only compatibility negotiation.
- Volatile RGB state and disconnect limitations.
- Non-atomic frames and absence of an FPS guarantee.
- State restoration semantics and failure handling.
- Clear separation between the frame SDK and a future effects/key-mapping library.

The sample application demonstrates discovery, acquisition with an initial frame, a bounded color update, explicit restoration, and cancellation. It does not implement a production animation engine.

## Risks and mitigations

- **HidSharp platform behavior differs by OS.** Keep it behind `IHidTransport`, run hardware smoke per OS, and do not claim support based only on library documentation.
- **Another controller can corrupt request ordering.** Request exclusive access, serialize all operations, and fault on mismatched responses.
- **Frames are non-atomic.** Document the limit and preserve acknowledged-chunk cache state after partial failure.
- **Disconnection can prevent restoration.** Surface restoration failure and rely only on the firmware's verified volatile/cold-boot behavior, not a false success claim.
- **A broad public low-level API would freeze firmware details.** Keep raw packets internal and place necessary device-level controls under `session.Advanced`.
- **Future effects requirements could distort the base SDK.** Keep scheduling, key semantics, and effects in a separate package built on 92-slot frames.
- **Language implementations can drift.** Use one versioned conformance corpus executed by the firmware emulator and every SDK implementation.

## Compatibility addendum — 2026-09-26

The original v1-only handshake decision above describes the initial SDK design. A user's Windows sample on optimized firmware physically received capability response `CH8AAFBLUkcCXAgACQsAAAAAAAAAAAAAAAAAAAAAAAA=`: a valid 32-byte PKRG response with version 2, 92 LEDs, eight-LED configuration chunks, disabled override, and streaming metadata of nine LEDs × eleven fragments. The previous exact-v1 parser rejected this response before lighting writes. Optimized firmware commit `a88d8850428830c8d970b85c88987e7f45d94131` retains mode operation 1, RGB configuration GET/SET operation 2, and OEM brightness/effect controls while adding streaming operations 3/4.

The SDK now accepts **only** exact v1 or exact optimized v2 capability formats; v2 additionally requires streaming metadata 9/11. The four-argument `PerKeyRgbCapabilities` constructor is unchanged. Nullable init properties report the stream LED/fragment capacities; `SupportsFrameStreaming` describes firmware support, not SDK usage. Both versions continue to use the existing sequential, acknowledged eight-LED operation-2 reads/writes, capture, and restoration. The SDK does **not** send streaming operations 3/4 or promise atomic frames; a future v2 internal strategy and host-rendered effects remain separate future capabilities.

**Recorded Windows v2 hardware smoke — 2026-09-26:** On a Windows host, SDK commit `501b802` was tested with optimized PKRG v2 firmware. Capability output was `PKRG v2: 92 LEDs, 8 per chunk; override initially False.` Sample output was `Full 92-color pattern readback and restored mode/effect/brightness/RGB verified.` The user physically confirmed Esc red and F1 green during the two-second pattern. This verifies the Windows wired PKRG v2 common-subset SDK path: capability negotiation, sequential operation-2 writes, full 92-color readback, physical Esc/F1 output, and restoration. No firmware CRC evidence was supplied in this run.

Linux and macOS SDK hardware paths remain implementation targets requiring separate authorized smoke runs. V2 atomic streaming operations 3/4 remain unimplemented and unverified in the SDK.
