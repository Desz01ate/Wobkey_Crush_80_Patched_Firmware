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
var failures = new Wobkey.Crush80.Sample.SmokeFailureReport();
Crush80RgbSession? session = null;
RgbControlLease? lease = null;
var controlAcquisitionStarted = false;
var restorationVerified = false;
var stage = "Discovery";
try
{
    var frame = new Rgb24[92];
    frame[0] = new Rgb24(255, 0, 0); // Esc
    frame[1] = new Rgb24(0, 255, 0); // F1

    // Open only after authorization; opening reads capabilities but does not change lighting.
    var devices = Crush80DeviceLocator.Enumerate();
    if (devices.Count == 0)
        throw new DeviceNotFoundException("Smoke");

    stage = "Open";
    session = await Crush80RgbSession.OpenAsync(devices[0], cancellationToken: cancellation.Token);
    stage = "Capture original state";
    var before = await session.Advanced.CaptureStateAsync(cancellation.Token);
    Console.WriteLine($"PKRG v{session.Capabilities.ProtocolVersion}: {session.Capabilities.LedCount} LEDs, {session.Capabilities.ChunkLimit} per chunk; override initially {session.Capabilities.Enabled}.");

    // Acquisition can perform writes before returning a lease; mark it before calling.
    stage = "Acquire control";
    controlAcquisitionStarted = true;
    lease = await session.AcquireControlAsync(new Rgb24[92], cancellationToken: cancellation.Token);
    stage = "Pattern write/readback";
    await lease.WriteFrameAsync(frame, cancellation.Token);
    await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);

    var actual = new Rgb24[92];
    await lease.ReadFrameAsync(actual, cancellation.Token);
    if (!actual.AsSpan().SequenceEqual(frame))
        throw new InvalidDataException("Full 92-color readback did not match the Esc/F1 pattern.");

    stage = "Explicit restore";
    await lease.RestoreAsync();
    // Check the original state after the lease releases the session's exclusive control.
    stage = "Verify restoration";
    var restored = await session.Advanced.CaptureStateAsync(cancellation.Token);
    if (restored.Enabled != before.Enabled || restored.Brightness != before.Brightness ||
        restored.Effect != before.Effect || !restored.Colors.Span.SequenceEqual(before.Colors.Span))
        throw new InvalidDataException("Mode, effect, brightness, or full 92-color snapshot differed after restoration.");
    restorationVerified = true;
}
catch (Exception error)
{
    failures.Capture(stage, error);
}
finally
{
    if (lease is not null)
        await failures.AttemptAsync("Lease cleanup", () => lease.DisposeAsync());
    if (session is not null)
        await failures.AttemptAsync("Session cleanup", () => session.DisposeAsync());
    Console.CancelKeyPress -= cancelHandler;
}

if (failures.Count != 0 || !restorationVerified)
{
    if (failures.Count == 0)
        failures.Capture("Verification", new InvalidOperationException("Restoration verification did not complete."));
    failures.WriteTo(Console.Error, controlAcquisitionStarted);
    return failures.WasCanceledOnly && cancellation.IsCancellationRequested ? 130 : 1;
}

Console.WriteLine("Full 92-color pattern readback and restored mode/effect/brightness/RGB verified.");
return 0;
