# Crush 80 C# SDK Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a `net10.0` C# SDK that discovers the wired Crush 80, negotiates the per-key `PKRG` protocol, provides safe frame-oriented control with state restoration, and runs on Windows, Linux, and macOS through HidSharp.

**Architecture:** A public session/control-lease API owns lifecycle and request serialization. An internal version-1 protocol client encodes canonical 32-byte VIA payloads over an injectable `IHidTransport`; the default adapter uses HidSharp and handles report-ID-zero framing. Language-neutral conformance vectors keep the C# implementation aligned with the existing patched-firmware emulator and future language SDKs.

**Tech Stack:** C# 14, .NET 10, HidSharp 2.6.4, xUnit 2.9.3, Microsoft.NET.Test.Sdk 18.10.1, Python 3 `unittest`, Unicorn firmware emulator, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-26-crush80-csharp-sdk-design.md`

## Global Constraints

- Publish one managed package named `Wobkey.Crush80.Sdk`, targeting exactly `net10.0`.
- Pin HidSharp to `2.6.4`; add its Apache 2.0 attribution to package notices.
- Support only wired VID `0x320F`, PID `0x5055`, usage page `0xFF60`, usage `0x61`.
- Accept exactly `PKRG` protocol version `1`, LED count `92`, and chunk limit `8`.
- Send no SET request before the read-only capability handshake succeeds.
- Keep raw VIA packets internal; expose no general packet-send escape hatch.
- Use sequential acknowledged exchanges in the first release; no frame pipelining.
- Validate a complete frame or range before its first device mutation.
- Preserve request ordering; one written request must consume or time out its matching response.
- A timeout, malformed response, or mismatched response faults the session until disposal.
- Keep animation timing, effects, key names, zones, firmware flashing, VIA keymaps, wireless control, and SignalRGB management out of scope.
- Avoid per-chunk request, response, flattened-frame, and per-pixel allocations.
- Do not claim an OS is hardware-supported until the documented real-device smoke passes on that OS.

---

### Task 1: Create the SDK workspace and immutable public value types

**Files:**
- Create: `sdk/dotnet/Wobkey.Crush80.Sdk.sln`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Wobkey.Crush80.Sdk.csproj`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Properties/AssemblyInfo.cs`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Models/Rgb24.cs`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Models/Crush80DeviceDescriptor.cs`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Models/PerKeyRgbCapabilities.cs`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Models/RgbDeviceState.cs`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Models/RgbControlOptions.cs`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Models/Crush80SessionOptions.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Wobkey.Crush80.Sdk.Tests.csproj`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Models/Rgb24Tests.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Models/RgbDeviceStateTests.cs`

**Interfaces:**
- Produces: `readonly record struct Rgb24(byte Red, byte Green, byte Blue)`.
- Produces: `sealed record Crush80DeviceDescriptor(string Path, ushort VendorId, ushort ProductId, ushort UsagePage, ushort Usage, string? SerialNumber, string? ProductName)`.
- Produces: `sealed record PerKeyRgbCapabilities(byte ProtocolVersion, int LedCount, int ChunkLimit, bool Enabled)`.
- Produces: immutable `RgbDeviceState` with owned color storage, `ReadOnlyMemory<Rgb24> Colors`, `bool Enabled`, `byte Brightness`, and `byte Effect`.
- Produces: `RgbControlOptions` with `byte HardwareBrightness = 9` and `bool RestoreStateOnDispose = true`.
- Produces: `Crush80SessionOptions` with `TimeSpan ResponseTimeout = TimeSpan.FromSeconds(2)`.

- [ ] **Step 1: Create the solution and project files**

Create the solution with the SDK and test projects, then use these project settings:

```xml
<!-- sdk/dotnet/src/Wobkey.Crush80.Sdk/Wobkey.Crush80.Sdk.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>14.0</LangVersion>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <PackageId>Wobkey.Crush80.Sdk</PackageId>
    <Version>0.1.0</Version>
    <Authors>Wobkey Crush 80 Community</Authors>
    <Description>Managed SDK for the wired Wobkey Crush 80 per-key RGB firmware.</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="HidSharp" Version="2.6.4" />
  </ItemGroup>
</Project>
```

```xml
<!-- sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Wobkey.Crush80.Sdk.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Wobkey.Crush80.Sdk/Wobkey.Crush80.Sdk.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write failing value-type and ownership tests**

```csharp
// Models/Rgb24Tests.cs
using Wobkey.Crush80;

namespace Wobkey.Crush80.Sdk.Tests.Models;

public sealed class Rgb24Tests
{
    [Fact]
    public void UsesValueEquality()
    {
        Assert.Equal(new Rgb24(1, 2, 3), new Rgb24(1, 2, 3));
        Assert.NotEqual(new Rgb24(1, 2, 3), new Rgb24(1, 2, 4));
    }
}
```

```csharp
// Models/RgbDeviceStateTests.cs
using Wobkey.Crush80;

namespace Wobkey.Crush80.Sdk.Tests.Models;

public sealed class RgbDeviceStateTests
{
    [Fact]
    public void OwnsCapturedColors()
    {
        var source = Enumerable.Repeat(new Rgb24(1, 2, 3), 92).ToArray();
        var state = new RgbDeviceState(source, enabled: true, brightness: 9, effect: 6);

        source[0] = new Rgb24(9, 9, 9);

        Assert.Equal(new Rgb24(1, 2, 3), state.Colors.Span[0]);
        Assert.True(state.Enabled);
        Assert.Equal((byte)9, state.Brightness);
        Assert.Equal((byte)6, state.Effect);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run:

```bash
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --filter FullyQualifiedName~Models
```

Expected: build failure because `Rgb24` and `RgbDeviceState` do not exist.

- [ ] **Step 4: Implement the immutable public models**

Use namespace `Wobkey.Crush80` for all public SDK types. Implement `RgbDeviceState` with a private copied array:

```csharp
namespace Wobkey.Crush80;

public readonly record struct Rgb24(byte Red, byte Green, byte Blue);

public sealed class RgbDeviceState
{
    private readonly Rgb24[] _colors;

    internal RgbDeviceState(ReadOnlySpan<Rgb24> colors, bool enabled, byte brightness, byte effect)
    {
        if (colors.Length != 92)
            throw new ArgumentException("A captured Crush 80 state requires exactly 92 colors.", nameof(colors));
        _colors = colors.ToArray();
        Enabled = enabled;
        Brightness = brightness;
        Effect = effect;
    }

    public ReadOnlyMemory<Rgb24> Colors => _colors;
    public bool Enabled { get; }
    public byte Brightness { get; }
    public byte Effect { get; }
}
```

Implement the remaining records/options with constructor or init validation. `RgbControlOptions.HardwareBrightness` must reject values above `9`; `Crush80SessionOptions.ResponseTimeout` must be positive and finite.

Expose internals to the test assembly:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Wobkey.Crush80.Sdk.Tests")]
```

- [ ] **Step 5: Run the focused tests and build**

Run:

```bash
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --filter FullyQualifiedName~Models
dotnet build sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release
```

Expected: all model tests pass; build succeeds without warnings.

- [ ] **Step 6: Commit the workspace and models**

```bash
git add sdk/dotnet
git commit -m "feat(sdk): add .NET workspace and core models"
```

---

### Task 2: Add the shared PKRG version-1 conformance corpus

**Files:**
- Create: `sdk/conformance/pkrg-v1.json`
- Create: `tests/test_sdk_conformance.py`
- Modify: `docs/user/PER-KEY-RGB.md` in the VIA 11 wire-contract section

**Interfaces:**
- Produces: versioned JSON schema consumed by Python firmware tests and C# protocol tests.
- Produces: protocol cases with `request`, `expectedPrefix`, and optional `expectedStatus` byte arrays.
- Produces: named lifecycle sequences `acquireControl` and `restoreState` mirrored by the C# lifecycle tests.

- [ ] **Step 1: Write the failing Python conformance test**

```python
# tests/test_sdk_conformance.py
import json
import unittest
from pathlib import Path

from test_per_key_firmware import Machine, candidate

ROOT = Path(__file__).resolve().parents[1]
CORPUS = ROOT / "sdk/conformance/pkrg-v1.json"


class SdkConformanceTests(unittest.TestCase):
    def test_protocol_vectors_execute_against_patched_machine_code(self):
        corpus = json.loads(CORPUS.read_text())
        self.assertEqual(corpus["schemaVersion"], 1)
        self.assertEqual(corpus["protocol"], "PKRG")
        self.assertEqual(corpus["protocolVersion"], 1)

        for case in corpus["protocolCases"]:
            with self.subTest(case=case["name"]):
                reply = Machine(candidate()).request(case["request"])
                self.assertEqual(list(reply[:len(case["expectedPrefix"])]),
                                 case["expectedPrefix"])
                if "expectedStatus" in case:
                    self.assertEqual(reply[3], case["expectedStatus"])

```

- [ ] **Step 2: Run the test to verify it fails**

Run:

```bash
python -m unittest discover -s tests -p 'test_sdk_conformance.py' -v
```

Expected: error because `sdk/conformance/pkrg-v1.json` does not exist.

- [ ] **Step 3: Create concrete protocol vectors and lifecycle flows**

Create `pkrg-v1.json` with this initial corpus:

```json
{
  "schemaVersion": 1,
  "protocol": "PKRG",
  "protocolVersion": 1,
  "ledCount": 92,
  "chunkLimit": 8,
  "protocolCases": [
    {
      "name": "capabilities-disabled",
      "request": [8, 127, 0],
      "expectedPrefix": [8, 127, 0, 0, 80, 75, 82, 71, 1, 92, 8, 0],
      "expectedStatus": 0
    },
    {
      "name": "read-default-mode",
      "request": [8, 127, 1],
      "expectedPrefix": [8, 127, 1, 0, 0],
      "expectedStatus": 0
    },
    {
      "name": "enable-mode",
      "request": [7, 127, 1, 0, 1],
      "expectedPrefix": [7, 127, 1, 0, 1],
      "expectedStatus": 0
    },
    {
      "name": "write-complete-eight-led-chunk",
      "request": [7, 127, 2, 0, 0, 8, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23],
      "expectedPrefix": [7, 127, 2, 0, 0, 8, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23],
      "expectedStatus": 0
    },
    {
      "name": "read-final-four-led-chunk",
      "request": [8, 127, 2, 0, 88, 4],
      "expectedPrefix": [8, 127, 2, 0, 88, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
      "expectedStatus": 0
    },
    {
      "name": "reject-range-past-last-led",
      "request": [7, 127, 2, 0, 91, 2, 255, 0, 0, 0, 255, 0],
      "expectedPrefix": [7, 127, 2, 2],
      "expectedStatus": 2
    },
    {
      "name": "reject-invalid-mode",
      "request": [7, 127, 1, 0, 2],
      "expectedPrefix": [7, 127, 1, 3],
      "expectedStatus": 3
    },
    {
      "name": "reject-unsupported-operation",
      "request": [8, 127, 255],
      "expectedPrefix": [8, 127, 255, 1],
      "expectedStatus": 1
    }
  ],
  "clientCases": [
    {
      "name": "mismatched-operation-faults-session",
      "request": [7, 127, 1, 0, 1],
      "responsePrefix": [7, 127, 2, 0, 1],
      "expectedException": "ProtocolViolationException",
      "faultsSession": true
    },
    {
      "name": "timeout-faults-session",
      "request": [8, 127, 1],
      "response": "timeout",
      "expectedException": "ProtocolViolationException",
      "faultsSession": true
    }
  ],
  "sessionFlows": {
    "acquireControl": [
      "ReadFrame", "GetEnabled", "GetBrightness", "GetEffect",
      "SetEnabled:false", "WriteInitialFrame", "SetBrightness:9",
      "SetEffect:6", "SetEnabled:true"
    ],
    "restoreState": [
      "SetEnabled:false", "WriteSavedFrame", "SetBrightness:saved",
      "SetEffect:saved", "SetEnabled:saved"
    ]
  }
}
```

Add one sentence to the wire-contract documentation naming `sdk/conformance/pkrg-v1.json` as the cross-language observable contract. Do not replace the existing byte-level table.

- [ ] **Step 4: Run the conformance and existing firmware tests**

Run:

```bash
python -m unittest discover -s tests -p 'test_sdk_conformance.py' -v
python -m unittest discover -s tests -v
```

Expected: the conformance vectors pass against the generated patched machine code; the existing suite remains green.

- [ ] **Step 5: Commit the shared corpus**

```bash
git add sdk/conformance tests/test_sdk_conformance.py docs/user/PER-KEY-RGB.md
git commit -m "test(sdk): add PKRG v1 conformance corpus"
```

---

### Task 3: Implement packet encoding, response validation, and SDK exceptions

**Files:**
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Exceptions/Crush80SdkException.cs`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Exceptions/DeviceExceptions.cs`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Exceptions/ProtocolExceptions.cs`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Protocol/PkrgV1Codec.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Protocol/PkrgV1CodecTests.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Protocol/ConformanceCorpusTests.cs`
- Modify: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Wobkey.Crush80.Sdk.Tests.csproj`

**Interfaces:**
- Produces: internal `PkrgV1Codec` constants `PayloadLength = 32`, `Channel = 0x7F`, `LedCount = 92`, `ChunkLimit = 8`.
- Produces: request writers and response parsers for capabilities, mode, RGB chunks, OEM brightness, and OEM effect.
- Produces: `Crush80SdkException`, `DeviceNotFoundException`, `DeviceBusyException`, `IncompatibleFirmwareException`, `FirmwareRejectedRequestException`, `ProtocolViolationException`, `DeviceDisconnectedException`, `SessionFaultedException`, and `StateRestoreException`.

- [ ] **Step 1: Copy the corpus and write failing codec tests**

Add this item to the test project so the shared corpus has a stable runtime path:

```xml
<ItemGroup>
  <None Include="../../../conformance/pkrg-v1.json" Link="pkrg-v1.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Then add the codec tests:

```csharp
// Protocol/PkrgV1CodecTests.cs
using Wobkey.Crush80.Protocol;

namespace Wobkey.Crush80.Sdk.Tests.Protocol;

public sealed class PkrgV1CodecTests
{
    [Fact]
    public void EncodesFinalFourLedChunkAndClearsPadding()
    {
        Span<byte> request = stackalloc byte[PkrgV1Codec.PayloadLength];
        request.Fill(0xA5);
        var colors = new[]
        {
            new Rgb24(1, 2, 3), new Rgb24(4, 5, 6),
            new Rgb24(7, 8, 9), new Rgb24(10, 11, 12)
        };

        PkrgV1Codec.WriteRgbSetRequest(request, 88, colors);

        Assert.Equal(new byte[] { 7, 127, 2, 0, 88, 4 }, request[..6].ToArray());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 },
                     request[6..18].ToArray());
        Assert.All(request[18..].ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void RejectsMismatchedAcknowledgement()
    {
        Span<byte> request = stackalloc byte[32];
        PkrgV1Codec.WriteModeSetRequest(request, true);
        var reply = request.ToArray();
        reply[2] = 2;

        Assert.Throws<ProtocolViolationException>(
            () => PkrgV1Codec.ParseModeSetResponse(reply, true));
    }
}
```

```csharp
// Protocol/ConformanceCorpusTests.cs
using System.Text.Json;
using Wobkey.Crush80.Protocol;

namespace Wobkey.Crush80.Sdk.Tests.Protocol;

public sealed class ConformanceCorpusTests
{
    [Fact]
    public void CodecBuildsEveryConformanceRequest()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../../conformance/pkrg-v1.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var capability = document.RootElement.GetProperty("protocolCases")[0];
        Span<byte> request = stackalloc byte[32];

        PkrgV1Codec.WriteCapabilitiesRequest(request);

        Assert.Equal(capability.GetProperty("request").EnumerateArray().Select(x => x.GetByte()),
                     request[..3].ToArray());
    }
}
```

- [ ] **Step 2: Run the codec tests to verify they fail**

Run:

```bash
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --filter FullyQualifiedName~Protocol
```

Expected: build failure because codec and exception types do not exist.

- [ ] **Step 3: Implement exception types and the pure codec**

Use one base constructor carrying operation and device identity where available:

```csharp
namespace Wobkey.Crush80;

public abstract class Crush80SdkException : Exception
{
    protected Crush80SdkException(string message, string? operation = null,
        Crush80DeviceDescriptor? device = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Operation = operation;
        Device = device;
    }

    public string? Operation { get; }
    public Crush80DeviceDescriptor? Device { get; }
}
```

Implement the codec as pure span-based methods. Every request writer starts with `destination.Clear()`. Every parser first checks a 32-byte response, command, channel, operation, and status before reading payload bytes:

```csharp
internal static class PkrgV1Codec
{
    internal const int PayloadLength = 32;
    internal const byte Channel = 0x7F;
    internal const int LedCount = 92;
    internal const int ChunkLimit = 8;

    internal static void WriteCapabilitiesRequest(Span<byte> destination)
    {
        Start(destination, command: 8, operation: 0);
    }

    internal static void WriteRgbSetRequest(
        Span<byte> destination, int startIndex, ReadOnlySpan<Rgb24> colors)
    {
        ValidateRange(startIndex, colors.Length);
        if (colors.Length > ChunkLimit) throw new ArgumentOutOfRangeException(nameof(colors));
        Start(destination, command: 7, operation: 2);
        destination[4] = checked((byte)startIndex);
        destination[5] = checked((byte)colors.Length);
        for (var index = 0; index < colors.Length; index++)
        {
            destination[6 + index * 3] = colors[index].Red;
            destination[7 + index * 3] = colors[index].Green;
            destination[8 + index * 3] = colors[index].Blue;
        }
    }

    private static void Start(Span<byte> destination, byte command, byte operation)
    {
        if (destination.Length != PayloadLength)
            throw new ArgumentException("A VIA payload must contain exactly 32 bytes.", nameof(destination));
        destination.Clear();
        destination[0] = command;
        destination[1] = Channel;
        destination[2] = operation;
    }
}
```

Implement corresponding GET/SET methods for mode and RGB, OEM channel `3` GET/SET methods for brightness ID `1` and effect ID `2`, capability parsing, echoed-data validation, firmware status mapping (`1`, `2`, `3`), and argument validation.

- [ ] **Step 4: Run the protocol tests**

Run:

```bash
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --filter FullyQualifiedName~Protocol
```

Expected: all codec and corpus tests pass.

- [ ] **Step 5: Commit the codec**

```bash
git add sdk/dotnet/src/Wobkey.Crush80.Sdk/Exceptions sdk/dotnet/src/Wobkey.Crush80.Sdk/Protocol sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Protocol
git commit -m "feat(sdk): add PKRG packet codec"
```

---

### Task 4: Add the injectable transport and protocol client

**Files:**
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Transport/IHidTransport.cs`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Protocol/PkrgV1Client.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Support/FakeFirmwareTransport.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Protocol/PkrgV1ClientTests.cs`

**Interfaces:**
- Produces: `IHidTransport.Device`, `WriteAsync(ReadOnlyMemory<byte>, CancellationToken)`, `ReadAsync(Memory<byte>, TimeSpan, CancellationToken)`, and `IAsyncDisposable`.
- Produces: internal `PkrgV1Client` methods for capabilities, mode, RGB range, brightness, and effect.
- Consumes: `PkrgV1Codec` from Task 3.

- [ ] **Step 1: Write failing client tests using a stateful transport**

```csharp
// Protocol/PkrgV1ClientTests.cs
using Wobkey.Crush80.Protocol;
using Wobkey.Crush80.Sdk.Tests.Support;

namespace Wobkey.Crush80.Sdk.Tests.Protocol;

public sealed class PkrgV1ClientTests
{
    [Fact]
    public async Task WritesAndReadsAcrossChunkBoundaries()
    {
        await using var transport = new FakeFirmwareTransport();
        var client = new PkrgV1Client(transport, TimeSpan.FromSeconds(2));
        var colors = Enumerable.Range(0, 12)
            .Select(i => new Rgb24((byte)i, (byte)(255 - i), (byte)(i * 3)))
            .ToArray();

        await client.WriteRangeAsync(80, colors, CancellationToken.None);
        var readback = new Rgb24[12];
        await client.ReadRangeAsync(80, readback, CancellationToken.None);

        Assert.Equal(colors, readback);
        Assert.Equal(new[] { 8, 4 }, transport.RgbWriteCounts);
    }

    [Fact]
    public async Task FirmwareStatusDoesNotDesynchronizeClient()
    {
        await using var transport = new FakeFirmwareTransport();
        transport.RejectNextStatus = 2;
        var client = new PkrgV1Client(transport, TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<FirmwareRejectedRequestException>(
            async () => await client.SetEnabledAsync(true, CancellationToken.None));

        Assert.False(await client.GetEnabledAsync(CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:

```bash
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --filter FullyQualifiedName~PkrgV1ClientTests
```

Expected: build failure because transport and client types do not exist.

- [ ] **Step 3: Define the payload-oriented transport**

```csharp
namespace Wobkey.Crush80.Transport;

public interface IHidTransport : IAsyncDisposable
{
    Crush80DeviceDescriptor Device { get; }

    ValueTask WriteAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);

    ValueTask ReadAsync(
        Memory<byte> payload,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
```

Both methods require exactly 32 canonical payload bytes. Implementations throw standard argument exceptions for other lengths.

- [ ] **Step 4: Implement the stateful fake firmware transport**

The fake owns a 92-color array, mode, brightness, and effect. On `WriteAsync`, copy the request into an internal pending buffer and append a descriptive operation string to `Operations`. On `ReadAsync`, interpret the pending request, mutate state only for valid SET requests, and fill a 32-byte reply matching firmware version 1. Support these controls for failure tests:

```csharp
internal sealed class FakeFirmwareTransport : IHidTransport
{
    internal byte ProtocolVersion { get; set; } = 1;
    internal byte LedCount { get; set; } = 92;
    internal byte ChunkLimit { get; set; } = 8;
    private readonly byte[] _pending = new byte[32];
    private readonly TaskCompletionSource<bool> _firstWriteObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _releaseWrites = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _hasPending;

    private Rgb24[] _colors = new Rgb24[92];
    internal Rgb24[] Colors { get => _colors; set => _colors = (Rgb24[])value.Clone(); }
    public Crush80DeviceDescriptor Device { get; } = new(
        "fake://crush80-via", 0x320F, 0x5055, 0xFF60, 0x61, null, "Fake Crush 80");
    internal bool Enabled { get; set; }
    internal byte Brightness { get; set; } = 9;
    internal byte Effect { get; set; } = 7;
    internal byte? RejectNextStatus { get; set; }
    internal int? RejectRgbStartOnce { get; set; }
    internal bool? RejectModeValueOnce { get; set; }
    internal string? MalformOperationOnce { get; set; }
    internal bool TimeoutNextRead { get; set; }
    internal bool PauseAfterWrite { get; set; }
    internal bool CancelCallerAfterNextWrite { get; set; }
    internal CancellationTokenSource? CallerCancellation { get; set; }
    internal int PendingReplyCount => _hasPending ? 1 : 0;
    internal bool IsDisposed { get; private set; }
    internal Task FirstWriteObserved => _firstWriteObserved.Task;
    internal List<string> Operations { get; } = [];
    internal List<int> RgbWriteCounts { get; } = [];
    internal List<Rgb24> RgbWriteFirstColors { get; } = [];

    internal void ReleaseWrites() => _releaseWrites.TrySetResult(true);
}
```

The fake's `WriteAsync` requires 32 bytes, rejects a second pending request, copies into `_pending`, records the decoded operation, and sets `_hasPending`. When `PauseAfterWrite` is true, it signals `_firstWriteObserved` and waits on `_releaseWrites`. It optionally cancels `CallerCancellation` after the request is pending. `ReadAsync` requires 32 bytes and one pending request; `TimeoutNextRead` clears itself and throws `TimeoutException`. Otherwise it starts from an echoed request, applies this exact state machine, then clears `_hasPending`:

- GET capabilities returns `PKRG`, configured protocol version, LED count, chunk limit, and enabled flag.
- GET/SET mode reads or updates `Enabled`; `RejectModeValueOnce` returns status `3` without mutation and clears itself.
- GET RGB copies configured colors; SET RGB validates the firmware range, records count/first color, updates `Colors`, or returns status `2`. `RejectRgbStartOnce` returns status `2` for that start index and clears itself.
- OEM GET/SET brightness and effect read or update the configured values.
- `RejectNextStatus` overrides the next structurally valid response status and clears itself.
- `MalformOperationOnce` flips the response operation byte for the matching decoded operation and clears itself.
- Disposal sets `IsDisposed`; state-array assignment clones input.

- [ ] **Step 5: Implement the allocation-stable protocol client**

`PkrgV1Client` owns one request and one response array for its lifetime. Its caller guarantees serialized access. For every exchange:

```csharp
private async ValueTask ExchangeAsync(CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();

    // Never abandon half an exchange: a late reply could match the next command.
    await _transport.WriteAsync(_request, CancellationToken.None).ConfigureAwait(false);
    await _transport.ReadAsync(_response, _responseTimeout, CancellationToken.None)
        .ConfigureAwait(false);
}
```

Implement these exact methods: `ValueTask<PerKeyRgbCapabilities> GetCapabilitiesAsync(CancellationToken)`, `ValueTask<bool> GetEnabledAsync(CancellationToken)`, `ValueTask SetEnabledAsync(bool, CancellationToken)`, `ValueTask ReadRangeAsync(int, Memory<Rgb24>, CancellationToken)`, `ValueTask WriteRangeAsync(int, ReadOnlyMemory<Rgb24>, CancellationToken)`, `ValueTask<byte> GetBrightnessAsync(CancellationToken)`, `ValueTask SetBrightnessAsync(byte, CancellationToken)`, `ValueTask<byte> GetEffectAsync(CancellationToken)`, and `ValueTask SetEffectAsync(byte, CancellationToken)`. Range methods split at eight LEDs; reads write directly into caller-provided memory without a flattened temporary byte array.

Catch `TimeoutException` from transport reads and throw `ProtocolViolationException` with operation context and the timeout as `InnerException`; this is what causes the owning session to enter the faulted state.

- [ ] **Step 6: Run client and protocol tests**

Run:

```bash
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --filter "FullyQualifiedName~PkrgV1ClientTests|FullyQualifiedName~Protocol"
```

Expected: all focused tests pass.

- [ ] **Step 7: Commit the transport contract and protocol client**

```bash
git add sdk/dotnet/src/Wobkey.Crush80.Sdk/Transport sdk/dotnet/src/Wobkey.Crush80.Sdk/Protocol/PkrgV1Client.cs sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Support sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Protocol/PkrgV1ClientTests.cs
git commit -m "feat(sdk): add HID transport contract and protocol client"
```

---

### Task 5: Implement negotiated sessions, advanced operations, serialization, and faulting

**Files:**
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Session/Crush80RgbSession.cs`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Session/Crush80RgbAdvanced.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Session/SessionCompatibilityTests.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Session/AdvancedOperationsTests.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Session/SessionFaultTests.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Session/ClientConformanceTests.cs`

**Interfaces:**
- Produces: `Crush80RgbSession.OpenAsync(IHidTransport, Crush80SessionOptions?, CancellationToken)`.
- Produces: `Crush80RgbSession.Device`, `.Capabilities`, `.Advanced`, and `IAsyncDisposable`.
- Produces: advanced capture/restore, frame/range, mode, brightness, and effect methods.
- Consumes: `PkrgV1Client` and `IHidTransport` from Task 4.

- [ ] **Step 1: Write failing compatibility and no-write-before-handshake tests**

```csharp
// Session/SessionCompatibilityTests.cs
using Wobkey.Crush80.Sdk.Tests.Support;

namespace Wobkey.Crush80.Sdk.Tests.Session;

public sealed class SessionCompatibilityTests
{
    [Fact]
    public async Task RejectsUnknownProtocolBeforeAnySetRequest()
    {
        await using var transport = new FakeFirmwareTransport { ProtocolVersion = 2 };

        await Assert.ThrowsAsync<IncompatibleFirmwareException>(async () =>
            await Crush80RgbSession.OpenAsync(transport, cancellationToken: CancellationToken.None));

        Assert.Equal(new[] { "GetCapabilities" }, transport.Operations);
    }

    [Fact]
    public async Task AcceptsExactVersionOneCapabilities()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(
            transport, cancellationToken: CancellationToken.None);

        Assert.Equal(1, session.Capabilities.ProtocolVersion);
        Assert.Equal(92, session.Capabilities.LedCount);
        Assert.Equal(8, session.Capabilities.ChunkLimit);
    }
}
```

- [ ] **Step 2: Write failing advanced-operation and fault tests**

```csharp
// Session/AdvancedOperationsTests.cs
[Fact]
public async Task InvalidFrameLengthPerformsNoDeviceIo()
{
    await using var transport = new FakeFirmwareTransport();
    await using var session = await Crush80RgbSession.OpenAsync(transport);
    var operationsBefore = transport.Operations.Count;

    await Assert.ThrowsAsync<ArgumentException>(async () =>
        await session.Advanced.WriteFrameAsync(new Rgb24[91]));

    Assert.Equal(operationsBefore, transport.Operations.Count);
}
```

```csharp
// Session/SessionFaultTests.cs
[Fact]
public async Task TimeoutFaultsSessionUntilDispose()
{
    await using var transport = new FakeFirmwareTransport();
    await using var session = await Crush80RgbSession.OpenAsync(transport);
    transport.TimeoutNextRead = true;

    await Assert.ThrowsAsync<ProtocolViolationException>(async () =>
        await session.Advanced.GetEnabledAsync());

    await Assert.ThrowsAsync<SessionFaultedException>(async () =>
        await session.Advanced.GetBrightnessAsync());
}
```

```csharp
// Session/ClientConformanceTests.cs
public sealed class ClientConformanceTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "pkrg-v1.json")));
        foreach (var item in document.RootElement.GetProperty("clientCases").EnumerateArray())
        {
            yield return
            [
                item.GetProperty("name").GetString()!,
                item.GetProperty("expectedException").GetString()!,
                item.GetProperty("faultsSession").GetBoolean()
            ];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ExecutesSharedClientFailureCases(
        string name, string expectedException, bool faultsSession)
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);

        Func<Task> operation = name switch
        {
            "mismatched-operation-faults-session" => async () =>
            {
                transport.MalformOperationOnce = "SetEnabled:true";
                await session.Advanced.SetEnabledAsync(true);
            },
            "timeout-faults-session" => async () =>
            {
                transport.TimeoutNextRead = true;
                await session.Advanced.GetEnabledAsync();
            },
            _ => throw new InvalidDataException($"Unknown conformance case: {name}")
        };

        var error = await Record.ExceptionAsync(operation);
        Assert.Equal(expectedException, error?.GetType().Name);
        if (faultsSession)
        {
            await Assert.ThrowsAsync<SessionFaultedException>(async () =>
                await session.Advanced.GetBrightnessAsync());
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run:

```bash
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --filter FullyQualifiedName~Session
```

Expected: build failure because session and advanced types do not exist.

- [ ] **Step 4: Implement negotiated session opening**

Use a `SemaphoreSlim(1, 1)` as the session operation gate. `OpenAsync` constructs the client, sends only capabilities GET, verifies signature/version/count/chunk limit, and disposes the transport if opening fails:

```csharp
public static async ValueTask<Crush80RgbSession> OpenAsync(
    IHidTransport transport,
    Crush80SessionOptions? options = null,
    CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(transport);
    options ??= new Crush80SessionOptions();
    var client = new PkrgV1Client(transport, options.ResponseTimeout);
    try
    {
        var capabilities = await client.GetCapabilitiesAsync(cancellationToken)
            .ConfigureAwait(false);
        ValidateCapabilities(capabilities, transport.Device);
        return new Crush80RgbSession(transport, client, capabilities);
    }
    catch
    {
        await transport.DisposeAsync().ConfigureAwait(false);
        throw;
    }
}
```

- [ ] **Step 5: Implement serialized advanced operations**

Create one internal executor used by all session and lease operations:

```csharp
internal async ValueTask<T> ExecuteAsync<T>(
    string operation,
    Func<PkrgV1Client, CancellationToken, ValueTask<T>> action,
    CancellationToken cancellationToken)
{
    ThrowIfDisposedOrFaulted();
    await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        ThrowIfDisposedOrFaulted();
        return await action(_client, cancellationToken).ConfigureAwait(false);
    }
    catch (FirmwareRejectedRequestException)
    {
        throw;
    }
    catch (ArgumentException)
    {
        throw;
    }
    catch (Exception exception) when (ShouldFault(exception))
    {
        _fault = exception;
        throw;
    }
    finally
    {
        _operationGate.Release();
    }
}
```

`Crush80RgbAdvanced` delegates through this executor. Validate full-frame size, ranges, brightness `0..9`, and effect `0..18` before entering the executor so invalid calls do no I/O.

Implement `CaptureStateAsync` in the specified order: frame, enabled, brightness, effect. Implement `RestoreStateAsync` in the specified order: disable, saved frame, saved brightness, saved effect, saved enabled. Compound operations call `PkrgV1Client` directly inside one executor invocation; they must not call public advanced methods and recursively acquire the session gate.

- [ ] **Step 6: Run session tests**

Run:

```bash
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --filter FullyQualifiedName~Session
```

Expected: compatibility, advanced validation, session-fault, and shared client conformance tests pass.

- [ ] **Step 7: Commit sessions and advanced operations**

```bash
git add sdk/dotnet/src/Wobkey.Crush80.Sdk/Session sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Session
git commit -m "feat(sdk): add negotiated RGB sessions"
```

---

### Task 6: Add HidSharp discovery and report-ID-zero transport framing

**Files:**
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Transport/HidDeviceMatcher.cs`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Transport/HidReportFraming.cs`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Transport/HidSharpTransport.cs`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Transport/Crush80DeviceLocator.cs`
- Modify: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Session/Crush80RgbSession.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Transport/HidDeviceMatcherTests.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Transport/HidReportFramingTests.cs`

**Interfaces:**
- Produces: public `Crush80DeviceLocator.Enumerate()`.
- Produces: default `Crush80RgbSession.OpenAsync(Crush80DeviceDescriptor, ...)` and `OpenFirstAsync(...)`.
- Produces: internal `HidSharpTransport.Open(Crush80DeviceDescriptor)`.
- Consumes: HidSharp 2.6.4 and `IHidTransport`.

- [ ] **Step 1: Write failing device matcher and framing tests**

```csharp
// Transport/HidDeviceMatcherTests.cs
using Wobkey.Crush80.Transport;

namespace Wobkey.Crush80.Sdk.Tests.Transport;

public sealed class HidDeviceMatcherTests
{
    [Theory]
    [InlineData(0x320F, 0x5055, 0xFF60, 0x61, true)]
    [InlineData(0x320F, 0x5055, 0xFFEF, 0x61, false)]
    [InlineData(0x320F, 0x5055, 0xFF1C, 0x61, false)]
    [InlineData(0x320F, 0x5088, 0xFF60, 0x61, false)]
    public void MatchesOnlyWiredViaInterface(
        int vid, int pid, int usagePage, int usage, bool expected)
    {
        Assert.Equal(expected, HidDeviceMatcher.IsWiredVia(
            vid, pid, (ushort)usagePage, (ushort)usage));
    }
}
```

```csharp
// Transport/HidReportFramingTests.cs
[Fact]
public void AddsAndRemovesReportIdZero()
{
    var payload = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    Span<byte> report = stackalloc byte[33];
    Span<byte> decoded = stackalloc byte[32];

    HidReportFraming.Encode(payload, report);
    HidReportFraming.Decode(report, decoded);

    Assert.Equal(0, report[0]);
    Assert.Equal(payload, decoded.ToArray());
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:

```bash
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --filter FullyQualifiedName~Transport
```

Expected: build failure because matcher and framing helpers do not exist.

- [ ] **Step 3: Implement pure matching and framing helpers**

Decode HidSharp's top-level usage as high 16 bits usage page and low 16 bits usage:

```csharp
internal static bool IsWiredVia(int vendorId, int productId, uint topLevelUsage)
{
    var usagePage = checked((ushort)(topLevelUsage >> 16));
    var usage = checked((ushort)topLevelUsage);
    return IsWiredVia(vendorId, productId, usagePage, usage);
}
```

`HidReportFraming.Encode` requires 32-byte payload and 33-byte report, clears the report, writes report ID zero at byte 0, and copies payload to bytes 1..32. `Decode` requires a 33-byte report whose byte 0 is zero and copies bytes 1..32.

- [ ] **Step 4: Implement locator and descriptor creation**

Enumerate `DeviceList.Local.GetHidDevices(0x320F, 0x5055)`. For each device, call `GetTopLevelUsage()` and keep only the wired VIA interface. Build descriptors from `DevicePath`, VID/PID, decoded usage, and best-effort serial/product strings. A failure to read optional strings leaves them null; a failure to read top-level usage excludes the device rather than falling back to VID/PID.

- [ ] **Step 5: Implement HidSharp transport**

Open by exact descriptor path, validate maximum input/output report lengths are `33`, and use:

```csharp
var configuration = new OpenConfiguration();
configuration.SetOption(OpenOption.Exclusive, true);
var stream = device.Open(configuration);
```

Own reusable 33-byte input/output report arrays. Set `ReadTimeout` and `WriteTimeout` from the session timeout for each exchange. Use the stream's asynchronous `ReadAsync`/`WriteAsync`; require an exact 33-byte read. Translate `UnauthorizedAccessException`, `IOException`, timeout, and removed-device failures into the SDK exception model with the descriptor attached. Disposal closes the stream exactly once.

- [ ] **Step 6: Add default session opening**

`OpenAsync(descriptor, options, token)` re-enumerates by exact path, opens `HidSharpTransport`, then delegates to injected `OpenAsync`. `OpenFirstAsync` enumerates and throws `DeviceNotFoundException` when no matching descriptor exists.

- [ ] **Step 7: Run transport tests and build on the current OS**

Run:

```bash
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --filter FullyQualifiedName~Transport
dotnet build sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release
```

Expected: pure adapter tests pass and the HidSharp adapter compiles.

- [ ] **Step 8: Commit the default transport**

```bash
git add sdk/dotnet/src/Wobkey.Crush80.Sdk/Transport sdk/dotnet/src/Wobkey.Crush80.Sdk/Session/Crush80RgbSession.cs sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Transport
git commit -m "feat(sdk): add cross-platform HidSharp transport"
```

---

### Task 7: Implement control acquisition, state ownership, and normal restoration

**Files:**
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Session/RgbControlLease.cs`
- Modify: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Session/Crush80RgbSession.cs`
- Modify: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Session/Crush80RgbAdvanced.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Session/ControlAcquisitionTests.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Session/RestorationTests.cs`
- Modify: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Support/FakeFirmwareTransport.cs`

**Interfaces:**
- Produces: `Crush80RgbSession.AcquireControlAsync(ReadOnlyMemory<Rgb24>, RgbControlOptions?, CancellationToken)`.
- Produces: one active `RgbControlLease` per session.
- Produces: `RgbControlLease.RestoreAsync`, `.Capabilities`, `.Enabled`, and `IAsyncDisposable`.
- Consumes: lifecycle sequences from `sdk/conformance/pkrg-v1.json`.

- [ ] **Step 1: Write failing acquisition-order and no-mutation-on-snapshot-failure tests**

```csharp
// Session/ControlAcquisitionTests.cs
[Fact]
public async Task CapturesEverythingBeforeFirstMutation()
{
    await using var transport = new FakeFirmwareTransport
    {
        Enabled = true,
        Brightness = 4,
        Effect = 7
    };
    await using var session = await Crush80RgbSession.OpenAsync(transport);
    transport.Operations.Clear();

    await using var lease = await session.AcquireControlAsync(
        new Rgb24[92], cancellationToken: CancellationToken.None);

    var expectedReads = Enumerable.Range(0, 12).Select(chunk =>
    {
        var start = chunk * 8;
        return $"ReadRgb:{start}:{Math.Min(8, 92 - start)}";
    });
    var expectedWrites = Enumerable.Range(0, 12).Select(chunk =>
    {
        var start = chunk * 8;
        return $"WriteRgb:{start}:{Math.Min(8, 92 - start)}";
    });
    var expected = expectedReads
        .Concat(["GetEnabled", "GetBrightness", "GetEffect", "SetEnabled:false"])
        .Concat(expectedWrites)
        .Concat(["SetBrightness:9", "SetEffect:6", "SetEnabled:true"]);

    Assert.Equal(expected, transport.Operations);
}

[Fact]
public async Task SnapshotFailureSendsNoMutation()
{
    await using var transport = new FakeFirmwareTransport
    {
        MalformOperationOnce = "GetEffect"
    };
    await using var session = await Crush80RgbSession.OpenAsync(transport);
    transport.Operations.Clear();

    await Assert.ThrowsAsync<ProtocolViolationException>(async () =>
        await session.AcquireControlAsync(new Rgb24[92]));

    Assert.DoesNotContain(transport.Operations, operation => operation.StartsWith("Set"));
    Assert.DoesNotContain(transport.Operations, operation => operation.StartsWith("WriteRgb"));
}
```

- [ ] **Step 2: Write failing restoration test**

```csharp
// Session/RestorationTests.cs
[Fact]
public async Task DisposalRestoresSavedStateInSafeOrder()
{
    var saved = Enumerable.Repeat(new Rgb24(10, 20, 30), 92).ToArray();
    await using var transport = new FakeFirmwareTransport
    {
        Colors = saved,
        Enabled = true,
        Brightness = 4,
        Effect = 7
    };
    await using var session = await Crush80RgbSession.OpenAsync(transport);
    var lease = await session.AcquireControlAsync(new Rgb24[92]);
    transport.Operations.Clear();

    await lease.DisposeAsync();

    Assert.Equal("SetEnabled:false", transport.Operations[0]);
    Assert.Equal("SetBrightness:4", transport.Operations[^3]);
    Assert.Equal("SetEffect:7", transport.Operations[^2]);
    Assert.Equal("SetEnabled:true", transport.Operations[^1]);
    Assert.Equal(saved, transport.Colors);
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run:

```bash
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --filter "FullyQualifiedName~ControlAcquisitionTests|FullyQualifiedName~RestorationTests"
```

Expected: build failure because `RgbControlLease` and `AcquireControlAsync` do not exist.

- [ ] **Step 4: Implement one-lease ownership and acquisition**

Validate the entire initial frame and options before entering the session gate. Under one direct `PkrgV1Client` operation, reject a second active lease, capture state, disable mode, write all 92 initial colors, set brightness, select effect 6, enable mode, and publish the lease only after all steps pass. Do not call `session.Advanced` from inside acquisition because that would recursively acquire the same gate.

If mutation begins and a subsequent step fails, run the same direct-client restoration core before surfacing failure. Store the saved `RgbDeviceState` and an owned copy of the acknowledged initial frame in the lease.

- [ ] **Step 5: Implement normal restoration and advanced-operation restrictions**

While a lease is active, advanced mutation and `RestoreStateAsync` throw `InvalidOperationException`; advanced reads remain serialized and allowed. `RestoreAsync` runs once under the session gate in the exact safe order and marks the lease restored only after every step succeeds. Successful explicit restore makes disposal a no-op. Lease operations after restore/dispose throw `ObjectDisposedException`.

- [ ] **Step 6: Run acquisition and restoration tests**

Run:

```bash
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --filter "FullyQualifiedName~ControlAcquisitionTests|FullyQualifiedName~RestorationTests"
```

Expected: acquisition order, single-lease ownership, snapshot failure, and normal restoration tests pass.

- [ ] **Step 7: Commit the control lifecycle**

```bash
git add sdk/dotnet/src/Wobkey.Crush80.Sdk/Session sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Session/ControlAcquisitionTests.cs sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Session/RestorationTests.cs
git commit -m "feat(sdk): add safe RGB control leases"
```

---

### Task 8: Add cached frame writes, range writes, fill, reads, and queued concurrency

**Files:**
- Modify: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Session/RgbControlLease.cs`
- Modify: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Session/Crush80RgbSession.cs`
- Modify: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Support/FakeFirmwareTransport.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Session/FrameWriteTests.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Session/ConcurrencyTests.cs`

**Interfaces:**
- Produces: `RgbControlLease.WriteFrameAsync`, `WriteRangeAsync`, `FillAsync`, `ReadFrameAsync`, and `SetHardwareBrightnessAsync`.
- Produces: acknowledged eight-LED chunk cache.
- Produces: FIFO serialization of complete logical operations.

- [ ] **Step 1: Write failing changed-chunk and partial-failure tests**

```csharp
// Session/FrameWriteTests.cs
[Fact]
public async Task WritesOnlyChangedChunksAndFinalFourLedChunk()
{
    await using var transport = new FakeFirmwareTransport();
    await using var session = await Crush80RgbSession.OpenAsync(transport);
    await using var lease = await session.AcquireControlAsync(new Rgb24[92]);
    transport.Operations.Clear();

    var frame = new Rgb24[92];
    frame[0] = new Rgb24(255, 0, 0);
    frame[91] = new Rgb24(0, 0, 255);
    await lease.WriteFrameAsync(frame);

    Assert.Equal(new[] { "WriteRgb:0:8", "WriteRgb:88:4" }, transport.Operations);

    transport.Operations.Clear();
    await lease.WriteFrameAsync(frame);
    Assert.Empty(transport.Operations);
}

[Fact]
public async Task FailedChunkIsRetriedButAcknowledgedChunkIsNot()
{
    await using var transport = new FakeFirmwareTransport();
    await using var session = await Crush80RgbSession.OpenAsync(transport);
    await using var lease = await session.AcquireControlAsync(new Rgb24[92]);
    var frame = new Rgb24[92];
    frame[0] = new Rgb24(1, 2, 3);
    frame[8] = new Rgb24(4, 5, 6);
    transport.RejectRgbStartOnce = 8;

    await Assert.ThrowsAsync<FirmwareRejectedRequestException>(async () =>
        await lease.WriteFrameAsync(frame));

    transport.Operations.Clear();
    await lease.WriteFrameAsync(frame);
    Assert.Equal(new[] { "WriteRgb:8:8" }, transport.Operations);
}
```

- [ ] **Step 2: Write failing concurrency test**

```csharp
// Session/ConcurrencyTests.cs
[Fact]
public async Task CompleteFrameOperationsDoNotInterleave()
{
    await using var transport = new FakeFirmwareTransport();
    await using var session = await Crush80RgbSession.OpenAsync(transport);
    await using var lease = await session.AcquireControlAsync(new Rgb24[92]);
    transport.Operations.Clear();
    transport.RgbWriteFirstColors.Clear();
    transport.PauseAfterWrite = true;

    var first = Enumerable.Repeat(new Rgb24(1, 0, 0), 92).ToArray();
    var second = Enumerable.Repeat(new Rgb24(0, 1, 0), 92).ToArray();
    var firstTask = lease.WriteFrameAsync(first).AsTask();
    await transport.FirstWriteObserved;
    var secondTask = lease.WriteFrameAsync(second).AsTask();

    transport.ReleaseWrites();
    await Task.WhenAll(firstTask, secondTask);

    Assert.Equal(24, transport.RgbWriteFirstColors.Count);
    Assert.All(transport.RgbWriteFirstColors.Take(12), color => Assert.Equal(new Rgb24(1, 0, 0), color));
    Assert.All(transport.RgbWriteFirstColors.Skip(12), color => Assert.Equal(new Rgb24(0, 1, 0), color));
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run:

```bash
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --filter "FullyQualifiedName~FrameWriteTests|FullyQualifiedName~ConcurrencyTests"
```

Expected: failures because lease frame methods and cache behavior are incomplete.

- [ ] **Step 4: Implement cached frame and range writes**

Validate caller memory length before entering the session gate. Copy no full frame for transmission; compare caller `ReadOnlyMemory<Rgb24>.Span` with the owned acknowledged cache inside the serialized operation. For each changed chunk:

```csharp
for (var start = 0; start < PkrgV1Codec.LedCount; start += PkrgV1Codec.ChunkLimit)
{
    var count = Math.Min(PkrgV1Codec.ChunkLimit, PkrgV1Codec.LedCount - start);
    var source = colors.Slice(start, count);
    if (source.Span.SequenceEqual(_acknowledgedFrame.AsSpan(start, count))) continue;

    source.Span.CopyTo(_chunkScratch);
    await client.WriteRangeAsync(start, _chunkScratch.AsMemory(0, count), token)
        .ConfigureAwait(false);
    _chunkScratch.AsSpan(0, count).CopyTo(_acknowledgedFrame.AsSpan(start, count));
}
```

Because spans cannot cross `await`, recompute spans after each await or copy only the current eight-color chunk into a reusable lease-owned eight-color scratch array before calling the client. Do not allocate a new chunk array per iteration.

`WriteRangeAsync` updates only intersecting cache entries after each acknowledged chunk. `FillAsync` fills a lease-owned 92-color reusable buffer, then submits it through the same cached frame path. `ReadFrameAsync` requires exactly 92 destination slots and reads supplied RGB values before brightness scaling. `SetHardwareBrightnessAsync` validates `0..9`.

- [ ] **Step 5: Preserve logical-operation FIFO ordering**

Use the existing session gate around the whole frame/range/fill operation. Assign fake-transport frame labels only in tests; production code has no frame identifiers. Argument validation occurs before queueing; device operations are serialized in invocation order after validation.

- [ ] **Step 6: Run frame and concurrency tests**

Run:

```bash
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --filter "FullyQualifiedName~FrameWriteTests|FullyQualifiedName~ConcurrencyTests"
```

Expected: changed chunks only, final four-slot handling, partial acknowledgement cache behavior, and non-interleaving operations pass. Review the implementation to confirm request/response arrays and the eight-color scratch buffer are owned and reused rather than allocated per chunk.

- [ ] **Step 7: Commit frame operations**

```bash
git add sdk/dotnet/src/Wobkey.Crush80.Sdk/Session sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Support/FakeFirmwareTransport.cs sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Session/FrameWriteTests.cs sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Session/ConcurrencyTests.cs
git commit -m "feat(sdk): add cached frame operations"
```

---

### Task 9: Complete restoration failure reporting, disposal, and cancellation semantics

**Files:**
- Modify: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Exceptions/ProtocolExceptions.cs`
- Modify: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Session/Crush80RgbSession.cs`
- Modify: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Session/RgbControlLease.cs`
- Modify: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Support/FakeFirmwareTransport.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Session/RestorationFailureTests.cs`
- Create: `sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests/Session/CancellationTests.cs`

**Interfaces:**
- Produces: `StateRestoreException.Failures`, a read-only list of named restoration failures.
- Produces: session disposal that attempts configured active-lease restoration and always closes transport.
- Produces: cancellation only before or between complete exchanges.

- [ ] **Step 1: Write failing restoration aggregation tests**

```csharp
// Session/RestorationFailureTests.cs
[Fact]
public async Task RestorationAttemptsEverySafeRemainingStep()
{
    await using var transport = new FakeFirmwareTransport
    {
        Enabled = true,
        Brightness = 4,
        Effect = 7
    };
    await using var session = await Crush80RgbSession.OpenAsync(transport);
    var lease = await session.AcquireControlAsync(new Rgb24[92]);
    transport.RejectModeValueOnce = false;
    transport.RejectRgbStartOnce = 0;
    transport.Operations.Clear();

    var error = await Assert.ThrowsAsync<StateRestoreException>(async () =>
        await lease.RestoreAsync());

    Assert.Equal(new[] { "OverrideDisable", "Colors" },
                 error.Failures.Select(failure => failure.Field));
    Assert.Contains("SetBrightness:4", transport.Operations);
    Assert.Contains("SetEffect:7", transport.Operations);
    Assert.Contains("SetEnabled:true", transport.Operations);
}

[Fact]
public async Task SessionDisposalClosesTransportWhenRestoreFails()
{
    var transport = new FakeFirmwareTransport();
    var session = await Crush80RgbSession.OpenAsync(transport);
    await session.AcquireControlAsync(new Rgb24[92]);
    transport.RejectRgbStartOnce = 0;

    await Assert.ThrowsAsync<StateRestoreException>(async () =>
        await session.DisposeAsync());

    Assert.True(transport.IsDisposed);
}
```

- [ ] **Step 2: Write failing post-write cancellation test**

```csharp
// Session/CancellationTests.cs
[Fact]
public async Task CancellationAfterWriteStillConsumesMatchingReply()
{
    await using var transport = new FakeFirmwareTransport
    {
        CancelCallerAfterNextWrite = true
    };
    await using var session = await Crush80RgbSession.OpenAsync(transport);
    using var cancellation = new CancellationTokenSource();
    transport.CallerCancellation = cancellation;

    await session.Advanced.SetEnabledAsync(true, cancellation.Token);

    Assert.True(await session.Advanced.GetEnabledAsync());
    Assert.Equal(0, transport.PendingReplyCount);
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run:

```bash
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --filter "FullyQualifiedName~RestorationFailureTests|FullyQualifiedName~CancellationTests"
```

Expected: restoration stops too early or lacks aggregate details; cancellation behavior is incomplete.

- [ ] **Step 4: Implement named restoration failure aggregation**

Define:

```csharp
public sealed record StateRestoreFailure(string Field, Exception Error);

public sealed class StateRestoreException : Crush80SdkException
{
    public StateRestoreException(
        IReadOnlyList<StateRestoreFailure> failures,
        Exception? originalOperationFailure = null)
        : base("The keyboard state could not be restored completely.",
            operation: "RestoreState", innerException: originalOperationFailure)
    {
        Failures = failures.ToArray();
    }

    public IReadOnlyList<StateRestoreFailure> Failures { get; }
}
```

Use the exact field names `OverrideDisable`, `Colors`, `Brightness`, `Effect`, and `OverrideEnable`.

Restoration first attempts disable. If disable fails because the stream is faulted/disconnected, record it and still attempt only operations whose protocol alignment remains trustworthy. For acknowledged firmware rejections, continue all remaining steps. For timeout/malformed response, the session is faulted; record unattempted fields as failures whose error is `SessionFaultedException` rather than issuing unsafe commands.

- [ ] **Step 5: Implement deterministic disposal**

Session disposal must:

1. Prevent new operations.
2. Wait for the active complete exchange.
3. Ask the active lease to restore if configured and not restored.
4. Dispose the transport in `finally`.
5. Throw `StateRestoreException` after transport closure when restoration was incomplete.
6. Be idempotent after its first completion.

Lease disposal delegates restoration to the session when configured. If `RestoreStateOnDispose` is false, it releases lease ownership without changing device state.

- [ ] **Step 6: Enforce cancellation boundaries**

Check caller cancellation before entering the operation gate and before each new request. Once an exchange starts, pass `CancellationToken.None` to both transport write and read so a partial exchange cannot be abandoned. Check caller cancellation again only before starting the next request. A timeout/malformed response faults the session; ordinary cancellation before a request does not.

- [ ] **Step 7: Run the focused and complete .NET suites**

Run:

```bash
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --filter "FullyQualifiedName~RestorationFailureTests|FullyQualifiedName~CancellationTests"
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release
```

Expected: all restoration, cancellation, lifecycle, protocol, and transport tests pass.

- [ ] **Step 8: Commit failure and disposal semantics**

```bash
git add sdk/dotnet/src/Wobkey.Crush80.Sdk sdk/dotnet/tests/Wobkey.Crush80.Sdk.Tests
git commit -m "feat(sdk): harden restoration and disposal"
```

---

### Task 10: Add sample, package notices, documentation, CI, and release smoke instructions

**Files:**
- Create: `sdk/dotnet/samples/Wobkey.Crush80.Sample/Wobkey.Crush80.Sample.csproj`
- Create: `sdk/dotnet/samples/Wobkey.Crush80.Sample/Program.cs`
- Create: `sdk/dotnet/README.md`
- Create: `sdk/dotnet/src/Wobkey.Crush80.Sdk/THIRD-PARTY-NOTICES.md`
- Create: `.github/workflows/sdk-dotnet.yml`
- Modify: `sdk/dotnet/src/Wobkey.Crush80.Sdk/Wobkey.Crush80.Sdk.csproj`
- Modify: `sdk/dotnet/Wobkey.Crush80.Sdk.sln`
- Modify: `README.md:5-21,27-58`
- Modify: `docs/user/PER-KEY-RGB.md` after the host CLI usage section

**Interfaces:**
- Produces: buildable sample with `--list`, `--smoke`, and `--help` commands.
- Produces: Windows/Linux/macOS SDK CI.
- Produces: package README and Apache 2.0 third-party notice.

- [ ] **Step 1: Write the sample project and help-path smoke**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Wobkey.Crush80.Sdk/Wobkey.Crush80.Sdk.csproj" />
  </ItemGroup>
</Project>
```

`Program.cs` behavior:

```csharp
if (args is ["--help"] or [])
{
    Console.WriteLine("Usage: Wobkey.Crush80.Sample --list | --smoke");
    return 0;
}

if (args is ["--list"])
{
    foreach (var device in Crush80DeviceLocator.Enumerate())
        Console.WriteLine($"{device.ProductName ?? "Crush 80"}: {device.Path}");
    return 0;
}

if (args is not ["--smoke"])
{
    Console.Error.WriteLine("Unknown command. Use --help.");
    return 2;
}
```

For `--smoke`, open the first device, acquire control with a black initial frame, set Esc red and F1 green for two seconds, read back all 92 supplied values, explicitly restore, and report success only after restoration. Handle Ctrl+C through a cancellation token. Print a warning that the command performs temporary lighting writes and requires per-key firmware; require the user to type `SMOKE` before acquisition.

- [ ] **Step 2: Add SDK and package documentation**

`sdk/dotnet/README.md` must include:

- Firmware prerequisite and exact supported interface.
- Minimal `await using` control-lease example.
- Enumeration and explicit opening.
- `session.Advanced` positioning.
- Linux udev installation using `hardware/udev/99-wobkey-crush80.rules`.
- macOS permission/sandbox caveat.
- Conflicts with SignalRGB, VIA, Python tools, and other sessions.
- Volatile state, non-atomic frames, no FPS guarantee, sequential protocol v1 behavior.
- Restore/disconnect failure handling.
- Statement that Windows/Linux/macOS support claims require recorded hardware smoke.

Add an SDK section to the root README and add `sdk/` to its repository tree. Add a short link from `docs/user/PER-KEY-RGB.md` to the SDK README without rewriting the existing Python instructions.

- [ ] **Step 3: Package notices and README**

Add HidSharp's copyright and Apache License 2.0 notice to `THIRD-PARTY-NOTICES.md`. Update the SDK project:

```xml
<PropertyGroup>
  <PackageReadmeFile>README.md</PackageReadmeFile>
</PropertyGroup>
<ItemGroup>
  <None Include="../../README.md" Pack="true" PackagePath="README.md" />
  <None Include="THIRD-PARTY-NOTICES.md" Pack="true" PackagePath="" />
</ItemGroup>
```

Do not invent a repository license or `PackageLicenseExpression`; packaging remains internal until the repository owner selects the project license.

- [ ] **Step 4: Add cross-platform CI**

Create `.github/workflows/sdk-dotnet.yml`:

```yaml
name: Test .NET SDK

on:
  push:
    paths:
      - "sdk/**"
      - "tests/test_sdk_conformance.py"
      - "tests/test_per_key_firmware.py"
      - ".github/workflows/sdk-dotnet.yml"
  pull_request:
    paths:
      - "sdk/**"
      - "tests/test_sdk_conformance.py"
      - "tests/test_per_key_firmware.py"
      - ".github/workflows/sdk-dotnet.yml"

permissions:
  contents: read

jobs:
  dotnet:
    strategy:
      fail-fast: false
      matrix:
        os: [ubuntu-latest, windows-latest, macos-latest]
    runs-on: ${{ matrix.os }}
    timeout-minutes: 15
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - run: dotnet restore sdk/dotnet/Wobkey.Crush80.Sdk.sln
      - run: dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release --no-restore
      - run: dotnet build sdk/dotnet/samples/Wobkey.Crush80.Sample/Wobkey.Crush80.Sample.csproj -c Release --no-restore

  firmware-conformance:
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-python@v5
        with:
          python-version: "3.13"
      - run: python -m pip install -r tests/requirements.txt
      - run: python -m unittest discover -s tests -p 'test_sdk_conformance.py' -v
```

Add the sample project to the solution so the restore step covers it before `--no-restore` build.

- [ ] **Step 5: Run documentation-safe smoke, full tests, and package build**

Run:

```bash
dotnet run --project sdk/dotnet/samples/Wobkey.Crush80.Sample/Wobkey.Crush80.Sample.csproj -- --help
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release
python -m unittest discover -s tests -p 'test_sdk_conformance.py' -v
python -m unittest discover -s tests -v
dotnet pack sdk/dotnet/src/Wobkey.Crush80.Sdk/Wobkey.Crush80.Sdk.csproj -c Release --output /tmp/wobkey-sdk-pack
```

Expected:

- Help prints without opening or writing to HID.
- All .NET tests pass.
- Conformance and existing Python tests pass.
- Pack produces `Wobkey.Crush80.Sdk.0.1.0.nupkg` containing `README.md` and `THIRD-PARTY-NOTICES.md`.

Do not run `--smoke` without a connected keyboard and explicit user authorization because it performs real lighting writes.

- [ ] **Step 6: Commit documentation and CI**

```bash
git add sdk/dotnet README.md docs/user/PER-KEY-RGB.md .github/workflows/sdk-dotnet.yml
git commit -m "docs(sdk): add samples CI and package guidance"
```

---

## Final verification and release evidence

- [ ] Run all repository tests:

```bash
python -m unittest discover -s tests -v
node --experimental-vm-modules --test tests/test_signalrgb_v3.mjs
dotnet test sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release
```

- [ ] Build every .NET deliverable reachable on the current OS:

```bash
dotnet build sdk/dotnet/Wobkey.Crush80.Sdk.sln -c Release
dotnet build host/windows/Crush80FirmwareInstaller/Crush80FirmwareInstaller.csproj -c Release
```

On non-Windows systems, report the WPF build limitation if the installer target cannot build; do not report it as passing without observed output.

- [ ] Run the sample help path and inspect the package:

```bash
dotnet run --project sdk/dotnet/samples/Wobkey.Crush80.Sample/Wobkey.Crush80.Sample.csproj -- --help
dotnet pack sdk/dotnet/src/Wobkey.Crush80.Sdk/Wobkey.Crush80.Sdk.csproj -c Release --output /tmp/wobkey-sdk-pack
```

- [ ] With explicit authorization and real hardware, run `--smoke` separately on Windows, Linux, and macOS. Record for each OS: SDK commit, .NET runtime, HidSharp version, device firmware CRC, capability response, Esc/F1 visual confirmation, full 92-color readback result, and restored mode/effect/brightness/RGB result.

- [ ] Claim support only for operating systems whose real-device smoke evidence passed. Keep unverified operating systems documented as implementation targets rather than verified hardware support.
