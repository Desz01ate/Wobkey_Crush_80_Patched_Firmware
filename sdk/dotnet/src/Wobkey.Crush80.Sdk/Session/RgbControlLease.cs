namespace Wobkey.Crush80;

/// <summary>Owns exclusive RGB control until the captured device state is restored or the lease is disposed.</summary>
public sealed class RgbControlLease : IAsyncDisposable
{
    private readonly Crush80RgbSession _session;
    private readonly RgbDeviceState _savedState;
    private readonly Rgb24[] _initialFrame;
    private readonly bool _restoreStateOnDispose;
    private int _restored;

    internal RgbControlLease(Crush80RgbSession session, RgbDeviceState savedState, Rgb24[] initialFrame, bool restoreStateOnDispose)
    {
        _session = session;
        _savedState = savedState;
        _initialFrame = initialFrame;
        _restoreStateOnDispose = restoreStateOnDispose;
    }

    internal RgbDeviceState SavedState => _savedState;
    internal ReadOnlyMemory<Rgb24> InitialFrame => _initialFrame;
    internal void MarkRestored() => Volatile.Write(ref _restored, 1);

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
            return true;
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
