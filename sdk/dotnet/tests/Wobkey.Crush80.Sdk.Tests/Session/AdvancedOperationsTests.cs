using Wobkey.Crush80.Sdk.Exceptions;
using Wobkey.Crush80.Sdk.Models;
using Wobkey.Crush80.Sdk.Session;
using Wobkey.Crush80.Sdk.Tests.Support;

namespace Wobkey.Crush80.Sdk.Tests.Session;

public sealed class AdvancedOperationsTests
{
    [Fact]
    public async Task CaptureAndRestorePreserveStateInSafeOrder()
    {
        var saved = Enumerable.Range(0, 92)
            .Select(i => new Rgb24((byte)i, (byte)(i + 1), (byte)(i + 2))).ToArray();
        await using var transport = new FakeFirmwareTransport
        {
            Colors = saved, Enabled = true, Brightness = 4, Effect = 12
        };
        await using var session = await Crush80RgbSession.OpenAsync(transport);

        var state = await session.Advanced.CaptureStateAsync();
        Assert.Equal(saved, state.Colors.ToArray());
        Assert.True(state.Enabled);
        Assert.Equal((byte)4, state.Brightness);
        Assert.Equal((byte)12, state.Effect);
        Assert.Equal(new[] { "ReadRgb", "GetEnabled", "GetBrightness", "GetEffect" },
            transport.Operations.Skip(1).Distinct().ToArray());

        transport.Colors = new Rgb24[92];
        transport.Enabled = false;
        transport.Brightness = 9;
        transport.Effect = 1;
        Assert.Equal(saved, state.Colors.ToArray());
        var restoreOffset = transport.Operations.Count;
        await session.Advanced.RestoreStateAsync(state);

        Assert.Equal(saved, transport.Colors);
        Assert.True(transport.Enabled);
        Assert.Equal((byte)4, transport.Brightness);
        Assert.Equal((byte)12, transport.Effect);
        var restoreOperations = transport.Operations.Skip(restoreOffset).ToArray();
        Assert.Equal("SetEnabled", restoreOperations[0]);
        Assert.All(restoreOperations.Skip(1).Take(12), item => Assert.Equal("WriteRgb", item));
        Assert.Equal(new[] { "SetBrightness", "SetEffect", "SetEnabled" }, restoreOperations.Skip(13));
    }

    [Fact]
    public async Task RestoreContinuesAfterAcknowledgedRejectionAndRemainsUsable()
    {
        await using var transport = new FakeFirmwareTransport { Enabled = true, Brightness = 4, Effect = 12 };
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        var state = await session.Advanced.CaptureStateAsync();
        transport.RejectModeValueOnce = false;
        transport.Brightness = 9;
        transport.Effect = 1;

        var error = await Assert.ThrowsAsync<StateRestoreException>(async () =>
            await session.Advanced.RestoreStateAsync(state));

        var failure = Assert.Single(error.Failures);
        Assert.Equal("OverrideDisable", failure.Field);
        Assert.IsType<FirmwareRejectedRequestException>(failure.Error);
        Assert.Equal((byte)4, transport.Brightness);
        Assert.Equal((byte)12, transport.Effect);
        Assert.True(await session.Advanced.GetEnabledAsync());
    }

    [Fact]
    public async Task InvalidArgumentsDoNotTouchDevice()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        var before = transport.Operations.Count;

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await session.Advanced.WriteFrameAsync(new Rgb24[91]));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await session.Advanced.ReadFrameAsync(new Rgb24[93]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await session.Advanced.WriteRangeAsync(91, new Rgb24[2]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await session.Advanced.WriteRangeAsync(0, ReadOnlyMemory<Rgb24>.Empty));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await session.Advanced.SetBrightnessAsync(10));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await session.Advanced.SetEffectAsync(19));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await session.Advanced.RestoreStateAsync(null!));

        Assert.Equal(before, transport.Operations.Count);
    }

    [Fact]
    public async Task WritesRangesAcrossFirmwareChunksAndReadsWholeFrame()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        var colors = Enumerable.Range(0, 12).Select(i => new Rgb24((byte)(i + 10), 3, 2)).ToArray();
        await session.Advanced.WriteRangeAsync(80, colors);
        var frame = new Rgb24[92];
        await session.Advanced.ReadFrameAsync(frame);

        Assert.Equal(colors, frame.Skip(80));
        Assert.Equal(new[] { 8, 4 }, transport.RgbWriteCounts);
    }

    [Fact]
    public async Task ConcurrentCallsSerializeWholeFrames()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        transport.PauseAfterWrite = true;
        var frame = Enumerable.Repeat(new Rgb24(21, 22, 23), 92).ToArray();
        var write = session.Advanced.WriteFrameAsync(frame).AsTask();
        await transport.FirstWriteObserved.WaitAsync(TimeSpan.FromSeconds(5));
        var effect = session.Advanced.SetEffectAsync(6).AsTask();
        transport.ReleaseWrites();
        await Task.WhenAll(write, effect);

        Assert.Equal(12, transport.RgbWriteCounts.Count);
        Assert.Equal("SetEffect", transport.Operations.Last());
        Assert.Equal(frame, transport.Colors);
    }

    [Fact]
    public async Task ConcurrentMutationCannotInterleaveWithCapture()
    {
        await using var transport = new FakeFirmwareTransport { Effect = 12 };
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        transport.PauseAfterWrite = true;
        var capture = session.Advanced.CaptureStateAsync().AsTask();
        await transport.FirstWriteObserved.WaitAsync(TimeSpan.FromSeconds(5));
        var change = session.Advanced.SetEffectAsync(6).AsTask();
        transport.ReleaseWrites();
        var state = await capture;
        await change;

        Assert.Equal((byte)12, state.Effect);
        Assert.Equal((byte)6, transport.Effect);
        Assert.Equal("SetEffect", transport.Operations.Last());
    }

}
