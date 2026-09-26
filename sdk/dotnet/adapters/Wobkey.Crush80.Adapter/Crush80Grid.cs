using Wobkey.Crush80.Sdk.Models;

namespace Wobkey.Crush80.Adapter;

/// <summary>
/// A mutable, in-memory sparse canvas for the wired Crush 80's provisional LED map.
/// Coordinates are canvas points, not VIA matrix addresses. Only Esc, F1, and Caps Lock
/// have physically confirmed key assignments; other key names await verification.
/// </summary>
public sealed class Crush80Grid
{
    private readonly object _sync;
    private readonly Action _ensureMutable;
    private readonly Rgb24[] _frame = new Rgb24[Crush80Layout.SlotCount];

    internal Crush80Grid(object sync, Action ensureMutable)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _ensureMutable = ensureMutable ?? throw new ArgumentNullException(nameof(ensureMutable));
    }

    /// <summary>Width of the sparse canvas, including unoccupied points.</summary>
    public int Width => Crush80Layout.Width;

    /// <summary>Height of the sparse canvas, including unoccupied points.</summary>
    public int Height => Crush80Layout.Height;

    /// <summary>Colors every LED emitter belonging to a logical key in memory, until explicitly applied.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The key is not part of the provisional layout.</exception>
    public void SetKey(Crush80Key key, Rgb24 color)
    {
        lock (_sync)
        {
            _ensureMutable();
            if ((uint)key > (uint)Crush80Key.RightArrow)
                throw new ArgumentOutOfRangeException(nameof(key));

            var slots = Crush80Layout.Slots;
            for (var index = 0; index < slots.Length; index++)
            {
                if (slots[index].Key == key)
                    _frame[index] = color;
            }
        }
    }

    /// <summary>Colors every LED emitter at a populated canvas point in memory, until explicitly applied.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The coordinate is outside the canvas or is an unoccupied point.</exception>
    public void SetAt(int x, int y, Rgb24 color)
    {
        lock (_sync)
        {
            _ensureMutable();
            if ((uint)x >= Crush80Layout.Width)
                throw new ArgumentOutOfRangeException(nameof(x));
            if ((uint)y >= Crush80Layout.Height)
                throw new ArgumentOutOfRangeException(nameof(y));

            var slots = Crush80Layout.Slots;
            var first = 0;
            for (; first < slots.Length; first++)
            {
                if (slots[first].X == x && slots[first].Y == y)
                    break;
            }

            if (first == slots.Length)
                throw new ArgumentOutOfRangeException(nameof(x), "The canvas point has no LED emitter.");

            for (var index = first; index < slots.Length; index++)
            {
                if (slots[index].X == x && slots[index].Y == y)
                    _frame[index] = color;
            }
        }
    }

    /// <summary>Colors all 92 LED slots in memory, until explicitly applied.</summary>
    public void Fill(Rgb24 color)
    {
        lock (_sync)
        {
            _ensureMutable();
            _frame.AsSpan().Fill(color);
        }
    }

    internal ReadOnlyMemory<Rgb24> Frame => _frame;
}
