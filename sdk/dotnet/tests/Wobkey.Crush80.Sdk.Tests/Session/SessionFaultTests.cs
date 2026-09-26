using Wobkey.Crush80.Sdk.Tests.Support;

namespace Wobkey.Crush80.Sdk.Tests.Session;

public sealed class SessionFaultTests
{
    [Fact]
    public async Task TimeoutFaultsSessionAndRetainsOriginalCause()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        transport.TimeoutNextRead = true;

        var original = await Assert.ThrowsAsync<ProtocolViolationException>(async () =>
            await session.Advanced.GetEnabledAsync());
        var fault = await Assert.ThrowsAsync<SessionFaultedException>(async () =>
            await session.Advanced.GetBrightnessAsync());

        Assert.Same(original, fault.InnerException);
        Assert.Equal(new[] { "GetCapabilities", "GetEnabled" }, transport.Operations);
    }

    [Fact]
    public async Task FirmwareRejectionLeavesSessionUsable()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        transport.RejectNextStatus = 3;

        var error = await Assert.ThrowsAsync<FirmwareRejectedRequestException>(async () =>
            await session.Advanced.SetEnabledAsync(true));

        Assert.Equal((byte)3, error.Status);
        Assert.False(await session.Advanced.GetEnabledAsync());
    }

    [Fact]
    public async Task DisposingWaitsForInFlightRequestAndClosesTransport()
    {
        await using var transport = new FakeFirmwareTransport();
        var session = await Crush80RgbSession.OpenAsync(transport);
        transport.PauseAfterWrite = true;
        var operation = session.Advanced.GetEnabledAsync().AsTask();
        await transport.FirstWriteObserved.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = session.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        transport.ReleaseWrites();
        await operation;
        await disposal;

        Assert.True(transport.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.Advanced.GetEnabledAsync());
    }

    [Fact]
    public async Task ConcurrentDisposeCallsBothAwaitTransportClosure()
    {
        await using var transport = new FakeFirmwareTransport();
        var session = await Crush80RgbSession.OpenAsync(transport);
        transport.PauseAfterWrite = true;
        var operation = session.Advanced.GetEnabledAsync().AsTask();
        await transport.FirstWriteObserved.WaitAsync(TimeSpan.FromSeconds(5));
        var first = session.DisposeAsync().AsTask();
        var second = session.DisposeAsync().AsTask();

        Assert.False(second.IsCompleted);
        transport.ReleaseWrites();
        await Task.WhenAll(operation, first, second);
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task TransportFailureFaultsSubsequentOperations()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        await transport.DisposeAsync();

        var original = await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.Advanced.GetEnabledAsync());
        var fault = await Assert.ThrowsAsync<SessionFaultedException>(async () =>
            await session.Advanced.GetEffectAsync());

        Assert.Same(original, fault.InnerException);
    }

    [Fact]
    public async Task CancellationBeforeExchangeDoesNotFaultSession()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.Advanced.GetEnabledAsync(cancellation.Token));

        Assert.Equal((byte)9, await session.Advanced.GetBrightnessAsync());
        Assert.Equal(new[] { "GetCapabilities", "GetBrightness" }, transport.Operations);
    }

}
