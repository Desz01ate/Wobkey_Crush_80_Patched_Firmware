using Wobkey.Crush80.Sdk.Tests.Support;

namespace Wobkey.Crush80.Sdk.Tests.Session;

public sealed class RestorationTests
{
    [Fact]
    public async Task DisposalRestoresSavedStateInSafeOrder()
    {
        var saved = Enumerable.Repeat(new Rgb24(10, 20, 30), 92).ToArray();
        await using var transport = new FakeFirmwareTransport
        {
            Colors = saved, Enabled = true, Brightness = 4, Effect = 7
        };
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        var lease = await session.AcquireControlAsync(new Rgb24[92]);
        transport.DetailedOperations.Clear();

        await lease.DisposeAsync();

        Assert.Equal(new[] { "SetEnabled:false" }
            .Concat(Enumerable.Range(0, 12).Select(i => $"WriteRgb:{i * 8}:{Math.Min(8, 92 - i * 8)}"))
            .Concat(["SetBrightness:4", "SetEffect:7", "SetEnabled:true"]), transport.DetailedOperations);
        Assert.Equal(saved, transport.Colors);
        Assert.True(transport.Enabled);
        Assert.Equal((byte)4, transport.Brightness);
        Assert.Equal((byte)7, transport.Effect);
    }

    [Fact]
    public async Task RestoringDisabledSnapshotLeavesOverrideDisabled()
    {
        await using var transport = new FakeFirmwareTransport { Enabled = false, Brightness = 1, Effect = 13 };
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        var lease = await session.AcquireControlAsync(new Rgb24[92]);

        await lease.RestoreAsync();

        Assert.False(transport.Enabled);
        Assert.Equal((byte)1, transport.Brightness);
        Assert.Equal((byte)13, transport.Effect);
        Assert.Throws<ObjectDisposedException>(() => _ = lease.Enabled);
        Assert.Throws<ObjectDisposedException>(() => _ = lease.Capabilities);
    }

    [Fact]
    public async Task OptedOutDisposalLeavesOverrideInPlaceAndReleasesOwnership()
    {
        await using var transport = new FakeFirmwareTransport { Enabled = false, Brightness = 2, Effect = 10 };
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        var initial = Enumerable.Repeat(new Rgb24(4, 5, 6), 92).ToArray();
        var lease = await session.AcquireControlAsync(initial, new RgbControlOptions { RestoreStateOnDispose = false });
        transport.Operations.Clear();

        await lease.DisposeAsync();

        Assert.Empty(transport.Operations);
        Assert.Equal(initial, transport.Colors);
        Assert.True(transport.Enabled);
        Assert.Equal((byte)9, transport.Brightness);
        Assert.Equal((byte)6, transport.Effect);
        await using var next = await session.AcquireControlAsync(new Rgb24[92]);
    }

    [Fact]
    public async Task RejectedSavedFrameLeavesLeaseReportingDisabledUntilRetry()
    {
        await using var transport = new FakeFirmwareTransport { Enabled = true };
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        var lease = await session.AcquireControlAsync(new Rgb24[92]);
        transport.RejectRgbWriteStartOnce = 8;

        await Assert.ThrowsAsync<StateRestoreException>(async () => await lease.RestoreAsync());

        Assert.False(transport.Enabled);
        Assert.False(lease.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.AcquireControlAsync(new Rgb24[92]));
        await lease.RestoreAsync();
        Assert.True(transport.Enabled);
        Assert.Throws<ObjectDisposedException>(() => _ = lease.Enabled);
    }

    [Fact]
    public async Task RejectedRestoreRetainsOwnershipUntilSuccessfulRetry()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        var lease = await session.AcquireControlAsync(new Rgb24[92]);
        transport.RejectModeValueOnce = false;

        await Assert.ThrowsAsync<StateRestoreException>(async () => await lease.RestoreAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.AcquireControlAsync(new Rgb24[92]));
        await lease.RestoreAsync();
        await using var next = await session.AcquireControlAsync(new Rgb24[92]);
    }

    [Fact]
    public async Task ExplicitRestorationIsIdempotentAndReleasesLease()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        var lease = await session.AcquireControlAsync(new Rgb24[92]);
        await lease.RestoreAsync();
        var count = transport.Operations.Count;

        await lease.DisposeAsync();
        Assert.Equal(count, transport.Operations.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await lease.RestoreAsync());
        await using var next = await session.AcquireControlAsync(new Rgb24[92]);
    }

    [Fact]
    public async Task InitialFrameAndRestoreSnapshotAreOwnedAcrossCallerMutations()
    {
        var saved = Enumerable.Repeat(new Rgb24(2, 4, 6), 92).ToArray();
        var initial = Enumerable.Repeat(new Rgb24(1, 3, 5), 92).ToArray();
        await using var transport = new FakeFirmwareTransport { Colors = saved };
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        var lease = await session.AcquireControlAsync(initial);
        initial[0] = new Rgb24(99, 99, 99);
        saved[0] = new Rgb24(88, 88, 88);

        await lease.RestoreAsync();
        Assert.Equal(new Rgb24(2, 4, 6), transport.Colors[0]);
    }
}
