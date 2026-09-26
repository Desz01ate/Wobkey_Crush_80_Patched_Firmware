# Wobkey Crush 80 SDK emulator

`Wobkey.Crush80.Emulator` provides both an in-process SDK transport and a standalone, inter-process emulator server. The server keeps the emulated 92-slot PKRG v2 state in its own process, accepts one SDK application over loopback TCP, and serves a browser view of the resulting RGB frame. It never discovers, opens, configures, or flashes HID hardware.

## Standalone server: no generated animation

Start the idle emulator server from the repository root:

```sh
dotnet run --project emulator/server/Wobkey.Crush80.Emulator.Server -- --open
```

Default endpoints:

```text
Visualizer: http://127.0.0.1:5080/
Transport:  tcp://127.0.0.1:5081/
```

The server generates no frames. The browser initially reports `waiting for SDK client` and changes only when another process connects and performs SDK operations. Stop the server with Ctrl+C.

Ports are configurable; zero selects an available port:

```sh
dotnet run --project emulator/server/Wobkey.Crush80.Emulator.Server -- \
  --visualizer-port 6080 --transport-port 6081
```

## Separate adapter client sample

With the server running, start the static client in another terminal:

```sh
dotnet run --project emulator/client/Wobkey.Crush80.Emulator.Client.Sample
```

It connects through `Crush80RemoteTransport`, opens the normal grid adapter, applies one static multi-key frame, and holds it until Ctrl+C. There is no animation. A finite run is available for smoke checks:

```sh
dotnet run --project emulator/client/Wobkey.Crush80.Emulator.Client.Sample -- --seconds 10
```

The adapter restores the state it captured on opening before the client disconnects.

## Connect another SDK application

Reference the emulator project or package alongside the SDK or adapter:

```xml
<ProjectReference Include="/path/to/Wobkey/emulator/src/Wobkey.Crush80.Emulator/Wobkey.Crush80.Emulator.csproj" />
```

Then replace HID discovery with a remote transport connection:

```csharp
using Wobkey.Crush80.Adapter;
using Wobkey.Crush80.Emulator;
using Wobkey.Crush80.Sdk.Models;

var transport = await Crush80RemoteTransport.ConnectAsync(
    new Uri("tcp://127.0.0.1:5081"));

await using var keyboard = await Crush80Keyboard.OpenAsync(transport);

keyboard.Grid.SetAll(default);
keyboard.Grid.SetKey(Crush80Key.Esc, new Rgb24(255, 0, 0));
keyboard.Grid.SetKey(Crush80Key.F1, new Rgb24(0, 255, 0));
await keyboard.ApplyAsync();
```

`Crush80Keyboard` and `Crush80RgbSession` take ownership of the remote transport exactly as they do a real HID transport. The same remote transport can be passed directly to the SDK:

```csharp
using Wobkey.Crush80.Emulator;
using Wobkey.Crush80.Sdk.Models;
using Wobkey.Crush80.Sdk.Session;

var transport = await Crush80RemoteTransport.ConnectAsync();
await using var session = await Crush80RgbSession.OpenAsync(transport);

var black = new Rgb24[session.Capabilities.LedCount];
await using var lease = await session.AcquireControlAsync(black);

var frame = new Rgb24[session.Capabilities.LedCount];
frame[0] = new Rgb24(255, 0, 0);
frame[1] = new Rgb24(0, 255, 0);
await lease.WriteFrameAsync(frame);
```

`ConnectAsync()` without an endpoint uses `tcp://127.0.0.1:5081`.

## Ownership and reconnect behavior

The standalone server permits exactly one SDK client at a time, matching exclusive ownership of the physical VIA interface. A second connection receives a deterministic busy error. When the active client disconnects, the server:

- releases ownership for the next client;
- discards any unread protocol reply or incomplete streamed frame;
- retains the active RGB buffer, override, brightness, and effect state.

A normal adapter or SDK lease restores its captured state before disconnecting. A process crash cannot perform that restoration, matching the same limitation as a real transport failure.

## In-process mode

For unit tests or applications that do not need a process boundary, the original in-process API remains available:

```csharp
await using var emulator = await Crush80Emulator.StartAsync();
Console.WriteLine(emulator.VisualizerUri);
await using var keyboard = await Crush80Keyboard.OpenAsync(emulator.Transport);
```

The animated in-process demonstration remains separate:

```sh
dotnet run --project emulator/app/Wobkey.Crush80.Emulator.App -- --open
```

## Visualizer behavior

The browser server binds only to `127.0.0.1`. It renders all 92 firmware slots using the adapter's physically verified 37 × 12 map and reports whether an SDK client currently owns the emulator.

The default display shows the raw RGB buffer supplied by the SDK. The optional brightness checkbox applies the firmware brightness lookup approximately, including the physical firmware's `192/256` maximum scaling. RGB remains visible for inspection when override is disabled or effect 6 is not selected, with a warning that the physical keyboard would not currently display that per-key buffer.

## Emulated protocol

The device model advertises the current wired PKRG v2 capability shape:

- VID/PID `320F:5055`
- VIA usage page `0xFF60`, usage `0x61`
- 92 RGB slots
- sequential chunks of up to eight LEDs
- nine-LED × eleven-fragment streaming metadata
- per-key enable state
- OEM brightness and effect state
- PKRG v2 fragment and commit handling

The inter-process transport uses a versioned binary protocol over IPv4 loopback. It preserves `IHidTransport.WriteAsync` and `ReadAsync` boundaries, including silent PKRG streaming fragments, response timeouts, and transport ownership. It is not exposed on external network interfaces.

## Boundaries

This is an SDK application emulator, not a virtual USB device:

- SDK HID discovery does not list it; applications inject `Crush80RemoteTransport` or the in-process transport.
- SignalRGB and VIA cannot discover it as hardware.
- USB scheduling, HID framing, device permissions, and physical scanout timing are not reproduced.
- OEM animation algorithms are not rendered. The page visualizes the supplied per-key RGB buffer.
- Browser colors are an inspection aid, not a calibrated prediction of switch, keycap, diffuser, or camera appearance.

## Verification

```sh
dotnet test emulator/Wobkey.Crush80.Emulator.sln -c Release
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release
dotnet pack emulator/src/Wobkey.Crush80.Emulator/Wobkey.Crush80.Emulator.csproj \
  -c Release --output /tmp/wobkey-emulator-pack
```

The emulator tests cover in-process and remote SDK behavior, adapter frame publication and restoration, exclusive remote ownership, reconnect after an incomplete exchange, public layout metadata, and the browser API's exact firmware-slot colors without HID hardware.
