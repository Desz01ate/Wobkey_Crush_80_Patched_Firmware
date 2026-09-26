# Wobkey Crush 80 SDK emulator

`Wobkey.Crush80.Emulator` is an in-process PKRG v2 keyboard emulator for applications built on the .NET SDK. It implements `IHidTransport`, keeps the emulated 92-slot RGB and lighting state in memory, and serves a loopback browser view of the resulting frame. It does not enumerate as HID hardware and never opens, configures, or flashes a physical keyboard.

## Run the visual demo

From the repository root:

```sh
dotnet run --project emulator/app/Wobkey.Crush80.Emulator.App -- --open
```

The demo injects the emulated transport into `Wobkey.Crush80.Adapter`, renders a moving rainbow through the normal SDK session and control-lease path, and opens the visualizer in the default browser. Stop it with Ctrl+C.

A finite non-interactive run is available for smoke checks:

```sh
dotnet run --project emulator/app/Wobkey.Crush80.Emulator.App -- --frames 20
```

## Use it in an SDK application

Reference the emulator project or package alongside the SDK:

```xml
<ProjectReference Include="/path/to/Wobkey/emulator/src/Wobkey.Crush80.Emulator/Wobkey.Crush80.Emulator.csproj" />
```

Start the emulator, show its URL, and pass its transport to the SDK's existing injected-transport overload:

```csharp
using Wobkey.Crush80.Emulator;
using Wobkey.Crush80.Sdk.Models;
using Wobkey.Crush80.Sdk.Session;

await using var emulator = await Crush80Emulator.StartAsync();
Console.WriteLine(emulator.VisualizerUri);

await using var session = await Crush80RgbSession.OpenAsync(emulator.Transport);
var black = new Rgb24[session.Capabilities.LedCount];
await using var lease = await session.AcquireControlAsync(black);

var frame = new Rgb24[session.Capabilities.LedCount];
frame[0] = new Rgb24(255, 0, 0); // Esc
frame[1] = new Rgb24(0, 255, 0); // F1
await lease.WriteFrameAsync(frame);
```

`Crush80RgbSession.OpenAsync(IHidTransport)` transfers ownership of the transport to the session. Emulator and transport disposal are idempotent, so the enclosing emulator can still stop the browser server after the SDK closes the transport.

The grid adapter also accepts the emulator transport:

```csharp
using Wobkey.Crush80.Adapter;
using Wobkey.Crush80.Emulator;
using Wobkey.Crush80.Sdk.Models;

await using var emulator = await Crush80Emulator.StartAsync();
await using var keyboard = await Crush80Keyboard.OpenAsync(emulator.Transport);

keyboard.Grid.SetKey(Crush80Key.Esc, new Rgb24(255, 0, 0));
keyboard.Grid.SetKey(Crush80Key.F1, new Rgb24(0, 255, 0));
await keyboard.ApplyAsync();
```

## Visualizer behavior

The server binds only to `127.0.0.1`; port zero selects an available local port. The page polls versioned state and renders all 92 firmware slots using the adapter's physically verified 37 × 12 map.

The default display shows the raw RGB buffer supplied by the SDK. The optional brightness checkbox applies the firmware brightness lookup approximately, including the physical firmware's `192/256` maximum scaling. The status badges show transport state, override state, selected effect, brightness, and state version. RGB remains visible for inspection when override is disabled or effect 6 is not selected, with a warning that the physical keyboard would not currently display that per-key buffer.

## Emulated protocol

The transport advertises the current wired PKRG v2 capability shape:

- VID/PID `320F:5055`
- VIA usage page `0xFF60`, usage `0x61`
- 92 RGB slots
- sequential chunks of up to eight LEDs
- nine-LED × eleven-fragment streaming metadata
- per-key enable state
- OEM brightness and effect state
- PKRG v2 fragment and commit handling

The current SDK intentionally uses the sequential operation-2 common subset on PKRG v1 and v2. Streaming support in the emulator keeps its advertised v2 protocol internally consistent and permits future SDK transport tests.

## Boundaries

This is an SDK application emulator, not a USB device emulator:

- SDK discovery APIs do not list it; applications must inject `emulator.Transport`.
- SignalRGB and VIA cannot discover it as hardware.
- USB scheduling, HID framing, device permissions, disconnect behavior, and physical scanout timing are not reproduced.
- OEM animation algorithms are not rendered. The page visualizes the supplied per-key RGB buffer.
- Browser colors are an inspection aid, not a calibrated prediction of switch, keycap, diffuser, or camera appearance.

## Verification

```sh
dotnet test emulator/Wobkey.Crush80.Emulator.sln -c Release
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release
dotnet pack emulator/src/Wobkey.Crush80.Emulator/Wobkey.Crush80.Emulator.csproj -c Release --output /tmp/wobkey-emulator-pack
```

The emulator tests exercise SDK capability negotiation, lease writes and restoration, adapter submission, public layout metadata, and the browser API's exact firmware-slot colors without accessing HID hardware.
