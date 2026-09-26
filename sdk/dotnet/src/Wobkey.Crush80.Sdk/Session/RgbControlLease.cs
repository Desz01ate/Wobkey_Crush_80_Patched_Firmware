using Wobkey.Crush80.Protocol;

namespace Wobkey.Crush80;

/// <summary>Owns exclusive RGB control until the captured device state is restored or the lease is disposed.</summary>
public sealed class RgbControlLease : IAsyncDisposable
{
    private readonly Crush80RgbSession _session;
    private readonly RgbDeviceState _savedState;
    private readonly Rgb24[] _acknowledgedFrame;
    private readonly Rgb24[] _chunkScratch = new Rgb24[PkrgV1Codec.ChunkLimit];
    private readonly Rgb24[] _fillFrame = new Rgb24[PkrgV1Codec.LedCount];
    private readonly bool _restoreStateOnDispose;
    private int _restored;
    private int _enabled = 1;

    internal RgbControlLease(Crush80RgbSession session, RgbDeviceState savedState, Rgb24[] initialFrame, bool restoreStateOnDispose)
    {
        _session = session;
        _savedState = savedState;
        _acknowledgedFrame = initialFrame;
        _restoreStateOnDispose = restoreStateOnDispose;
    }

    internal RgbDeviceState SavedState => _savedState;
    internal bool RestoreStateOnDispose => _restoreStateOnDispose;
    internal void MarkRestored() => Volatile.Write(ref _restored, 1);
    internal void MarkEnabled(bool enabled) => Volatile.Write(ref _enabled, enabled ? 1 : 0);

    /// <summary>Gets the negotiated device capabilities while this lease is active.</summary>
    public PerKeyRgbCapabilities Capabilities
    {
        get
        {
            ThrowIfRestored();
            return _session.Capabilities;
        }
    }

    /// <summary>Gets whether the RGB override is enabled by this lease.</summary>
    public bool Enabled
    {
        get
        {
            ThrowIfRestored();
            return Volatile.Read(ref _enabled) != 0;
        }
    }

    /// <summary>Writes changed eight-LED chunks of a complete frame. Keep the supplied memory unchanged until completion.</summary>
    public ValueTask WriteFrameAsync(ReadOnlyMemory<Rgb24> colors, CancellationToken cancellationToken = default)
    {
        if (colors.Length != PkrgV1Codec.LedCount)
            throw new ArgumentException($"A full frame requires exactly {PkrgV1Codec.LedCount} colors.", nameof(colors));
        ThrowIfRestored();
        return _session.ExecuteAsync("WriteFrame", (client, token) =>
            WriteCachedRangeAsync(client, 0, colors, token), cancellationToken, expectedLease: this);
    }

    /// <summary>Writes changed chunks of an adjacent range. Keep the supplied memory unchanged until completion.</summary>
    public ValueTask WriteRangeAsync(int startIndex, ReadOnlyMemory<Rgb24> colors, CancellationToken cancellationToken = default)
    {
        if (startIndex < 0 || startIndex >= PkrgV1Codec.LedCount)
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        if (colors.Length < 1 || colors.Length > PkrgV1Codec.LedCount - startIndex)
            throw new ArgumentOutOfRangeException(nameof(colors));
        ThrowIfRestored();
        return _session.ExecuteAsync("WriteRange", (client, token) =>
            WriteCachedRangeAsync(client, startIndex, colors, token), cancellationToken, expectedLease: this);
    }

    /// <summary>Fills the frame with one color, sending only changed chunks.</summary>
    public ValueTask FillAsync(Rgb24 color, CancellationToken cancellationToken = default)
    {
        ThrowIfRestored();
        return _session.ExecuteAsync("Fill", (client, token) => FillCoreAsync(client, color, token),
            cancellationToken, expectedLease: this);
    }

    /// <summary>Reads the raw RGB frame before hardware brightness is applied.</summary>
    public ValueTask ReadFrameAsync(Memory<Rgb24> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length != PkrgV1Codec.LedCount)
            throw new ArgumentException($"A full frame requires exactly {PkrgV1Codec.LedCount} colors.", nameof(destination));
        ThrowIfRestored();
        return _session.ExecuteAsync("ReadFrame", (client, token) =>
            client.ReadRangeAsync(0, destination, token), cancellationToken, expectedLease: this);
    }

    /// <summary>Sets the OEM hardware brightness from zero through nine.</summary>
    public ValueTask SetHardwareBrightnessAsync(byte brightness, CancellationToken cancellationToken = default)
    {
        if (brightness > 9)
            throw new ArgumentOutOfRangeException(nameof(brightness));
        ThrowIfRestored();
        return _session.ExecuteAsync("SetBrightness", (client, token) =>
            client.SetBrightnessAsync(brightness, token), cancellationToken, expectedLease: this);
    }

    private async ValueTask FillCoreAsync(PkrgV1Client client, Rgb24 color, CancellationToken token)
    {
        _fillFrame.AsSpan().Fill(color);
        await WriteCachedRangeAsync(client, 0, _fillFrame, token).ConfigureAwait(false);
    }

    private async ValueTask WriteCachedRangeAsync(
        PkrgV1Client client, int startIndex, ReadOnlyMemory<Rgb24> colors, CancellationToken token)
    {
        for (var offset = 0; offset < colors.Length; offset += PkrgV1Codec.ChunkLimit)
        {
            var count = Math.Min(PkrgV1Codec.ChunkLimit, colors.Length - offset);
            var start = startIndex + offset;
            var source = colors.Slice(offset, count);
            if (source.Span.SequenceEqual(_acknowledgedFrame.AsSpan(start, count)))
                continue;

            source.Span.CopyTo(_chunkScratch);
            await client.WriteRangeAsync(start, _chunkScratch.AsMemory(0, count), token).ConfigureAwait(false);
            _chunkScratch.AsSpan(0, count).CopyTo(_acknowledgedFrame.AsSpan(start, count));
        }
    }

    /// <summary>Restores the original RGB state and releases exclusive control.</summary>
    public ValueTask RestoreAsync()
    {
        ThrowIfRestored();
        return _session.RestoreLeaseAsync(this, restore: true);
    }

    /// <summary>Releases this lease, restoring the snapshot unless the options opted out.</summary>
    public ValueTask DisposeAsync() => Volatile.Read(ref _restored) != 0
        ? ValueTask.CompletedTask
        : _session.RestoreLeaseAsync(this, _restoreStateOnDispose);

    private void ThrowIfRestored() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _restored) != 0, this);
}
