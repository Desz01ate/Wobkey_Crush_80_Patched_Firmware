namespace Wobkey.Crush80.Sdk.Models;

/// <summary>Describes the per-key RGB protocol capabilities reported by firmware.</summary>
/// <param name="ProtocolVersion">The per-key RGB protocol version.</param>
/// <param name="LedCount">The number of addressable LEDs.</param>
/// <param name="ChunkLimit">The maximum number of LEDs in one transfer chunk.</param>
/// <param name="Enabled">Whether per-key RGB control is enabled.</param>
public sealed record PerKeyRgbCapabilities(
    byte ProtocolVersion,
    int LedCount,
    int ChunkLimit,
    bool Enabled)
{
    /// <summary>The advertised LEDs per frame fragment, if reported by PKRG v2.</summary>
    public int? StreamLedCount { get; init; }

    /// <summary>The advertised fragment count, if reported by PKRG v2.</summary>
    public int? StreamChunkCount { get; init; }

    /// <summary>Whether firmware advertises the PKRG v2 frame-streaming format. SDK writes remain sequential.</summary>
    public bool SupportsFrameStreaming =>
        ProtocolVersion == 2 && StreamLedCount is 9 && StreamChunkCount is 11;
}
