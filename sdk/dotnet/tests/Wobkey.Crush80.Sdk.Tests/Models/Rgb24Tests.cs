using Wobkey.Crush80;
using Wobkey.Crush80.Sdk.Models;

namespace Wobkey.Crush80.Sdk.Tests.Models;

public sealed class Rgb24Tests
{
    [Fact]
    public void UsesValueEquality()
    {
        Assert.Equal(new Rgb24(1, 2, 3), new Rgb24(1, 2, 3));
        Assert.NotEqual(new Rgb24(1, 2, 3), new Rgb24(1, 2, 4));
    }
}
