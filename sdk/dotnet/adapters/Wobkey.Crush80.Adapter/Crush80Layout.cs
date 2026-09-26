namespace Wobkey.Crush80.Adapter;

/// <summary>One firmware RGB slot in the built-in Crush 80 canvas.</summary>
/// <param name="Key">Logical key illuminated by the slot.</param>
/// <param name="X">Horizontal canvas coordinate.</param>
/// <param name="Y">Vertical canvas coordinate.</param>
public readonly record struct Crush80Led(Crush80Key Key, byte X, byte Y);

/// <summary>
/// Firmware-slot ordering and sparse canvas coordinates derived from the wired SignalRGB plugin;
/// mapped key assignments and row-major order have since been visually verified on hardware.
/// </summary>
public static class Crush80Layout
{
    /// <summary>Width of the sparse LED canvas.</summary>
    public const int Width = 37;
    /// <summary>Height of the sparse LED canvas.</summary>
    public const int Height = 12;
    /// <summary>Number of addressable firmware RGB slots.</summary>
    public const int SlotCount = 92;

    private static readonly Crush80Led[] OrderedSlots =
    [
        new(Crush80Key.Esc, 0, 0), new(Crush80Key.F1, 3, 0), new(Crush80Key.F2, 5, 0), new(Crush80Key.F3, 7, 0),
        new(Crush80Key.F4, 9, 0), new(Crush80Key.F5, 11, 0), new(Crush80Key.F6, 13, 0), new(Crush80Key.F7, 15, 0),
        new(Crush80Key.F8, 17, 0), new(Crush80Key.F9, 20, 0), new(Crush80Key.F10, 22, 0), new(Crush80Key.F11, 24, 0),
        new(Crush80Key.F12, 26, 0), new(Crush80Key.AudioMute, 28, 0), new(Crush80Key.PrintScreen, 31, 0),
        new(Crush80Key.ScrollLock, 33, 0), new(Crush80Key.PauseBreak, 35, 0),
        new(Crush80Key.GraveAccent, 0, 3), new(Crush80Key.Digit1, 2, 3), new(Crush80Key.Digit2, 4, 3),
        new(Crush80Key.Digit3, 6, 3), new(Crush80Key.Digit4, 8, 3), new(Crush80Key.Digit5, 10, 3),
        new(Crush80Key.Digit6, 12, 3), new(Crush80Key.Digit7, 14, 3), new(Crush80Key.Digit8, 16, 3),
        new(Crush80Key.Digit9, 18, 3), new(Crush80Key.Digit0, 20, 3), new(Crush80Key.Minus, 22, 3),
        new(Crush80Key.Equals, 24, 3), new(Crush80Key.Backspace, 27, 3), new(Crush80Key.Insert, 31, 3),
        new(Crush80Key.Home, 33, 3), new(Crush80Key.PageUp, 35, 3),
        new(Crush80Key.Tab, 1, 5), new(Crush80Key.Q, 3, 5), new(Crush80Key.W, 5, 5), new(Crush80Key.E, 7, 5),
        new(Crush80Key.R, 9, 5), new(Crush80Key.T, 11, 5), new(Crush80Key.Y, 13, 5), new(Crush80Key.U, 15, 5),
        new(Crush80Key.I, 17, 5), new(Crush80Key.O, 19, 5), new(Crush80Key.P, 21, 5),
        new(Crush80Key.LeftBracket, 23, 5), new(Crush80Key.RightBracket, 25, 5),
        new(Crush80Key.Backslash, 28, 5), new(Crush80Key.Delete, 31, 5), new(Crush80Key.End, 33, 5),
        new(Crush80Key.PageDown, 35, 5),
        new(Crush80Key.CapsLock, 1, 7), new(Crush80Key.CapsLock, 1, 7),
        new(Crush80Key.A, 4, 7), new(Crush80Key.S, 6, 7), new(Crush80Key.D, 8, 7), new(Crush80Key.F, 10, 7),
        new(Crush80Key.G, 12, 7), new(Crush80Key.H, 14, 7), new(Crush80Key.J, 16, 7), new(Crush80Key.K, 18, 7),
        new(Crush80Key.L, 20, 7), new(Crush80Key.Semicolon, 22, 7), new(Crush80Key.Apostrophe, 24, 7),
        new(Crush80Key.IsoHash, 26, 7), new(Crush80Key.Enter, 27, 7),
        new(Crush80Key.LeftShift, 1, 9), new(Crush80Key.Z, 5, 9), new(Crush80Key.X, 7, 9),
        new(Crush80Key.C, 9, 9), new(Crush80Key.V, 11, 9), new(Crush80Key.B, 13, 9),
        new(Crush80Key.N, 15, 9), new(Crush80Key.M, 17, 9), new(Crush80Key.Comma, 19, 9),
        new(Crush80Key.Period, 21, 9), new(Crush80Key.Slash, 23, 9), new(Crush80Key.RightShift, 26, 9),
        new(Crush80Key.UpArrow, 33, 9),
        new(Crush80Key.LeftCtrl, 0, 11), new(Crush80Key.LeftWin, 3, 11), new(Crush80Key.LeftAlt, 5, 11),
        new(Crush80Key.Space, 9, 11), new(Crush80Key.Space, 13, 11), new(Crush80Key.Space, 17, 11),
        new(Crush80Key.RightAlt, 20, 11), new(Crush80Key.Fn, 23, 11), new(Crush80Key.RightWin, 25, 11),
        new(Crush80Key.RightCtrl, 28, 11), new(Crush80Key.LeftArrow, 31, 11),
        new(Crush80Key.DownArrow, 33, 11), new(Crush80Key.RightArrow, 35, 11)
    ];

    private static readonly IReadOnlyList<Crush80Led> PublicSlots = Array.AsReadOnly(OrderedSlots);

    /// <summary>Gets all LED slots in firmware index order.</summary>
    public static IReadOnlyList<Crush80Led> LedSlots => PublicSlots;

    internal static ReadOnlySpan<Crush80Led> Slots => OrderedSlots;
    internal static Crush80Key KeyAt(int index) => OrderedSlots[index].Key;

}
