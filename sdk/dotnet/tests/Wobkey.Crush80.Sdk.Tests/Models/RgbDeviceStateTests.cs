using Wobkey.Crush80;

namespace Wobkey.Crush80.Sdk.Tests.Models;

public sealed class RgbDeviceStateTests
{
    [Fact]
    public void OwnsCapturedColors()
    {
        var source = Enumerable.Repeat(new Rgb24(1, 2, 3), 92).ToArray();
        var state = new RgbDeviceState(source, enabled: true, brightness: 9, effect: 6);

        source[0] = new Rgb24(9, 9, 9);

        Assert.Equal(92, state.Colors.Length);
        Assert.Equal(new Rgb24(1, 2, 3), state.Colors.Span[0]);
        Assert.True(state.Enabled);
        Assert.Equal((byte)9, state.Brightness);
        Assert.Equal((byte)6, state.Effect);
    }

    [Theory]
    [InlineData(91)]
    [InlineData(93)]
    public void RejectsColorListsWithoutExactlyNinetyTwoEntries(int colorCount)
    {
        var source = new Rgb24[colorCount];

        var exception = Assert.Throws<ArgumentException>(() => new RgbDeviceState(source, enabled: false, brightness: 0, effect: 0));

        Assert.Equal("colors", exception.ParamName);
    }
}
