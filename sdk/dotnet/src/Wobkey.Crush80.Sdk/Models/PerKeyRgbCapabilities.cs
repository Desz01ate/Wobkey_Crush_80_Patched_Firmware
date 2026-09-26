namespace Wobkey.Crush80;

/// <summary>Describes the per-key RGB protocol capabilities reported by firmware.</summary>
/// <param name="ProtocolVersion">The per-key RGB protocol version.</param>
/// <param name="LedCount">The number of addressable LEDs.</param>
/// <param name="ChunkLimit">The maximum number of LEDs in one transfer chunk.</param>
/// <param name="Enabled">Whether per-key RGB control is enabled.</param>
public sealed record PerKeyRgbCapabilities(
    byte ProtocolVersion,
    int LedCount,
    int ChunkLimit,
    bool Enabled);
