namespace Wobkey.Crush80.Sdk.Models;

/// <summary>Configures communication with a Crush 80 device.</summary>
public sealed record Crush80SessionOptions
{
    private TimeSpan _responseTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Gets or initializes the maximum time to wait for a device response.</summary>
    public TimeSpan ResponseTimeout
    {
        get => _responseTimeout;
        init
        {
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(ResponseTimeout), "The response timeout must be positive and finite.");

            _responseTimeout = value;
        }
    }
}
