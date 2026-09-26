namespace Wobkey.Crush80;

/// <summary>A captured Crush 80 RGB device state.</summary>
public sealed class RgbDeviceState
{
    private readonly Rgb24[] _colors;

    internal RgbDeviceState(ReadOnlySpan<Rgb24> colors, bool enabled, byte brightness, byte effect)
    {
        if (colors.Length != 92)
            throw new ArgumentException("A captured Crush 80 state requires exactly 92 colors.", nameof(colors));

        _colors = colors.ToArray();
        Enabled = enabled;
        Brightness = brightness;
        Effect = effect;
    }

    // The capture path creates this array itself and never exposes its writable reference.
    internal static RgbDeviceState FromCapturedFrame(Rgb24[] colors, bool enabled, byte brightness, byte effect) =>
        new(colors, enabled, brightness, effect);

    private RgbDeviceState(Rgb24[] colors, bool enabled, byte brightness, byte effect)
    {
        if (colors.Length != 92)
            throw new ArgumentException("A captured Crush 80 state requires exactly 92 colors.", nameof(colors));

        _colors = colors;
        Enabled = enabled;
        Brightness = brightness;
        Effect = effect;
    }

    /// <summary>Gets the captured color for each of the 92 LEDs.</summary>
    public ReadOnlyMemory<Rgb24> Colors => _colors;

    /// <summary>Gets whether the RGB device was enabled.</summary>
    public bool Enabled { get; }

    /// <summary>Gets the captured hardware brightness.</summary>
    public byte Brightness { get; }

    /// <summary>Gets the captured effect identifier.</summary>
    public byte Effect { get; }
}
