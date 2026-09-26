using Wobkey.Crush80.Sdk.Models;

namespace Wobkey.Crush80.Emulator;

/// <summary>An immutable snapshot of the emulated keyboard state.</summary>
public sealed class Crush80EmulatorState
{
    private readonly Rgb24[] _colors;

    internal Crush80EmulatorState(
        long version,
        bool connected,
        bool enabled,
        byte brightness,
        byte effect,
        Rgb24[] colors)
    {
        Version = version;
        Connected = connected;
        Enabled = enabled;
        Brightness = brightness;
        Effect = effect;
        _colors = colors;
    }

    /// <summary>Monotonically increasing state version.</summary>
    public long Version { get; }

    /// <summary>Whether the emulated transport remains open.</summary>
    public bool Connected { get; }

    /// <summary>Whether the per-key override is enabled.</summary>
    public bool Enabled { get; }

    /// <summary>OEM hardware brightness, from 0 through 9.</summary>
    public byte Brightness { get; }

    /// <summary>Selected OEM lighting effect, from 0 through 18.</summary>
    public byte Effect { get; }

    /// <summary>Gets the 92 supplied RGB values in firmware slot order.</summary>
    public ReadOnlyMemory<Rgb24> Colors => _colors;
}
