using Wobkey.Crush80.Sdk.Tests.Support;

namespace Wobkey.Crush80.Sdk.Tests.Session;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task CompleteFrameOperationsDoNotInterleave()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        await using var lease = await session.AcquireControlAsync(new Rgb24[92]);
        transport.Operations.Clear();
        transport.RgbWriteFirstColors.Clear();
        transport.PauseAfterWrite = true;

        var first = Enumerable.Repeat(new Rgb24(1, 0, 0), 92).ToArray();
        var second = Enumerable.Repeat(new Rgb24(0, 1, 0), 92).ToArray();
        var firstTask = lease.WriteFrameAsync(first).AsTask();
        await transport.FirstWriteObserved;
        var secondTask = lease.WriteFrameAsync(second).AsTask();

        transport.ReleaseWrites();
        await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(24, transport.RgbWriteFirstColors.Count);
        Assert.All(transport.RgbWriteFirstColors.Take(12), color => Assert.Equal(new Rgb24(1, 0, 0), color));
        Assert.All(transport.RgbWriteFirstColors.Skip(12), color => Assert.Equal(new Rgb24(0, 1, 0), color));
        Assert.Equal(second, transport.Colors);
    }

    [Fact]
    public async Task InvalidArgumentsAreRejectedWhileAnotherOperationOwnsGate()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        await using var lease = await session.AcquireControlAsync(new Rgb24[92]);
        transport.PauseAfterWrite = true;
        var running = lease.FillAsync(new Rgb24(1, 2, 3)).AsTask();
        await transport.FirstWriteObserved;

        Assert.Throws<ArgumentException>(() => lease.WriteFrameAsync(new Rgb24[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.WriteRangeAsync(90, new Rgb24[3]));
        Assert.Throws<ArgumentException>(() => lease.ReadFrameAsync(new Rgb24[91]));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.SetHardwareBrightnessAsync(10));

        transport.ReleaseWrites();
        await running;
    }

    [Fact]
    public async Task ReadAndBrightnessWaitForWholeFillOperation()
    {
        await using var transport = new FakeFirmwareTransport();
        await using var session = await Crush80RgbSession.OpenAsync(transport);
        await using var lease = await session.AcquireControlAsync(new Rgb24[92]);
        transport.DetailedOperations.Clear();
        transport.PauseAfterWrite = true;
        var color = new Rgb24(15, 28, 63);

        var fill = lease.FillAsync(color).AsTask();
        await transport.FirstWriteObserved;
        var destination = new Rgb24[92];
        var read = lease.ReadFrameAsync(destination).AsTask();
        var brightness = lease.SetHardwareBrightnessAsync(3).AsTask();
        transport.ReleaseWrites();
        await Task.WhenAll(fill, read, brightness);

        Assert.Equal(12, transport.DetailedOperations.TakeWhile(operation => operation.StartsWith("WriteRgb:")).Count());
        Assert.Equal(12, transport.DetailedOperations.Skip(12).TakeWhile(operation => operation.StartsWith("ReadRgb:")).Count());
        Assert.Equal("SetBrightness:3", transport.DetailedOperations[24]);
        Assert.All(destination, actual => Assert.Equal(color, actual));
    }
}
