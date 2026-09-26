using Wobkey.Crush80.Transport;

namespace Wobkey.Crush80.Sdk.Tests.Transport;

public sealed class HidReportFramingTests
{
    [Fact]
    public void AddsAndRemovesReportIdZero()
    {
        var payload = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Span<byte> report = stackalloc byte[33];
        Span<byte> decoded = stackalloc byte[32];
        report.Fill(0xFF);

        HidReportFraming.Encode(payload, report);
        HidReportFraming.Decode(report, decoded);

        Assert.Equal(0, report[0]);
        Assert.Equal(payload, report[1..].ToArray());
        Assert.Equal(payload, decoded.ToArray());
    }

    [Theory]
    [InlineData(31, 33)]
    [InlineData(33, 33)]
    [InlineData(32, 32)]
    [InlineData(32, 34)]
    public void EncodeRejectsIncorrectLengths(int payloadLength, int reportLength)
    {
        Assert.Throws<ArgumentException>(() =>
            HidReportFraming.Encode(new byte[payloadLength], new byte[reportLength]));
    }

    [Fact]
    public void DecodeRejectsNonzeroReportId()
    {
        var report = new byte[33];
        report[0] = 1;
        Assert.Throws<ArgumentException>(() => HidReportFraming.Decode(report, new byte[32]));
    }

    [Theory]
    [InlineData(32, 32)]
    [InlineData(34, 32)]
    [InlineData(33, 31)]
    public void DecodeRejectsIncorrectLengths(int reportLength, int payloadLength)
    {
        Assert.Throws<ArgumentException>(() =>
            HidReportFraming.Decode(new byte[reportLength], new byte[payloadLength]));
    }
}
