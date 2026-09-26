# Wobkey Crush 80 .NET SDK

`Wobkey.Crush80.Sdk` targets .NET 10 applications controlling the **wired** Crush 80 per-key RGB firmware. It is not a firmware installer. Build/CI coverage on Windows, Linux, and macOS is hardware-free. The Windows wired PKRG v2 common-subset path is hardware-verified as recorded below; Linux and macOS SDK hardware paths remain implementation targets requiring separate authorized smoke runs. The [Python per-key guide](https://github.com/Desz01ate/Wobkey_Crush_80_Patched_Firmware/blob/main/docs/user/PER-KEY-RGB.md) describes separate physical-keyboard evidence for the Linux Python client, not this SDK.

## Requirements and discovery

Install compatible **wired** v1.06 per-key firmware ([firmware guide](https://github.com/Desz01ate/Wobkey_Crush_80_Patched_Firmware/blob/main/docs/user/PER-KEY-RGB.md)) before using the SDK. The legacy hue-only firmware, stock firmware, and wireless dongle are not supported. Enumeration selects USB VID `0x320F`, PID `0x5055`, wired VIA interface top-level usage page `0xFF60` / usage `0x61` (interface 1 on the verified keyboard). Opening requires exact PKRG **v1** (92 LEDs, eight per transfer) or optimized **v2** (the same layout plus streaming metadata: nine LEDs × eleven fragments). Other versions and incompatible capability formats are rejected before lighting writes. `PerKeyRgbCapabilities` reports the negotiated version and nullable `StreamLedCount` / `StreamChunkCount`; `SupportsFrameStreaming` describes the firmware's v2 feature, **not** the SDK's upload strategy. An opened session owns its HID transport and must be disposed.

```csharp
using Wobkey.Crush80;
using Wobkey.Crush80.Transport;

var devices = Crush80DeviceLocator.Enumerate(); // discovery only, no lighting writes
if (devices.Count == 0)
    throw new DeviceNotFoundException("Open");

// Open the selected descriptor explicitly. OpenAsync performs a read-only capability handshake.
await using var session = await Crush80RgbSession.OpenAsync(devices[0]);
var initial = new Rgb24[session.Capabilities.LedCount]; // all black
await using var lease = await session.AcquireControlAsync(initial);
await lease.WriteRangeAsync(0, new[] { new Rgb24(255, 0, 0), new Rgb24(0, 255, 0) });
// Do work, then restore the captured colors, override mode, brightness, and effect.
await lease.RestoreAsync();
```

For precise control without an active lease, use `session.Advanced` for frame/range reads and writes, override mode, OEM brightness/effect, and ordered capture/restoration. Its mutation and restore operations are unavailable while a control lease owns the session. If providing your own `IHidTransport`, `Crush80RgbSession.OpenAsync(transport)` transfers ownership of the transport to the session. Do not mutate a supplied frame until its asynchronous operation has completed.

## Safe sample and release smoke

From the repository root:

```sh
dotnet run --project sdk/dotnet/samples/Wobkey.Crush80.Sample/Wobkey.Crush80.Sample.csproj -- --help
dotnet run --project sdk/dotnet/samples/Wobkey.Crush80.Sample/Wobkey.Crush80.Sample.csproj -- --list
```

`--help` does not access HID. `--list` discovers wired VIA interfaces without lighting writes or opening an SDK session; access to descriptors may still need OS permissions. The **hardware-writing** `--smoke` command must only be run with explicit authorization on a connected, compatible keyboard:

```sh
dotnet run --project sdk/dotnet/samples/Wobkey.Crush80.Sample/Wobkey.Crush80.Sample.csproj -- --smoke
```

The sample warns and requires typing exactly `SMOKE` **before opening** a device. It opens the first discovered wired interface, snapshots its lighting, acquires a control lease with a black frame, displays Esc red/F1 green for two seconds, and reads and compares **all 92** RGB values. It explicitly restores, checks the original override mode, effect, brightness and all 92 RGB values again, and reports success only after cleanup. Ctrl+C requests cancellation; lease and session cleanup attempt restoration without the canceled token. On **any** failure after acquisition begins, including cancellation or cleanup failure, the sample reports every operation/restoration/cleanup error it observes and warns that restoration is unverified: check keyboard lighting and settings manually. A disconnect can make restoration impossible. Never run the smoke command in CI or on a keyboard without the owner's consent.

For future Windows/Linux/macOS support claims, keep a **separate authorized physical-device run** for each OS and record: SDK commit, OS, .NET runtime, HidSharp version, firmware-reported CRC (query separately; the SDK sample does not read firmware metadata), PKRG capability response, Esc/F1 visual observation, all-92 readback result, and restored mode/effect/brightness/RGB verification. A readback match is not a visual confirmation or proof of restoration by itself. This remains the release-smoke checklist for future OS claims; an absent item must not be reported as observed evidence.

**Recorded hardware evidence — 2026-09-26:** On a Windows host, SDK commit `501b802` was tested with optimized PKRG v2 firmware. Capability output: `PKRG v2: 92 LEDs, 8 per chunk; override initially False.` Sample output: `Full 92-color pattern readback and restored mode/effect/brightness/RGB verified.` The user physically confirmed Esc red and F1 green during the two-second pattern. This verifies the Windows wired PKRG v2 common-subset SDK path for capability negotiation, sequential operation-2 writes, full 92-color readback, physical Esc/F1 output, and restoration. No firmware CRC evidence was supplied in this run.

Linux and macOS SDK hardware paths remain implementation targets requiring separate authorized smoke runs. V2 atomic streaming operations 3/4 remain unimplemented and unverified in the SDK. The release-smoke checklist above remains required for future per-OS claims.

## Access and limitations

- Close SignalRGB (including its wired plugin), VIA, the Python host tools, and other SDK sessions before acquiring control. The HID interface and lighting state require **exclusive ownership**; simultaneous writers can corrupt assumptions about the readback and saved state.
- On Linux, install [`hardware/udev/99-wobkey-crush80.rules`](https://github.com/Desz01ate/Wobkey_Crush_80_Patched_Firmware/blob/main/hardware/udev/99-wobkey-crush80.rules) into `/etc/udev/rules.d/`, for example `sudo install -m 0644 hardware/udev/99-wobkey-crush80.rules /etc/udev/rules.d/99-wobkey-crush80.rules`, then reload with `sudo udevadm control --reload-rules && sudo udevadm trigger` and reconnect the keyboard. The rule uses the `plugdev` group; ensure the account belongs to it or adapt the rule to the local group policy. Check permissions rather than running the sample as root by default.
- On macOS, HID access depends on local permissions and sandbox/entitlement settings; a sandboxed app may be denied even when discovery succeeds. Hardware behavior has not been verified on macOS.
- RGB buffer and override are **volatile** across resets and disconnects. On **both PKRG v1 and optimized v2**, this SDK uploads frames sequentially using acknowledged operation-2 chunks of at most eight LEDs, **not atomically**. Although v2 advertises atomic streaming, the SDK does not send operations 3/4; there is no FPS guarantee or tested continuous-streaming rate for this SDK. Future host-rendered effects and v2 atomic streaming remain separate capabilities. Readback returns supplied raw RGB values, before OEM brightness scaling. Keep every write buffer unchanged until its task completes.
- A lease takes a snapshot of colors, override, brightness and effect, then selects hardware brightness 9 and effect 6 with override enabled. `RestoreAsync(CancellationToken cancellationToken = default)` explicitly restores in safe order; cancellation stops before or between complete exchanges and leaves the lease available for a retry. Disposal restores with no cancellation by default. `RgbControlOptions.RestoreStateOnDispose = false` opts out of automatic restoration. Neither mechanism can guarantee recovery after disconnection, transport faults, power loss, or another application's concurrent writes. `StateRestoreException.Failures` identifies failed steps and `CleanupError` exposes a simultaneous transport disposal failure; a failed request stream must be disposed/reopened, not blindly retried. Cancellation takes effect between complete HID requests, never midway between a request and its reply.

## Offline verification and packaging

These commands do not write to HID. Use a Python environment with `tests/requirements.txt` installed (for example, `/tmp/wobkey-sdk-tests` on Linux):

```sh
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release
python -m unittest discover -s tests -p 'test_sdk_conformance.py' -v
python -m unittest discover -s tests -v
node --experimental-vm-modules --test tests/test_signalrgb_v3.mjs
dotnet pack sdk/dotnet/src/Wobkey.Crush80.Sdk/Wobkey.Crush80.Sdk.csproj -c Release --output /tmp/wobkey-sdk-pack
```

The SDK is licensed under the [Apache License 2.0](LICENSE.txt). The NuGet package contains `LICENSE.txt`, this README, `THIRD-PARTY-NOTICES.md`, and a verbatim HidSharp license at `licenses/HidSharp-LICENSE.txt`. This license applies to `sdk/dotnet/`; it does not license firmware, vendor binaries, or other repository content.

## License

Copyright for SDK contributions remains with their respective contributors. The SDK source, sample, tests, and package under `sdk/dotnet/` are distributed under Apache-2.0. HidSharp remains copyright James F. Bellinger and is distributed under its own Apache-2.0 notice and license copy.
