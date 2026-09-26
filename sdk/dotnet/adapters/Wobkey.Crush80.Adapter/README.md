# Wobkey.Crush80.Adapter

`Wobkey.Crush80.Adapter` is a .NET 10 convenience API for setting wired Crush 80 per-key RGB colors by logical key enum or sparse canvas coordinate. It depends on `Wobkey.Crush80.Sdk`; the SDK owns HID discovery, exclusive control, writes, state capture, and restoration. This adapter does not support wireless devices.

Use compatible wired per-key firmware as required by the SDK. Opening an adapter keyboard acquires control and immediately submits an all-black frame. Grid edits afterward change only the in-memory frame; they do not write to HID until `ApplyAsync` is called.

## Basic use

```csharp
using Wobkey.Crush80.Adapter;
using Wobkey.Crush80.Sdk.Models;

await using var keyboard = await Crush80Keyboard.OpenAsync();
keyboard.Grid.SetKey(Crush80Key.Esc, new Rgb24(255, 0, 0));
keyboard.Grid.SetAt(3, 0, new Rgb24(0, 255, 0)); // F1 canvas coordinate.
await keyboard.ApplyAsync();
```

The convenience overload opens the first compatible device in the SDK's enumeration order. To inspect devices before choosing one, enumerate descriptors and pass the selected descriptor to the other overload:

```csharp
using Wobkey.Crush80.Adapter;
using Wobkey.Crush80.Sdk.Models;

var devices = Crush80Keyboard.EnumerateDevices(); // discovery only; no device is opened
if (devices.Count == 0)
    throw new InvalidOperationException("No wired Crush 80 was found.");

await using var keyboard = await Crush80Keyboard.OpenAsync(devices[0]);
keyboard.Grid.SetKey(Crush80Key.Esc, new Rgb24(255, 0, 0));
await keyboard.ApplyAsync();
```

For hardware-free integration, `OpenAsync(IHidTransport)` accepts either the in-process
emulator or a `Crush80RemoteTransport` connected to the standalone server. The repository's
[`emulator/`](../../../../emulator/README.md) project uses this overload to run the same grid,
apply, lease, and restoration path while displaying the resulting RGB frame in a local browser.

`OpenAsync(descriptor)` opens that descriptor; it does not silently choose a different device. `Grid.Width` and `Grid.Height` are 37 and 12. `SetKey` edits every LED assigned to a logical enum key, `SetAt(x, y)` edits emitters at a canvas point, and `SetAll` (also available as `Fill`) edits all 92 frame slots. Enumerating `Grid` yields each mapped `Crush80Key` once, ordered from top to bottom and left to right by its first LED. Coordinates are points on the sparse built-in canvas, not firmware/VIA row-column addresses; gaps have no LEDs. Call `ApplyAsync` to submit the current frame. `await using` disposes the adapter and asks the SDK to restore the state captured when control was acquired.

## Mapping and hardware safety

The built-in 37 × 12 map was derived from the SignalRGB wired plugin. Its mapped key assignments and top-to-bottom, left-to-right traversal have since been visually verified on wired Crush 80 hardware using the sample. Protocol readback alone does not verify physical key identity; this confirmation came from observing the keyboard during traversal.

The keyboard lighting interface requires exclusive control. Close SignalRGB, VIA, and other keyboard controllers before opening the adapter; concurrent writers can invalidate the state snapshot and interfere with writes or restoration. Opening changes lighting to black before the caller's first `ApplyAsync`, so even opening for a read-only-looking grid edit is a hardware write. Grid mutations alone do not perform HID I/O.

Disposal asks the SDK to restore the captured colors and settings. Restoration cannot be guaranteed after a disconnect, transport or device failure, loss of power, or interference from another writer. Treat open, apply, and cleanup errors as meaningful; inspect the keyboard if an operation or disposal fails. The adapter does not promise atomic frame changes or physical recovery after an error.

## Hardware sample

From the repository root:

```sh
dotnet run --project sdk/dotnet/samples/Wobkey.Crush80.Adapter.Sample/Wobkey.Crush80.Adapter.Sample.csproj
```

This sample writes to the first discovered keyboard as soon as it starts; it does not provide read-only `--help` or `--list` modes. Opening immediately sends a black frame. The sample then applies Esc red and F1 green, waits for a keypress, and runs a full-layout rainbow animation. The rainbow traverses mapped keys in row-major order through a circular linked list; press another key to stop. Disposal asks the SDK to restore captured state, but physical restoration is not independently verified or guaranteed. Run only when prepared for temporary hardware writes and possible incomplete restoration.

