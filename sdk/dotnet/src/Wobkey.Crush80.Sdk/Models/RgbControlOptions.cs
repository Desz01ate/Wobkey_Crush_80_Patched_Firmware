namespace Wobkey.Crush80;

/// <summary>Configures a temporary RGB control lease.</summary>
public sealed record RgbControlOptions
{
    private byte _hardwareBrightness = 9;

    /// <summary>Gets or initializes the hardware brightness, from 0 through 9.</summary>
    public byte HardwareBrightness
    {
        get => _hardwareBrightness;
        init
        {
            if (value > 9)
                throw new ArgumentOutOfRangeException(nameof(HardwareBrightness), "Hardware brightness must be between 0 and 9.");

            _hardwareBrightness = value;
        }
    }

    /// <summary>Gets or initializes whether the captured state is restored when the lease is disposed.</summary>
    public bool RestoreStateOnDispose { get; init; } = true;
}
