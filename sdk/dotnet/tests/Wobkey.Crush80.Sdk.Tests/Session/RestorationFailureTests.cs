using Wobkey.Crush80.Sdk.Tests.Support;

namespace Wobkey.Crush80.Sdk.Tests.Session;

public sealed class RestorationFailureTests
{
    [Fact]
    public async Task RejectedDisableAndColorsDoNotEnablePartialSavedFrame()
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
        Assert.DoesNotContain("SetEnabled:true", transport.DetailedOperations);
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
    public async Task FaultedSessionAllowsOptedOutLeaseToReleaseOwnershipWithoutWrites()
    {
        await using var transport = new FakeFirmwareTransport();
        var session = await Crush80RgbSession.OpenAsync(transport);
        var lease = await session.AcquireControlAsync(new Rgb24[92],
            new RgbControlOptions { RestoreStateOnDispose = false });
        transport.TimeoutNextRead = true;
        await Assert.ThrowsAsync<ProtocolViolationException>(async () => await session.Advanced.GetEnabledAsync());
        transport.Operations.Clear();

        await lease.DisposeAsync();

        Assert.Empty(transport.Operations);
        Assert.Throws<ObjectDisposedException>(() => _ = lease.Enabled);
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

    [Fact]
    public async Task DisposalErrorAlonePropagatesAndRepeatedCallsShareIt()
    {
        var cleanup = new IOException("transport cleanup failed");
        var transport = new FakeFirmwareTransport { DisposalError = cleanup };
        var session = await Crush80RgbSession.OpenAsync(transport);
        var first = session.DisposeAsync().AsTask();
        var second = session.DisposeAsync().AsTask();

        Assert.Same(cleanup, await Assert.ThrowsAsync<IOException>(async () => await first));
        Assert.Same(first, second);
        Assert.Same(cleanup, await Assert.ThrowsAsync<IOException>(async () => await second));
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task RestorationAndTransportFailuresRetainBothCausesAndReadOnlyFailures()
    {
        var cleanup = new IOException("transport cleanup failed");
        var transport = new FakeFirmwareTransport
        {
            Enabled = true, DisposalError = cleanup
        };
        var session = await Crush80RgbSession.OpenAsync(transport);
        await session.AcquireControlAsync(new Rgb24[92]);
        transport.RejectModeValueOnce = false;
        var first = session.DisposeAsync().AsTask();
        var second = session.DisposeAsync().AsTask();

        var error = await Assert.ThrowsAsync<StateRestoreException>(async () => await first);
        Assert.Equal("OverrideDisable", Assert.Single(error.Failures).Field);
        Assert.Null(error.InnerException);
        Assert.Same(cleanup, error.CleanupError);
        Assert.False(error.Failures is StateRestoreFailure[]);
        Assert.Same(first, second);
        Assert.Same(error, await Assert.ThrowsAsync<StateRestoreException>(async () => await second));
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task RestorationFailureWithoutTransportFailureHasNoCleanupError()
    {
        var transport = new FakeFirmwareTransport { Enabled = true };
        var session = await Crush80RgbSession.OpenAsync(transport);
        await session.AcquireControlAsync(new Rgb24[92]);
        transport.RejectModeValueOnce = false;

        var error = await Assert.ThrowsAsync<StateRestoreException>(async () => await session.DisposeAsync());

        Assert.Equal("OverrideDisable", Assert.Single(error.Failures).Field);
        Assert.Null(error.CleanupError);
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public void RestorationExceptionDefensivelyCopiesFailuresAndRetainsOriginalCause()
    {
        var original = new IOException("initial operation failed");
        var failure = new StateRestoreFailure("Colors", new IOException("restore failed"));
        var cleanup = new IOException("cleanup failed");
        var source = new[] { failure };

        var error = new StateRestoreException(source, original, cleanup);
        source[0] = new StateRestoreFailure("Effect", original);

        Assert.Same(original, error.InnerException);
        Assert.Same(failure, Assert.Single(error.Failures));
        Assert.False(error.Failures is StateRestoreFailure[]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<StateRestoreFailure>)error.Failures)[0] = new StateRestoreFailure("Effect", original));
        Assert.Same(cleanup, error.CleanupError);
    }
}
