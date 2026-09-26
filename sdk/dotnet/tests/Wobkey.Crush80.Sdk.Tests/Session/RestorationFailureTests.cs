using Wobkey.Crush80.Sdk.Tests.Support;

namespace Wobkey.Crush80.Sdk.Tests.Session;

public sealed class RestorationFailureTests
{
    [Fact]
    public async Task RejectedDisableAndColorWriteAggregateAndContinueSafeSteps()
    {
        await using var transport = new FakeFirmwareTransport { Enabled = true, Brightness = 4, Effect = 7 };
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        var lease = await session.AcquireControlAsync(new Rgb24[92]);
        transport.RejectModeValueOnce = false;
        transport.RejectRgbStartOnce = 0;
        transport.DetailedOperations.Clear();

        var error = await Assert.ThrowsAsync<StateRestoreException>(async () => await lease.RestoreAsync());

        Assert.Equal(new[] { "OverrideDisable", "Colors" }, error.Failures.Select(failure => failure.Field));
        Assert.All(error.Failures, failure => Assert.IsType<FirmwareRejectedRequestException>(failure.Error));
        Assert.Contains("SetBrightness:4", transport.DetailedOperations);
        Assert.Contains("SetEffect:7", transport.DetailedOperations);
        Assert.Contains("SetEnabled:true", transport.DetailedOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultedRestoreReportsUnattemptedStepsWithoutSendingThem(bool timeout)
    {
        await using var transport = new FakeFirmwareTransport { Enabled = true };
        var session = await Crush80RgbSession.OpenAsync(transport);
        var lease = await session.AcquireControlAsync(new Rgb24[92]);
        transport.DetailedOperations.Clear();
        if (timeout) transport.TimeoutNextRead = true;
        else transport.MalformOperationOnce = "SetEnabled";

        var error = await Assert.ThrowsAsync<StateRestoreException>(async () => await lease.RestoreAsync());

        Assert.Equal(new[] { "OverrideDisable", "Colors", "Brightness", "Effect", "OverrideEnable" },
            error.Failures.Select(failure => failure.Field));
        Assert.IsType<ProtocolViolationException>(error.Failures[0].Error);
        Assert.All(error.Failures.Skip(1), failure =>
            Assert.Same(error.Failures[0].Error, Assert.IsType<SessionFaultedException>(failure.Error).InnerException));
        Assert.Equal(new[] { "SetEnabled:false" }, transport.DetailedOperations);
        await Assert.ThrowsAsync<StateRestoreException>(async () => await session.DisposeAsync());
    }

    [Fact]
    public async Task MalformedColorReplyStopsLaterRestorationCommands()
    {
        await using var transport = new FakeFirmwareTransport
        {
            Colors = Enumerable.Repeat(new Rgb24(1, 2, 3), 92).ToArray(), Enabled = true
        };
        var session = await Crush80RgbSession.OpenAsync(transport);
        var lease = await session.AcquireControlAsync(new Rgb24[92]);
        transport.DetailedOperations.Clear();
        transport.MalformOperationOnce = "WriteRgb";

        var error = await Assert.ThrowsAsync<StateRestoreException>(async () => await lease.RestoreAsync());

        Assert.Equal(new[] { "Colors", "Brightness", "Effect", "OverrideEnable" },
            error.Failures.Select(failure => failure.Field));
        Assert.IsType<ProtocolViolationException>(error.Failures[0].Error);
        Assert.All(error.Failures.Skip(1), failure => Assert.IsType<SessionFaultedException>(failure.Error));
        Assert.Equal(new[] { "SetEnabled:false", "WriteRgb:0:8" }, transport.DetailedOperations);
        await Assert.ThrowsAsync<StateRestoreException>(async () => await session.DisposeAsync());
    }

    [Fact]
    public async Task SessionDisposalRestoresLeaseAndClosesTransportOnFailure()
    {
        var saved = Enumerable.Repeat(new Rgb24(1, 2, 3), 92).ToArray();
        await using var transport = new FakeFirmwareTransport { Colors = saved, Enabled = true };
        var session = await Crush80RgbSession.OpenAsync(transport);
        await session.AcquireControlAsync(new Rgb24[92]);
        transport.RejectRgbStartOnce = 0;
        transport.DetailedOperations.Clear();

        var error = await Assert.ThrowsAsync<StateRestoreException>(async () => await session.DisposeAsync());

        Assert.Equal("Colors", Assert.Single(error.Failures).Field);
        Assert.True(transport.IsDisposed);
        Assert.All(transport.Colors, color => Assert.Equal(default, color));
        var firstCount = transport.DetailedOperations.Count;
        var again = await Assert.ThrowsAsync<StateRestoreException>(async () => await session.DisposeAsync());
        Assert.Same(error, again);
        Assert.Equal(firstCount, transport.DetailedOperations.Count);
    }

    [Fact]
    public async Task SessionDisposalHonorsLeaseOptOut()
    {
        await using var transport = new FakeFirmwareTransport();
        var session = await Crush80RgbSession.OpenAsync(transport);
        await session.AcquireControlAsync(new Rgb24[92], new RgbControlOptions { RestoreStateOnDispose = false });
        transport.Operations.Clear();

        await session.DisposeAsync();

        Assert.Empty(transport.Operations);
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task DisposalOfFaultedSessionReportsUnattemptedRestorationAndClosesTransport()
    {
        await using var transport = new FakeFirmwareTransport();
        var session = await Crush80RgbSession.OpenAsync(transport);
        await session.AcquireControlAsync(new Rgb24[92]);
        transport.TimeoutNextRead = true;
        await Assert.ThrowsAsync<ProtocolViolationException>(async () => await session.Advanced.GetEnabledAsync());
        transport.Operations.Clear();

        var error = await Assert.ThrowsAsync<StateRestoreException>(async () => await session.DisposeAsync());

        Assert.Equal(new[] { "OverrideDisable", "Colors", "Brightness", "Effect", "OverrideEnable" },
            error.Failures.Select(failure => failure.Field));
        Assert.All(error.Failures, failure => Assert.IsType<SessionFaultedException>(failure.Error));
        Assert.Empty(transport.Operations);
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task SessionDisposalWaitsForActiveExchangeThenRestores()
    {
        await using var transport = new FakeFirmwareTransport();
        var session = await Crush80RgbSession.OpenAsync(transport);
        var lease = await session.AcquireControlAsync(new Rgb24[92]);
        transport.PauseAfterWrite = true;
        var operation = lease.SetHardwareBrightnessAsync(3).AsTask();
        await transport.FirstWriteObserved.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = session.DisposeAsync().AsTask();
        var concurrent = session.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await session.Advanced.GetEnabledAsync());
        transport.ReleaseWrites();
        await Task.WhenAll(operation, disposal, concurrent);
        Assert.True(transport.IsDisposed);
        Assert.Equal((byte)9, transport.Brightness);
    }
}
