using Wobkey.Crush80.Sdk.Tests.Support;

namespace Wobkey.Crush80.Sdk.Tests.Session;

public sealed class ControlAcquisitionTests
{
    [Fact]
    public async Task CapturesEverythingBeforeFirstMutation()
    {
        var saved = Enumerable.Repeat(new Rgb24(12, 34, 56), 92).ToArray();
        var initial = Enumerable.Repeat(new Rgb24(65, 43, 21), 92).ToArray();
        await using var transport = new FakeFirmwareTransport
        {
            Colors = saved, Enabled = true, Brightness = 4, Effect = 7
        };
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        transport.DetailedOperations.Clear();

        await using var lease = await session.AcquireControlAsync(initial);

        Assert.Equal(Enumerable.Range(0, 12).Select(i => $"ReadRgb:{i * 8}:{Math.Min(8, 92 - i * 8)}")
            .Concat(["GetEnabled", "GetBrightness", "GetEffect", "SetEnabled:false"])
            .Concat(Enumerable.Range(0, 12).Select(i => $"WriteRgb:{i * 8}:{Math.Min(8, 92 - i * 8)}"))
            .Concat(["SetBrightness:9", "SetEffect:6", "SetEnabled:true"]), transport.DetailedOperations);
        Assert.Equal(initial, transport.Colors);
        Assert.True(lease.Enabled);
        Assert.Equal(session.Capabilities, lease.Capabilities);
    }

    [Fact]
    public async Task SnapshotFailureSendsNoMutation()
    {
        await using var transport = new FakeFirmwareTransport { MalformOperationOnce = "GetEffect" };
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        transport.Operations.Clear();

        await Assert.ThrowsAsync<ProtocolViolationException>(async () =>
            await session.AcquireControlAsync(new Rgb24[92]));

        Assert.DoesNotContain(transport.Operations, operation => operation.StartsWith("Set") || operation.StartsWith("WriteRgb"));
    }

    [Fact]
    public async Task InvalidInitialFrameDoesNotTouchDevice()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        transport.Operations.Clear();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await session.AcquireControlAsync(new Rgb24[91]));
        Assert.Empty(transport.Operations);
    }

    [Fact]
    public async Task ActiveLeaseRejectsAnotherAcquisitionAndAdvancedMutationsButAllowsReads()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        await using var lease = await session.AcquireControlAsync(new Rgb24[92]);
        transport.Operations.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.AcquireControlAsync(new Rgb24[92]));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.Advanced.WriteFrameAsync(new Rgb24[92]));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.Advanced.WriteRangeAsync(0, new Rgb24[1]));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.Advanced.SetEnabledAsync(false));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.Advanced.SetBrightnessAsync(3));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.Advanced.SetEffectAsync(3));
        var state = await session.Advanced.CaptureStateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.Advanced.RestoreStateAsync(state));
        Assert.True(await session.Advanced.GetEnabledAsync());
        Assert.Equal((byte)9, await session.Advanced.GetBrightnessAsync());
        Assert.Equal((byte)6, await session.Advanced.GetEffectAsync());
        await session.Advanced.ReadFrameAsync(new Rgb24[92]);
        Assert.DoesNotContain(transport.Operations, operation => operation.StartsWith("Set") || operation.StartsWith("WriteRgb"));
    }

    [Fact]
    public async Task RejectedInitialFrameRestoresSnapshotBeforeFailureEscapes()
    {
        var saved = Enumerable.Repeat(new Rgb24(11, 22, 33), 92).ToArray();
        await using var transport = new FakeFirmwareTransport
        {
            Colors = saved, Enabled = true, Brightness = 4, Effect = 7,
            RejectRgbWriteStartOnce = 8
        };
        await using var session = await Crush80RgbSession.OpenAsync(transport);

        await Assert.ThrowsAsync<FirmwareRejectedRequestException>(async () =>
            await session.AcquireControlAsync(new Rgb24[92]));

        Assert.Equal(saved, transport.Colors);
        Assert.True(transport.Enabled);
        Assert.Equal((byte)4, transport.Brightness);
        Assert.Equal((byte)7, transport.Effect);
        await using var lease = await session.AcquireControlAsync(new Rgb24[92]);
    }
}
