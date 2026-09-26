using Wobkey.Crush80;
using Wobkey.Crush80.Sdk.Models;

namespace Wobkey.Crush80.Sdk.Tests.Models;

public sealed class OptionsTests
{
    [Fact]
    public void RgbControlOptionsDefaultsToBrightnessNineAndRestoresState()
    {
        var options = new RgbControlOptions();

        Assert.Equal((byte)9, options.HardwareBrightness);
        Assert.True(options.RestoreStateOnDispose);
    }

    [Fact]
    public void RgbControlOptionsAcceptsMaximumHardwareBrightness()
    {
        var options = new RgbControlOptions { HardwareBrightness = 9 };

        Assert.Equal((byte)9, options.HardwareBrightness);
    }

    [Fact]
    public void RgbControlOptionsRejectsHardwareBrightnessAboveNine()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RgbControlOptions { HardwareBrightness = 10 });
    }

    [Fact]
    public void Crush80SessionOptionsDefaultsToTwoSecondTimeout()
    {
        var options = new Crush80SessionOptions();

        Assert.Equal(TimeSpan.FromSeconds(2), options.ResponseTimeout);
    }

    [Fact]
    public void Crush80SessionOptionsAcceptsPositiveTimeout()
    {
        var options = new Crush80SessionOptions { ResponseTimeout = TimeSpan.FromMilliseconds(1) };

        Assert.Equal(TimeSpan.FromMilliseconds(1), options.ResponseTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Crush80SessionOptionsRejectsNonpositiveTimeout(long ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Crush80SessionOptions { ResponseTimeout = TimeSpan.FromTicks(ticks) });
    }
}
