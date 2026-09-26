using Wobkey.Crush80.Sdk.Exceptions;
using Wobkey.Crush80.Sdk.Models;
using Wobkey.Crush80.Sdk.Session;
using Wobkey.Crush80.Sdk.Tests.Support;

namespace Wobkey.Crush80.Sdk.Tests.Session;

public sealed class FrameWriteTests
{
    [Fact]
    public async Task WritesOnlyChangedChunksIncludingFinalFourLedChunk()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        await using var lease = await session.AcquireControlAsync(new Rgb24[92]);
        transport.DetailedOperations.Clear();

        var frame = new Rgb24[92];
        frame[0] = new Rgb24(255, 0, 0);
        frame[91] = new Rgb24(0, 0, 255);
        await lease.WriteFrameAsync(frame);

        Assert.Equal(new[] { "WriteRgb:0:8", "WriteRgb:88:4" }, transport.DetailedOperations);
        Assert.Equal(frame, transport.Colors);
        transport.DetailedOperations.Clear();
        await lease.WriteFrameAsync(frame);
        Assert.Empty(transport.DetailedOperations);
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
        transport.RejectRgbWriteStartOnce = 8;

        await Assert.ThrowsAsync<FirmwareRejectedRequestException>(async () =>
            await lease.WriteFrameAsync(frame));

        transport.DetailedOperations.Clear();
        await lease.WriteFrameAsync(frame);
        Assert.Equal(new[] { "WriteRgb:8:8" }, transport.DetailedOperations);
        Assert.Equal(frame, transport.Colors);
    }

    [Fact]
    public async Task RangeWritesOnlyChangedSlotsAndRetryUnacknowledgedChunks()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        await using var lease = await session.AcquireControlAsync(new Rgb24[92]);
        var colors = Enumerable.Repeat(new Rgb24(7, 8, 9), 11).ToArray();
        transport.DetailedOperations.Clear();
        transport.RejectRgbWriteStartOnce = 86;

        await Assert.ThrowsAsync<FirmwareRejectedRequestException>(async () =>
            await lease.WriteRangeAsync(78, colors));
        transport.DetailedOperations.Clear();
        await lease.WriteRangeAsync(78, colors);
        Assert.Equal(new[] { "WriteRgb:86:3" }, transport.DetailedOperations);
        Assert.Equal(colors, transport.Colors.AsSpan(78, 11).ToArray());

        transport.DetailedOperations.Clear();
        await lease.WriteFrameAsync(transport.Colors);
        Assert.Empty(transport.DetailedOperations);
    }

    [Fact]
    public async Task FillReusesCachedFrameAndDoesNotMaskNextRangeChange()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        await using var lease = await session.AcquireControlAsync(new Rgb24[92]);
        var color = new Rgb24(42, 37, 11);
        transport.DetailedOperations.Clear();

        await lease.FillAsync(color);
        Assert.Equal(12, transport.DetailedOperations.Count);
        Assert.All(transport.Colors, actual => Assert.Equal(color, actual));
        transport.DetailedOperations.Clear();
        await lease.FillAsync(color);
        Assert.Empty(transport.DetailedOperations);

        await lease.WriteRangeAsync(91, new[] { new Rgb24(5, 6, 7) });
        Assert.Equal(new[] { "WriteRgb:91:1" }, transport.DetailedOperations);
        transport.DetailedOperations.Clear();
        await lease.FillAsync(color);
        Assert.Equal(new[] { "WriteRgb:88:4" }, transport.DetailedOperations);
        Assert.Equal(color, transport.Colors[91]);
    }

    [Fact]
    public async Task ReadFrameReturnsRawDeviceColorsRegardlessOfHardwareBrightness()
    {
        var physical = Enumerable.Repeat(new Rgb24(41, 83, 169), 92).ToArray();
        await using var transport = new FakeFirmwareTransport { Colors = physical };
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        await using var lease = await session.AcquireControlAsync(new Rgb24[92]);
        await lease.SetHardwareBrightnessAsync(1);
        var destination = new Rgb24[92];

        await lease.ReadFrameAsync(destination);

        Assert.Equal(new Rgb24[92], destination);
        Assert.Equal((byte)1, transport.Brightness);
        await lease.WriteRangeAsync(91, new[] { new Rgb24(41, 83, 169) });
        await lease.ReadFrameAsync(destination);
        Assert.Equal(new Rgb24(41, 83, 169), destination[91]);
    }

    [Fact]
    public async Task InvalidFrameRangeReadAndBrightnessDoNotReachTransport()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        await using var lease = await session.AcquireControlAsync(new Rgb24[92]);
        transport.Operations.Clear();

        Assert.Throws<ArgumentException>(() => lease.WriteFrameAsync(new Rgb24[91]));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.WriteRangeAsync(90, new Rgb24[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.WriteRangeAsync(92, new Rgb24[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.WriteRangeAsync(0, ReadOnlyMemory<Rgb24>.Empty));
        Assert.Throws<ArgumentException>(() => lease.ReadFrameAsync(new Rgb24[93]));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.SetHardwareBrightnessAsync(10));
        Assert.Empty(transport.Operations);
    }

    [Fact]
    public async Task DivergentReadDoesNotReplaceAcknowledgedWriteCache()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        await using var lease = await session.AcquireControlAsync(new Rgb24[92]);
        var acknowledged = new Rgb24[92];
        acknowledged[0] = new Rgb24(10, 20, 30);
        await lease.WriteFrameAsync(acknowledged);
        transport.Colors = new Rgb24[92];
        var observed = new Rgb24[92];
        await lease.ReadFrameAsync(observed);
        Assert.Equal(new Rgb24[92], observed);
        transport.DetailedOperations.Clear();

        await lease.WriteFrameAsync(acknowledged);

        Assert.Empty(transport.DetailedOperations);
    }
}
