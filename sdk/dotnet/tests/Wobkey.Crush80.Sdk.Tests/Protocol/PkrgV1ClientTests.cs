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
        Assert.Equal(new[] { "WriteRgb", "WriteRgb", "ReadRgb", "ReadRgb" }, transport.Operations);
    }

    [Fact]
    public async Task FirmwareStatusDoesNotDesynchronizeClient()
    {
        await using var transport = new FakeFirmwareTransport();
        transport.RejectNextStatus = 2;
        var client = new PkrgV1Client(transport, TimeSpan.FromSeconds(2));

        var error = await Assert.ThrowsAsync<FirmwareRejectedRequestException>(
            async () => await client.SetEnabledAsync(true, CancellationToken.None));

        Assert.Equal((byte)2, error.Status);
        Assert.False(await client.GetEnabledAsync(CancellationToken.None));
        Assert.Equal(0, transport.PendingReplyCount);
    }

    [Fact]
    public async Task CapabilitiesAndModeReflectFirmwareState()
    {
        await using var transport = new FakeFirmwareTransport { Enabled = true };
        var client = new PkrgV1Client(transport, TimeSpan.FromSeconds(2));

        Assert.Equal(new PerKeyRgbCapabilities(1, 92, 8, true),
            await client.GetCapabilitiesAsync(CancellationToken.None));
        Assert.True(await client.GetEnabledAsync(CancellationToken.None));
        await client.SetEnabledAsync(false, CancellationToken.None);
        Assert.False(await client.GetEnabledAsync(CancellationToken.None));
        Assert.False(transport.Enabled);
    }

    [Fact]
    public async Task OemSettingsRoundTripThroughFirmware()
    {
        await using var transport = new FakeFirmwareTransport();
        var client = new PkrgV1Client(transport, TimeSpan.FromSeconds(2));

        Assert.Equal((byte)9, await client.GetBrightnessAsync(CancellationToken.None));
        await client.SetBrightnessAsync(3, CancellationToken.None);
        Assert.Equal((byte)3, await client.GetBrightnessAsync(CancellationToken.None));
        Assert.Equal((byte)7, await client.GetEffectAsync(CancellationToken.None));
        await client.SetEffectAsync(18, CancellationToken.None);
        Assert.Equal((byte)18, await client.GetEffectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TimeoutIncludesOperationAndOriginalCause()
    {
        await using var transport = new FakeFirmwareTransport { TimeoutNextRead = true };
        var client = new PkrgV1Client(transport, TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<ProtocolViolationException>(
            async () => await client.GetEnabledAsync(CancellationToken.None));

        Assert.Equal("GetEnabled", error.Operation);
        Assert.IsType<TimeoutException>(error.InnerException);
    }

    [Fact]
    public async Task CancellationAfterWriteStillConsumesResponse()
    {
        using var source = new CancellationTokenSource();
        await using var transport = new FakeFirmwareTransport
        {
            CancelCallerAfterNextWrite = true,
            CallerCancellation = source
        };
        var client = new PkrgV1Client(transport, TimeSpan.FromSeconds(2));

        await client.SetEnabledAsync(true, source.Token);

        Assert.True(source.IsCancellationRequested);
        Assert.True(transport.Enabled);
        Assert.Equal(0, transport.PendingReplyCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await client.GetEnabledAsync(source.Token));
        Assert.Single(transport.Operations);
    }

    [Fact]
    public async Task CancellationBetweenChunksStopsBeforeNextExchange()
    {
        using var source = new CancellationTokenSource();
        await using var transport = new FakeFirmwareTransport
        {
            CancelCallerAfterNextWrite = true,
            CallerCancellation = source
        };
        var client = new PkrgV1Client(transport, TimeSpan.FromSeconds(2));
        var colors = Enumerable.Repeat(new Rgb24(4, 5, 6), 12).ToArray();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await client.WriteRangeAsync(0, colors, source.Token));

        Assert.Equal(new[] { 8 }, transport.RgbWriteCounts);
        Assert.Equal(0, transport.PendingReplyCount);
    }

    [Fact]
    public async Task RejectsMalformedOperationAndRecoversOnNextExchange()
    {
        await using var transport = new FakeFirmwareTransport { MalformOperationOnce = "GetEffect" };
        var client = new PkrgV1Client(transport, TimeSpan.FromSeconds(2));

        var error = await Assert.ThrowsAsync<ProtocolViolationException>(
            async () => await client.GetEffectAsync(CancellationToken.None));

        Assert.Equal("GetEffect", error.Operation);
        Assert.Equal((byte)7, await client.GetEffectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FirmwareRangeAndModeRejectionsPreserveState()
    {
        await using var transport = new FakeFirmwareTransport
        {
            RejectRgbStartOnce = 0,
            RejectModeValueOnce = true
        };
        var client = new PkrgV1Client(transport, TimeSpan.FromSeconds(2));

        var modeError = await Assert.ThrowsAsync<FirmwareRejectedRequestException>(
            async () => await client.SetEnabledAsync(true, CancellationToken.None));
        var rgbError = await Assert.ThrowsAsync<FirmwareRejectedRequestException>(
            async () => await client.WriteRangeAsync(0, new[] { new Rgb24(1, 2, 3) }, CancellationToken.None));

        Assert.Equal((byte)3, modeError.Status);
        Assert.Equal((byte)2, rgbError.Status);
        Assert.False(transport.Enabled);
        Assert.Equal(default, transport.Colors[0]);
    }

    [Fact]
    public async Task InvalidModePacketCannotMutateFirmware()
    {
        await using var transport = new FakeFirmwareTransport();
        var request = new byte[32];
        var reply = new byte[32];
        request[0] = 7;
        request[1] = 0x7F;
        request[2] = 1;
        request[4] = 2;

        await transport.WriteAsync(request);
        await transport.ReadAsync(reply, TimeSpan.FromSeconds(1));

        Assert.Equal((byte)3, reply[3]);
        Assert.False(transport.Enabled);
    }

    [Fact]
    public async Task PausedExchangeStillDrainsReplyAfterCallerCancellation()
    {
        using var source = new CancellationTokenSource();
        await using var transport = new FakeFirmwareTransport { PauseAfterWrite = true };
        var client = new PkrgV1Client(transport, TimeSpan.FromSeconds(2));

        var exchange = client.SetEnabledAsync(true, source.Token).AsTask();
        await transport.FirstWriteObserved.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, transport.PendingReplyCount);
        source.Cancel();
        transport.ReleaseWrites();
        await exchange;

        Assert.True(transport.Enabled);
        Assert.Equal(0, transport.PendingReplyCount);
    }

    [Fact]
    public async Task FakeRejectsNoncanonicalPayloadsAndClonesInitialColors()
    {
        await using var transport = new FakeFirmwareTransport();
        var initialColors = new Rgb24[92];
        initialColors[0] = new Rgb24(2, 4, 6);
        transport.Colors = initialColors;
        initialColors[0] = default;

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await transport.WriteAsync(new byte[31]));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await transport.ReadAsync(new byte[33], TimeSpan.FromSeconds(1)));

        var client = new PkrgV1Client(transport, TimeSpan.FromSeconds(1));
        var observed = new Rgb24[1];
        await client.ReadRangeAsync(0, observed, CancellationToken.None);
        Assert.Equal(new Rgb24(2, 4, 6), observed[0]);
    }

    [Fact]
    public async Task StatusOverrideWaitsForStructurallyValidRequest()
    {
        await using var transport = new FakeFirmwareTransport { RejectNextStatus = 2 };
        var invalid = new byte[32];
        invalid[0] = 7;
        invalid[1] = 0x7F;
        var reply = new byte[32];

        await transport.WriteAsync(invalid);
        await transport.ReadAsync(reply, TimeSpan.FromSeconds(1));
        Assert.Equal((byte)1, reply[3]);

        var client = new PkrgV1Client(transport, TimeSpan.FromSeconds(1));
        var rejection = await Assert.ThrowsAsync<FirmwareRejectedRequestException>(
            async () => await client.GetEnabledAsync(CancellationToken.None));
        Assert.Equal((byte)2, rejection.Status);
        Assert.False(await client.GetEnabledAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InvalidOemPacketsDoNotChangeFirmwareSettings()
    {
        await using var transport = new FakeFirmwareTransport();
        var request = new byte[32];
        var reply = new byte[32];
        request[0] = 7;
        request[1] = 3;
        request[2] = 1;
        request[3] = 10;
        await transport.WriteAsync(request);
        await transport.ReadAsync(reply, TimeSpan.FromSeconds(1));

        Assert.Equal((byte)9, transport.Brightness);
        request[2] = 2;
        request[3] = 19;
        await transport.WriteAsync(request);
        await transport.ReadAsync(reply, TimeSpan.FromSeconds(1));

        Assert.Equal((byte)7, transport.Effect);
    }

}
