using Wobkey.Crush80.Sdk.Tests.Support;

namespace Wobkey.Crush80.Sdk.Tests.Session;

public sealed class CancellationTests
{
    [Fact]
    public async Task CancellationAfterWriteStillConsumesMatchingReply()
    {
        await using var transport = new FakeFirmwareTransport { CancelCallerAfterNextWrite = true };
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        using var cancellation = new CancellationTokenSource();
        transport.CallerCancellation = cancellation;

        await session.Advanced.SetEnabledAsync(true, cancellation.Token);

        Assert.True(await session.Advanced.GetEnabledAsync());
        Assert.Equal(0, transport.PendingReplyCount);
    }

    [Fact]
    public async Task CancellationBetweenRgbRequestsLeavesTheReplyStreamAligned()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        using var cancellation = new CancellationTokenSource();
        transport.CallerCancellation = cancellation;
        transport.CancelCallerAfterNextWrite = true;
        var colors = new Rgb24[92];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.Advanced.ReadFrameAsync(colors, cancellation.Token));

        Assert.Equal(0, transport.PendingReplyCount);
        Assert.Equal(1, transport.Operations.Count(operation => operation == "ReadRgb"));
        Assert.False(await session.Advanced.GetEnabledAsync());
    }

    [Fact]
    public async Task DisposedSessionRejectsNewCanceledOperation()
    {
        await using var transport = new FakeFirmwareTransport();
        var session = await Crush80RgbSession.OpenAsync(transport);
        await session.DisposeAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.Advanced.GetEnabledAsync(cancellation.Token));
    }
}
