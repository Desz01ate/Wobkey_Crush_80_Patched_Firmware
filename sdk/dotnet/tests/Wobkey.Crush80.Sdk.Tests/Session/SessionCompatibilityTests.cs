using Wobkey.Crush80.Sdk.Tests.Support;

namespace Wobkey.Crush80.Sdk.Tests.Session;

public sealed class SessionCompatibilityTests
{
    [Theory]
    [InlineData(3, 92, 8)]
    [InlineData(1, 91, 8)]
    [InlineData(1, 92, 7)]
    [InlineData(2, 91, 8)]
    [InlineData(2, 92, 7)]
    public async Task RejectsIncompatibleCapabilitiesWithoutMutationAndClosesTransport(
        byte version, byte leds, byte chunk)
    {
        await using var transport = new FakeFirmwareTransport
        {
            ProtocolVersion = version, LedCount = leds, ChunkLimit = chunk
        };

        await Assert.ThrowsAsync<IncompatibleFirmwareException>(async () =>
            await Crush80RgbSession.OpenAsync(transport));

        Assert.Equal(new[] { "GetCapabilities" }, transport.Operations);
        Assert.True(transport.IsDisposed);
    }

    [Theory]
    [InlineData(8, 11)]
    [InlineData(9, 10)]
    public async Task RejectsIncompatibleVersionTwoStreamMetadataWithoutMutation(
        byte streamLeds, byte streamChunks)
    {
        await using var transport = new FakeFirmwareTransport
        {
            ProtocolVersion = 2, StreamLedCount = streamLeds, StreamChunkCount = streamChunks
        };

        await Assert.ThrowsAsync<IncompatibleFirmwareException>(async () =>
            await Crush80RgbSession.OpenAsync(transport));

        Assert.Equal(new[] { "GetCapabilities" }, transport.Operations);
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task OpensOptimizedFirmwareWithReadOnlyHandshakeAndActualCapabilities()
    {
        await using var transport = new FakeFirmwareTransport { ProtocolVersion = 2 };
        await using var session = await Crush80RgbSession.OpenAsync(transport);

        Assert.Equal(new PerKeyRgbCapabilities(2, 92, 8, false)
        {
            StreamLedCount = 9, StreamChunkCount = 11
        }, session.Capabilities);
        Assert.True(session.Capabilities.SupportsFrameStreaming);
        Assert.Equal(new[] { "GetCapabilities" }, transport.Operations);
    }

    [Fact]
    public async Task OptimizedFirmwareUsesOnlySequentialChunksAndRestoresSavedState()
    {
        var saved = Enumerable.Repeat(new Rgb24(13, 24, 35), 92).ToArray();
        await using var transport = new FakeFirmwareTransport
        {
            ProtocolVersion = 2, Colors = saved, Enabled = true, Brightness = 4, Effect = 7
        };
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        var lease = await session.AcquireControlAsync(new Rgb24[92]);
        var frame = new Rgb24[92];
        frame[0] = new Rgb24(255, 1, 2);
        frame[91] = new Rgb24(3, 4, 255);

        await lease.WriteFrameAsync(frame);
        var readback = new Rgb24[92];
        await lease.ReadFrameAsync(readback);
        Assert.Equal(frame, readback);
        await lease.RestoreAsync();

        Assert.Equal(saved, transport.Colors);
        Assert.True(transport.Enabled);
        Assert.Equal((byte)4, transport.Brightness);
        Assert.Equal((byte)7, transport.Effect);
        Assert.Contains("WriteRgb:0:8", transport.DetailedOperations);
        Assert.Contains("WriteRgb:88:4", transport.DetailedOperations);
        Assert.DoesNotContain("Unknown", transport.DetailedOperations);
    }

    [Fact]
    public async Task OpensExactProtocolWithoutCapturingOrChangingLighting()
    {
        await using var transport = new FakeFirmwareTransport { Enabled = true };
        await using var session = await Crush80RgbSession.OpenAsync(transport);

        Assert.Equal(new PerKeyRgbCapabilities(1, 92, 8, true), session.Capabilities);
        Assert.Same(transport.Device, session.Device);
        Assert.Equal(new[] { "GetCapabilities" }, transport.Operations);
        Assert.True(transport.Enabled);
    }

    [Fact]
    public async Task HandshakeTimeoutClosesTransport()
    {
        await using var transport = new FakeFirmwareTransport { TimeoutNextRead = true };

        await Assert.ThrowsAsync<ProtocolViolationException>(async () =>
            await Crush80RgbSession.OpenAsync(transport));

        Assert.True(transport.IsDisposed);
        Assert.Equal(new[] { "GetCapabilities" }, transport.Operations);
    }
}
