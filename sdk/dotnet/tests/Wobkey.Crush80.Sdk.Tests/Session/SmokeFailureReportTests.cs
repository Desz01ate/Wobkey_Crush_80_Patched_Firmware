using Wobkey.Crush80.Sample;

namespace Wobkey.Crush80.Sdk.Tests.Session;

public sealed class SmokeFailureReportTests
{
    [Fact]
    public async Task RestoreAndBothCleanupFailuresRemainVisibleWithOriginalCause()
    {
        var report = new SmokeFailureReport();
        var restore = new StateRestoreException(
            [new StateRestoreFailure("Colors", new IOException("color write rejected"))],
            new InvalidDataException("pattern readback failed"));

        await report.AttemptAsync("Explicit restore", () => ValueTask.FromException(restore));
        await report.AttemptAsync("Lease cleanup", () => ValueTask.FromException(new IOException("lease disposal failed")));
        await report.AttemptAsync("Session cleanup", () => ValueTask.FromException(new IOException("transport close failed")));

        using var output = new StringWriter();
        report.WriteTo(output, controlAcquisitionStarted: true);
        var text = output.ToString();
        Assert.Equal(3, report.Count);
        Assert.Contains("Explicit restore", text);
        Assert.Contains("Colors: color write rejected", text);
        Assert.Contains("Original operation: pattern readback failed", text);
        Assert.Contains("Lease cleanup: lease disposal failed", text);
        Assert.Contains("Session cleanup: transport close failed", text);
        Assert.Contains("restoration could not be verified", text);
        Assert.Contains("check keyboard lighting/settings manually", text);
    }

    [Fact]
    public void RestoreReportsNestedFieldAndOperationCauses()
    {
        var report = new SmokeFailureReport();
        var transport = new IOException("HID read timed out", new InvalidDataException("malformed reply"));
        var fault = new SessionFaultedException("RestoreState", innerException: transport);
        var operation = new InvalidOperationException("readback mismatch",
            new IOException("USB disconnected", new InvalidDataException("device path vanished")));
        report.Capture("Explicit restore", new StateRestoreException(
            [new StateRestoreFailure("Colors", fault)], operation));

        using var output = new StringWriter();
        report.WriteTo(output, controlAcquisitionStarted: true);
        var text = output.ToString();

        Assert.Contains("  Colors: The Crush 80 session is faulted and cannot continue.", text);
        Assert.Contains("    Caused by: HID read timed out", text);
        Assert.Contains("      Caused by: malformed reply", text);
        Assert.Contains("  Original operation: readback mismatch", text);
        Assert.Contains("    Caused by: USB disconnected", text);
        Assert.Contains("      Caused by: device path vanished", text);
    }

    [Fact]
    public async Task NonDisconnectFailureAfterAcquisitionWarnsOperator()
    {
        var report = new SmokeFailureReport();
        await report.AttemptAsync("Readback", () => ValueTask.FromException(new InvalidDataException("colors differ")));

        using var output = new StringWriter();
        report.WriteTo(output, controlAcquisitionStarted: true);
        Assert.Contains("Readback: colors differ", output.ToString());
        Assert.Contains("restoration could not be verified", output.ToString());
    }
}
