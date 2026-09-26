using Wobkey.Crush80;
using Wobkey.Crush80.Transport;

if (args is [] or ["--help"])
{
    Console.WriteLine("Usage: Wobkey.Crush80.Sample --help | --list | --smoke");
    Console.WriteLine("--list discovers wired VIA interfaces without lighting writes.");
    Console.WriteLine("--smoke temporarily changes lighting; requires per-key firmware and explicit confirmation.");
    return 0;
}

if (args is ["--list"])
{
    try
    {
        foreach (var device in Crush80DeviceLocator.Enumerate())
            Console.WriteLine($"{device.ProductName ?? "Crush 80"}: {device.Path}");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Could not list wired VIA devices: {error}");
        return 1;
    }
}

if (args is not ["--smoke"])
{
    Console.Error.WriteLine("Unknown command. Use --help.");
    return 2;
}

Console.WriteLine("WARNING: --smoke performs temporary lighting writes on the first wired Crush 80.");
Console.WriteLine("It requires compatible per-key firmware. Close SignalRGB, VIA, and other keyboard controllers first.");
Console.Write("Type SMOKE to authorize lighting writes: ");
if (!string.Equals(Console.ReadLine(), "SMOKE", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Not authorized. No device was opened or written.");
    return 2;
}

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;
try
{
    var frame = new Rgb24[92];
    frame[0] = new Rgb24(255, 0, 0); // Esc
    frame[1] = new Rgb24(0, 255, 0); // F1

    // Open only after authorization; opening reads capabilities but does not change lighting.
    var devices = Crush80DeviceLocator.Enumerate();
    if (devices.Count == 0)
        throw new DeviceNotFoundException("Smoke");

    await using (var session = await Crush80RgbSession.OpenAsync(devices[0], cancellationToken: cancellation.Token))
    {
        var before = await session.Advanced.CaptureStateAsync(cancellation.Token);
        Console.WriteLine($"PKRG v{session.Capabilities.ProtocolVersion}: {session.Capabilities.LedCount} LEDs, {session.Capabilities.ChunkLimit} per chunk; override initially {session.Capabilities.Enabled}.");
        await using (var lease = await session.AcquireControlAsync(new Rgb24[92], cancellationToken: cancellation.Token))
        {
            await lease.WriteFrameAsync(frame, cancellation.Token);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);

            var actual = new Rgb24[92];
            await lease.ReadFrameAsync(actual, cancellation.Token);
            if (!actual.AsSpan().SequenceEqual(frame))
                throw new InvalidDataException("Full 92-color readback did not match the Esc/F1 pattern.");

            // Restore explicitly, not just by disposing the lease on the success path.
            await lease.RestoreAsync();
        }
        // Check the original state after the lease releases the session's exclusive control.
        var restored = await session.Advanced.CaptureStateAsync(cancellation.Token);
        if (restored.Enabled != before.Enabled || restored.Brightness != before.Brightness ||
            restored.Effect != before.Effect || !restored.Colors.Span.SequenceEqual(before.Colors.Span))
            throw new InvalidDataException("Mode, effect, brightness, or full 92-color snapshot differed after restoration.");
    }

    Console.WriteLine("Full 92-color pattern readback and restored mode/effect/brightness/RGB verified.");
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Smoke canceled; automatic lease/session cleanup attempted restoration.");
    return 130;
}
catch (StateRestoreException error)
{
    Console.Error.WriteLine($"Restoration incomplete: {error.Message}");
    foreach (var failure in error.Failures)
        Console.Error.WriteLine($"  {failure.Field}: {failure.Error.Message}");
    Console.Error.WriteLine("Do not assume original lighting state was restored; reconnect/check the keyboard.");
    return 1;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Smoke failed: {error}");
    Console.Error.WriteLine("If the device disconnected, restoration cannot be guaranteed; reconnect/check the keyboard.");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
