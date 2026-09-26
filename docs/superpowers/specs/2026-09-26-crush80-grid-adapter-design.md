# Crush 80 grid adapter design

## Purpose and scope

Provide a small, high-level .NET library over `Wobkey.Crush80.Sdk` for coloring the wired Crush 80 by logical key or sparse keyboard coordinate. Opening the adapter takes control and displays black; edits remain in memory until an explicit apply. Disposal restores the lighting state captured by the SDK. This is a separate package at `sdk/dotnet/adapters/Wobkey.Crush80.Adapter`, targeting .NET 10 and referencing the existing SDK rather than reimplementing HID or PKRG.

The first release does not include effects, animations, a scheduler, rectangles/rows/columns, arbitrary user layouts, firmware flashing, wireless access, or changes to SDK protocols. It makes no claim that the full physical key mapping has been verified.

## Public surface and ownership

`Crush80Keyboard` is the single stateful entry point and implements `IAsyncDisposable`:

- `EnumerateDevices()` returns the SDK's wired `Crush80DeviceDescriptor` results for caller selection; it performs no adapter-specific discovery.
- `OpenAsync(CancellationToken)` opens the first SDK-discovered compatible device, following the SDK's enumeration order. It throws the SDK's device-not-found error if none are available.
- `OpenAsync(Crush80DeviceDescriptor, CancellationToken)` opens the specified wired device. There is no silent fallback to another device.
- `Grid` exposes the adapter's in-memory `Crush80Grid`.
- `ApplyAsync(CancellationToken)` submits the entire current grid frame through the active SDK `RgbControlLease`.
- `DisposeAsync()` releases the lease and closes its owning SDK session.

The default entry is therefore `await Crush80Keyboard.OpenAsync()`, while callers selecting among devices use `Crush80Keyboard.EnumerateDevices()` followed by the descriptor overload. SDK discovery, compatibility negotiation, exclusive transport ownership, chunked writes, and state restoration stay in the SDK. No custom transport or session abstraction is part of the first public adapter API. In the target SDK, descriptor and color model types are in `Wobkey.Crush80.Sdk.Models`; session and transport types remain in their respective `.Session` and `.Transport` namespaces.

## Grid and mapping

`Crush80Grid` describes the built-in sparse 37 × 12 keyboard canvas from `plugins/signalrgb/wired/WobkeyCrush80_v3.js`. Coordinates are canvas points, **not** VIA row/column addresses or a dense array of keys. The map assigns each of the firmware's 92 LED slots a logical `Crush80Key` enum value and an `(x, y)` point. This is a fixed layout for the first release; gaps have no LEDs. The enum uses valid, descriptive C# identifiers for punctuation and special keys, with one member per distinct logical key, not one member per LED slot.

`Grid.SetKey(Crush80Key, Rgb24)` updates every LED slot belonging to that logical key; e.g. Space updates all three of its emitters and Caps Lock updates both. `Grid.SetAt(int x, int y, Rgb24)` updates all emitters sharing that point. `Grid.Fill(Rgb24)` updates all 92 slots. `Width` and `Height` report 37 and 12. All three mutations touch only the in-memory frame. `SetAt` on a gap or out-of-bounds point, and `SetKey` with an undefined enum value, throw argument-range errors without changing any slot. Key and coordinate edits affect the same frame: the most recent edit to a slot wins. No key-name strings, effect API, or rectangle/row/column fills are exposed.

The firmware-derived SignalRGB mapping and its canvas geometry are the built-in **provisional** default, not a hardware-verified map. Documentation must distinguish Esc, F1, and Caps Lock (physically checked according to the plugin and per-key guide) from all other key assignments (not yet physically checked). A full-frame readback confirms protocol data, not that the corresponding physical keys lit up.

## Lifecycle, submission, and errors

Both `OpenAsync` overloads open an SDK session, then call `AcquireControlAsync` with a zeroed `Rgb24[92]`. The SDK captures colors, override state, brightness, and effect before switching the keyboard to the black frame. The grid starts black to match the acquired state. Opening returns only after acquisition succeeds; if acquisition fails, dispose the session and preserve any acquisition/restoration error.

`ApplyAsync` passes the grid's 92-color frame to the SDK lease's `WriteFrameAsync`. The SDK already skips unchanged acknowledged eight-LED chunks, so the adapter keeps no second device-write cache. No HID work occurs during grid edits. While an apply is in flight, reject new grid mutations and overlapping applies; keep the submitted frame stable until the SDK call completes. Disposal waits for an in-flight apply to settle, then attempts uncancelled lease restoration and session disposal even if restoration fails. After disposal begins, grid mutation and apply fail as disposed operations.

Invalid arguments leave the grid unchanged. A canceled or failed apply propagates the underlying SDK error and leaves the requested in-memory colors intact; depending on the SDK error, the session may be faulted and further I/O may not be possible. Do not claim atomic frame updates, unconditional retry, or guaranteed restoration after disconnect or another writer. If both restoration and session closing fail, report both failures rather than hiding either. The adapter does not suppress SDK protocol, discovery, access, or restoration exceptions.

## Verification and documentation

- Add hardware-free tests around the adapter's SDK-backed lifecycle using the SDK's injectable `IHidTransport` at an internal test boundary. Verify black-on-open and that opening, apply, and disposal preserve the SDK's capture/restore ordering and ownership semantics; tests do not claim physical lighting verification.
- Verify enum-key and coordinate edits share a frame, duplicate emitters are grouped, gap/invalid inputs do not mutate the frame, and grid edits cause no I/O between opening and `ApplyAsync` (opening itself writes black).
- Verify apply submissions, changed-chunk behavior via the existing SDK, in-flight mutation guard, cancellation/failure propagation, and restoration/session cleanup on disposal.
- Document both `OpenAsync` overloads, enum and coordinate editing, explicit `ApplyAsync`, and `await using` disposal with a small sample. Explain the provisional mapping, wired-only scope, and the need for authorized physical checks before claiming the remaining named keys are correct.

A full physical-key verification run is not required to implement the provisional adapter, but the package documentation must not present the default mapping as fully verified.
