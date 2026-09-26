using HidSharp.Reports;
using Wobkey.Crush80.Transport;

namespace Wobkey.Crush80.Sdk.Tests.Transport;

public sealed class HidDeviceMatcherTests
{
    [Theory]
    [InlineData(0x320F, 0x5055, 0xFF60, 0x61, true)]
    [InlineData(0x320F, 0x5055, 0xFFEF, 0x61, false)]
    [InlineData(0x320F, 0x5055, 0xFF1C, 0x61, false)]
    [InlineData(0x320F, 0x5055, 0xFF60, 0x62, false)]
    [InlineData(0x320F, 0x5088, 0xFF60, 0x61, false)]
    [InlineData(0x320E, 0x5055, 0xFF60, 0x61, false)]
    public void MatchesOnlyWiredViaInterface(int vid, int pid, int usagePage, int usage, bool expected)
    {
        Assert.Equal(expected, HidDeviceMatcher.IsWiredVia(vid, pid, (ushort)usagePage, (ushort)usage));
    }

    [Theory]
    [InlineData(0xFF600061u, true)]
    [InlineData(0x0061FF60u, false)]
    [InlineData(0xFF600062u, false)]
    public void DecodesTopLevelUsageWithPageInHighWord(uint topLevelUsage, bool expected)
    {
        Assert.Equal(expected, HidDeviceMatcher.IsWiredVia(0x320F, 0x5055, topLevelUsage));
    }

    [Theory]
    [InlineData(0x60, 0x61, true)]
    [InlineData(0xEF, 0x61, false)]
    [InlineData(0x60, 0x62, false)]
    public void ParsedTopLevelCollectionRequiresExactUsage(byte page, byte usage, bool expected)
    {
        // Usage page FFxx, followed by top-level application collection with usage yy.
        var descriptor = new ReportDescriptor([0x06, page, 0xFF, 0x09, usage, 0xA1, 0x01, 0xC0]);

        Assert.Equal(expected, HidDeviceMatcher.IsWiredVia(0x320F, 0x5055, descriptor));
    }
}
