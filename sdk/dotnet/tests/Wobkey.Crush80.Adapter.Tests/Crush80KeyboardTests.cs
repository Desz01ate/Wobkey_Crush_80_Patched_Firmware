using Wobkey.Crush80.Adapter;
using Wobkey.Crush80.Sdk.Exceptions;
using Wobkey.Crush80.Sdk.Models;
using Wobkey.Crush80.Sdk.Tests.Support;
using Xunit;

namespace Wobkey.Crush80.Adapter.Tests;

public sealed class Crush80KeyboardTests
{
    private static readonly Rgb24 Red = new(255, 0, 0);
    private static readonly Rgb24 Green = new(0, 255, 0);
    private static readonly Rgb24 Blue = new(0, 0, 255);

    [Fact]
    public async Task OpenDisplaysBlackAndDisposeRestoresCapturedLightingAndClosesTransport()
    {
        var transport = SeededTransport();
        var originalColors = (Rgb24[])transport.Colors.Clone();
        var keyboard = await Crush80Keyboard.OpenAsync(transport);
        try
        {
            Assert.All(transport.Colors, color => Assert.Equal(default, color));
            Assert.All(keyboard.Grid.Frame.ToArray(), color => Assert.Equal(default, color));
            Assert.True(transport.Enabled);
            Assert.NotEqual((byte)4, transport.Brightness);
            Assert.NotEqual((byte)2, transport.Effect);
            Assert.False(transport.IsDisposed);
        }
        finally
        {
            await keyboard.DisposeAsync();
        }

        Assert.Equal(originalColors, transport.Colors);
        Assert.True(transport.Enabled);
        Assert.Equal((byte)4, transport.Brightness);
        Assert.Equal((byte)2, transport.Effect);
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task RejectedAcquisitionRestoresStateAndClosesOwnedTransport()
    {
        var transport = SeededTransport();
        var originalColors = (Rgb24[])transport.Colors.Clone();
        transport.RejectModeValueOnce = false;

        var failure = await Assert.ThrowsAsync<FirmwareRejectedRequestException>(
            () => Crush80Keyboard.OpenAsync(transport).AsTask());

        Assert.Equal((byte)3, failure.Status);
        Assert.Equal(originalColors, transport.Colors);
        Assert.True(transport.Enabled);
        Assert.Equal((byte)4, transport.Brightness);
        Assert.Equal((byte)2, transport.Effect);
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task FailedAcquisitionPreservesRejectionAndTransportCleanupFailure()
    {
        var cleanupError = new InvalidOperationException("Transport close failed.");
        var transport = SeededTransport();
        transport.RejectModeValueOnce = false;
        transport.DisposalError = cleanupError;

        var failure = await Record.ExceptionAsync(() => Crush80Keyboard.OpenAsync(transport).AsTask());

        Assert.NotNull(failure);
        Assert.Contains(ErrorTree(failure), error => error is FirmwareRejectedRequestException { Status: 3 });
        Assert.Contains(cleanupError, ErrorTree(failure));
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task EditsDoNotWriteUntilApplyAndRepeatedFrameWritesNoUnchangedRgbChunks()
    {
        var transport = SeededTransport();
        await using var keyboard = await Crush80Keyboard.OpenAsync(transport);
        var writesAfterOpen = transport.RgbWriteCounts.Count;

        keyboard.Grid.SetKey(Crush80Key.Esc, Red);
        keyboard.Grid.SetAt(3, 0, Green); // F1's sparse canvas point.
        Assert.Equal(writesAfterOpen, transport.RgbWriteCounts.Count);
        Assert.Equal(default, transport.Colors[0]);
        Assert.Equal(default, transport.Colors[1]);

        await keyboard.ApplyAsync();
        Assert.Equal(Red, transport.Colors[0]);
        Assert.Equal(Green, transport.Colors[1]);
        Assert.Equal(default, transport.Colors[2]);
        Assert.Equal(default, transport.Colors[91]);
        Assert.Equal(writesAfterOpen + 1, transport.RgbWriteCounts.Count);

        await keyboard.ApplyAsync();
        Assert.Equal(writesAfterOpen + 1, transport.RgbWriteCounts.Count);
    }

    [Fact]
    public async Task CanceledApplyDoesNotDiscardDesiredEdits()
    {
        var transport = SeededTransport();
        await using var keyboard = await Crush80Keyboard.OpenAsync(transport);
        keyboard.Grid.SetKey(Crush80Key.Esc, Red);
        var writesAfterOpen = transport.RgbWriteCounts.Count;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => keyboard.ApplyAsync(cancellation.Token).AsTask());

        Assert.Equal(Red, keyboard.Grid.Frame.Span[0]);
        Assert.Equal(default, transport.Colors[0]);
        Assert.Equal(writesAfterOpen, transport.RgbWriteCounts.Count);
        await keyboard.ApplyAsync();
        Assert.Equal(Red, transport.Colors[0]);
    }

    [Fact]
    public async Task FirmwareRejectionPropagatesAndLeavesDesiredFrameIntact()
    {
        var transport = SeededTransport();
        await using var keyboard = await Crush80Keyboard.OpenAsync(transport);
        keyboard.Grid.SetKey(Crush80Key.Esc, Red);
        transport.RejectRgbWriteStartOnce = 0;

        var failure = await Assert.ThrowsAsync<FirmwareRejectedRequestException>(
            () => keyboard.ApplyAsync().AsTask());

        Assert.Equal((byte)2, failure.Status);
        Assert.Equal(Red, keyboard.Grid.Frame.Span[0]);
        Assert.Equal(default, transport.Colors[0]);
    }

    [Fact]
    public async Task InFlightApplyRejectsAllGridEditsAndOverlappingApply()
    {
        var transport = SeededTransport();
        await using var keyboard = await Crush80Keyboard.OpenAsync(transport);
        keyboard.Grid.SetKey(Crush80Key.Esc, Red);
        transport.PauseAfterWrite = true;
        var apply = keyboard.ApplyAsync().AsTask();
        try
        {
            await transport.FirstWriteObserved.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(apply.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => keyboard.Grid.SetKey(Crush80Key.F1, Green));
            Assert.Throws<InvalidOperationException>(() => keyboard.Grid.SetAt(3, 0, Green));
            Assert.Throws<InvalidOperationException>(() => keyboard.Grid.Fill(Blue));
            await Assert.ThrowsAsync<InvalidOperationException>(() => keyboard.ApplyAsync().AsTask());
            Assert.Equal(Red, keyboard.Grid.Frame.Span[0]);
            Assert.Equal(default, keyboard.Grid.Frame.Span[1]);
        }
        finally
        {
            transport.ReleaseWrites();
            await apply.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(Red, transport.Colors[0]);
        Assert.Equal(default, transport.Colors[1]);
        keyboard.Grid.SetKey(Crush80Key.F1, Green);
        await keyboard.ApplyAsync();
        Assert.Equal(Green, transport.Colors[1]);
    }

    [Fact]
    public async Task DisposalImmediatelyDisallowsEditsAndWaitsForInFlightApplyBeforeRestoring()
    {
        var transport = SeededTransport();
        var originalColors = (Rgb24[])transport.Colors.Clone();
        var keyboard = await Crush80Keyboard.OpenAsync(transport);
        keyboard.Grid.SetKey(Crush80Key.Esc, Red);
        transport.PauseAfterWrite = true;
        var apply = keyboard.ApplyAsync().AsTask();
        Task? disposal = null;
        try
        {
            await transport.FirstWriteObserved.WaitAsync(TimeSpan.FromSeconds(5));
            disposal = keyboard.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.False(transport.IsDisposed);
            Assert.Throws<ObjectDisposedException>(() => keyboard.Grid.SetKey(Crush80Key.F1, Green));
            Assert.Throws<ObjectDisposedException>(() => keyboard.Grid.SetAt(3, 0, Green));
            Assert.Throws<ObjectDisposedException>(() => keyboard.Grid.Fill(Blue));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => keyboard.ApplyAsync().AsTask());
        }
        finally
        {
            transport.ReleaseWrites();
            await apply.WaitAsync(TimeSpan.FromSeconds(5));
            if (disposal is null)
                disposal = keyboard.DisposeAsync().AsTask();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(originalColors, transport.Colors);
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task DisposeReportsBothRestorationRejectionAndTransportCloseFailure()
    {
        var cleanupError = new InvalidOperationException("Transport close failed.");
        var transport = SeededTransport();
        var keyboard = await Crush80Keyboard.OpenAsync(transport);
        transport.RejectModeValueOnce = true; // Reject re-enabling the captured override during restoration.
        transport.DisposalError = cleanupError;

        var failure = await Record.ExceptionAsync(() => keyboard.DisposeAsync().AsTask());

        Assert.NotNull(failure);
        Assert.Contains(ErrorTree(failure), error => error is StateRestoreException restore &&
            restore.Failures.Any(step => step.Field == "OverrideEnable" &&
                step.Error is FirmwareRejectedRequestException { Status: 3 }));
        Assert.Contains(cleanupError, ErrorTree(failure));
        Assert.True(transport.IsDisposed);
    }

    private static FakeFirmwareTransport SeededTransport() => new()
    {
        Colors = Enumerable.Range(0, 92)
            .Select(index => new Rgb24((byte)(index + 1), (byte)(index + 32), (byte)(index + 64)))
            .ToArray(),
        Enabled = true,
        Brightness = 4,
        Effect = 2
    };

    private static IEnumerable<Exception> ErrorTree(Exception error)
    {
        yield return error;
        if (error.InnerException is { } inner)
        {
            foreach (var nested in ErrorTree(inner))
                yield return nested;
        }
        if (error is AggregateException aggregate)
        {
            foreach (var nestedError in aggregate.InnerExceptions)
                foreach (var nested in ErrorTree(nestedError))
                    yield return nested;
        }
        if (error is StateRestoreException restore)
        {
            if (restore.CleanupError is { } cleanup)
            {
                foreach (var nested in ErrorTree(cleanup))
                    yield return nested;
            }
            foreach (var failure in restore.Failures)
                foreach (var nested in ErrorTree(failure.Error))
                    yield return nested;
        }
    }
}
