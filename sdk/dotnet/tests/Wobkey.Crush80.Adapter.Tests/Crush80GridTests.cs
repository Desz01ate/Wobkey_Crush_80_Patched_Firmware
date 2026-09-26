using Wobkey.Crush80.Adapter;
using Wobkey.Crush80.Sdk.Models;
using Xunit;

namespace Wobkey.Crush80.Adapter.Tests;

public sealed class Crush80GridTests
{
    private static readonly Rgb24 Red = new(255, 0, 0);
    private static readonly Rgb24 Green = new(0, 255, 0);
    private static readonly Rgb24 Blue = new(0, 0, 255);

    [Fact]
    public void KeyEditsColorEveryEmitterButNotNeighbors()
    {
        var grid = NewGrid();
        grid.SetKey(Crush80Key.Esc, Red);
        grid.SetKey(Crush80Key.F1, Green);
        grid.SetKey(Crush80Key.CapsLock, Blue);

        Assert.Equal(Red, grid.Frame.Span[0]);
        Assert.Equal(Green, grid.Frame.Span[1]);
        Assert.Equal(default, grid.Frame.Span[2]);
        Assert.Equal(Blue, grid.Frame.Span[51]);
        Assert.Equal(Blue, grid.Frame.Span[52]);
        Assert.Equal(default, grid.Frame.Span[53]);

        grid.SetKey(Crush80Key.Space, Red);
        Assert.Equal(Red, grid.Frame.Span[82]);
        Assert.Equal(Red, grid.Frame.Span[83]);
        Assert.Equal(Red, grid.Frame.Span[84]);
        Assert.Equal(default, grid.Frame.Span[81]);
        Assert.Equal(default, grid.Frame.Span[85]);
    }

    [Fact]
    public void CoordinateAndKeyEditsShareOneLastWriteWinsFrame()
    {
        var grid = NewGrid();
        grid.SetKey(Crush80Key.CapsLock, Red);
        grid.SetAt(1, 7, Green);
        Assert.Equal(Green, grid.Frame.Span[51]);
        Assert.Equal(Green, grid.Frame.Span[52]);
        grid.SetAt(13, 11, Blue);
        grid.SetKey(Crush80Key.Space, Red);
        Assert.Equal(Red, grid.Frame.Span[82]);
        Assert.Equal(Red, grid.Frame.Span[83]);
        Assert.Equal(Red, grid.Frame.Span[84]);
        grid.SetAt(13, 11, Blue);
        Assert.Equal(Red, grid.Frame.Span[82]);
        Assert.Equal(Blue, grid.Frame.Span[83]);
        Assert.Equal(Red, grid.Frame.Span[84]);
        Assert.Equal(default, grid.Frame.Span[81]);
    }

    [Fact]
    public void FillColorsAllSlotsAndLaterEditsOnlyTheirTargets()
    {
        var grid = NewGrid();
        Assert.Equal(37, grid.Width);
        Assert.Equal(12, grid.Height);
        grid.Fill(Red);
        Assert.Equal(92, grid.Frame.Length);
        foreach (var color in grid.Frame.Span)
            Assert.Equal(Red, color);

        grid.SetKey(Crush80Key.Esc, Blue);
        grid.SetAt(3, 0, Green);
        Assert.Equal(Blue, grid.Frame.Span[0]);
        Assert.Equal(Green, grid.Frame.Span[1]);
        Assert.Equal(Red, grid.Frame.Span[2]);
        Assert.Equal(Red, grid.Frame.Span[91]);
    }
    [Fact]
    public void SetAllColorsEverySlotAndLaterEditsOnlyTheirTargets()
    {
        var grid = NewGrid();
        grid.SetAll(Red);

        Assert.Equal(92, grid.Frame.Length);
        foreach (var color in grid.Frame.Span)
            Assert.Equal(Red, color);

        grid.SetKey(Crush80Key.Esc, Blue);
        Assert.Equal(Blue, grid.Frame.Span[0]);
        Assert.Equal(Red, grid.Frame.Span[1]);
        Assert.Equal(Red, grid.Frame.Span[91]);
    }

    [Fact]
    public void EnumerationYieldsUniqueMappedKeysInRowMajorOrder()
    {
        var keys = NewGrid().ToArray();

        Assert.Equal(89, keys.Length);
        Assert.Equal(89, keys.Distinct().Count());
        Assert.Equal(new[] { Crush80Key.Esc, Crush80Key.F1, Crush80Key.F2 }, keys[..3]);
        Assert.Equal(Crush80Key.GraveAccent, keys[17]);
        Assert.Equal(Crush80Key.Tab, keys[34]);
        Assert.Equal(Crush80Key.CapsLock, keys[51]);
        Assert.Equal(Crush80Key.LeftShift, keys[65]);
        Assert.Equal(Crush80Key.LeftCtrl, keys[78]);
        Assert.Equal(Crush80Key.RightArrow, keys[^1]);
    }

    [Fact]
    public void InvalidKeyGapAndCoordinatesRejectWithoutChangingFrame()
    {
        var grid = NewGrid();
        grid.Fill(Red);
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.SetKey((Crush80Key)(-1), Blue));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.SetKey((Crush80Key)int.MaxValue, Blue));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.SetAt(1, 0, Blue));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.SetAt(-1, 0, Blue));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.SetAt(37, 0, Blue));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.SetAt(0, -1, Blue));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.SetAt(0, 12, Blue));
        foreach (var color in grid.Frame.Span)
            Assert.Equal(Red, color);
    }

    [Fact]
    public void MutationGuardRunsWithinSharedLockBeforeEditing()
    {
        var sync = new object();
        var guardCalls = 0;
        var grid = new Crush80Grid(sync, () =>
        {
            Assert.True(System.Threading.Monitor.IsEntered(sync));
            guardCalls++;
            if (guardCalls == 2)
                throw new ObjectDisposedException(nameof(Crush80Grid));
        });

        grid.SetKey(Crush80Key.Esc, Red);
        Assert.Throws<ObjectDisposedException>(() => grid.Fill(Blue));
        Assert.Equal(Red, grid.Frame.Span[0]);
        Assert.Equal(default, grid.Frame.Span[1]);
    }

    [Fact]
    public void FarRightAndPunctuationPointsColorTheirSlotsWithoutNeighbors()
    {
        var grid = NewGrid();
        grid.SetAt(35, 11, Red);
        grid.SetKey(Crush80Key.GraveAccent, Green);

        Assert.Equal(Red, grid.Frame.Span[91]);
        Assert.Equal(default, grid.Frame.Span[90]);
        Assert.Equal(Green, grid.Frame.Span[17]);
        Assert.Equal(default, grid.Frame.Span[18]);

        grid.SetKey(Crush80Key.RightArrow, Blue);
        Assert.Equal(Blue, grid.Frame.Span[91]);
    }

    private static Crush80Grid NewGrid() => new(new object(), () => { });
}
