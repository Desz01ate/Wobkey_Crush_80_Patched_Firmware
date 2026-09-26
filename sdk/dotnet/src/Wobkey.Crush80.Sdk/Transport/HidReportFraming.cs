namespace Wobkey.Crush80.Sdk.Transport;

internal static class HidReportFraming
{
    internal const int PayloadLength = 32;
    internal const int ReportLength = PayloadLength + 1;

    internal static void Encode(ReadOnlySpan<byte> payload, Span<byte> report)
    {
        if (payload.Length != PayloadLength)
            throw new ArgumentException("A VIA payload must be exactly 32 bytes.", nameof(payload));
        if (report.Length != ReportLength)
            throw new ArgumentException("A HID report must be exactly 33 bytes.", nameof(report));

        report.Clear();
        payload.CopyTo(report[1..]);
    }

    internal static void Decode(ReadOnlySpan<byte> report, Span<byte> payload)
    {
        if (report.Length != ReportLength)
            throw new ArgumentException("A HID report must be exactly 33 bytes.", nameof(report));
        if (payload.Length != PayloadLength)
            throw new ArgumentException("A VIA payload must be exactly 32 bytes.", nameof(payload));
        if (report[0] != 0)
            throw new ArgumentException("The HID report ID must be zero.", nameof(report));

        report[1..].CopyTo(payload);
    }
}
