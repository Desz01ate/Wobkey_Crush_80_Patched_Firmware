using Wobkey.Crush80.Protocol;
using Wobkey.Crush80.Transport;

namespace Wobkey.Crush80;

/// <summary>Owns an exclusively serialized request stream to one compatible Crush 80 RGB device.</summary>
public sealed class Crush80RgbSession : IAsyncDisposable
{
    private readonly IHidTransport _transport;
    private readonly PkrgV1Client _client;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private Exception? _fault;
    private int _disposed;
    private Task? _disposeTask;
    private RgbControlLease? _activeLease;

    private Crush80RgbSession(IHidTransport transport, PkrgV1Client client, PerKeyRgbCapabilities capabilities)
    {
        _transport = transport;
        _client = client;
        Capabilities = capabilities;
        Advanced = new Crush80RgbAdvanced(this);
    }

    /// <summary>Gets the connected device identity.</summary>
    public Crush80DeviceDescriptor Device => _transport.Device;

    /// <summary>Gets the exact protocol capabilities negotiated on opening.</summary>
    public PerKeyRgbCapabilities Capabilities { get; }

    /// <summary>Gets explicit device-level RGB and OEM operations.</summary>
    public Crush80RgbAdvanced Advanced { get; }

    /// <summary>Captures the current state and takes exclusive ownership of RGB mutations.</summary>
    public ValueTask<RgbControlLease> AcquireControlAsync(
        ReadOnlyMemory<Rgb24> initialFrame,
        RgbControlOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (initialFrame.Length != Capabilities.LedCount)
            throw new ArgumentException($"A full frame requires exactly {Capabilities.LedCount} colors.", nameof(initialFrame));
        options ??= new RgbControlOptions();
        var frame = initialFrame.ToArray();

        return ExecuteAsync("AcquireControl", async (client, token) =>
        {
            var colors = new Rgb24[Capabilities.LedCount];
            await client.ReadRangeAsync(0, colors, token).ConfigureAwait(false);
            var enabled = await client.GetEnabledAsync(token).ConfigureAwait(false);
            var brightness = await client.GetBrightnessAsync(token).ConfigureAwait(false);
            var effect = await client.GetEffectAsync(token).ConfigureAwait(false);
            var saved = RgbDeviceState.FromCapturedFrame(colors, enabled, brightness, effect);

            try
            {
                await client.SetEnabledAsync(false, token).ConfigureAwait(false);
                await client.WriteRangeAsync(0, frame, token).ConfigureAwait(false);
                await client.SetBrightnessAsync(options.HardwareBrightness, token).ConfigureAwait(false);
                await client.SetEffectAsync(6, token).ConfigureAwait(false);
                await client.SetEnabledAsync(true, token).ConfigureAwait(false);
            }
            catch (Exception acquisitionError)
            {
                if (acquisitionError is not FirmwareRejectedRequestException and not OperationCanceledException)
                    Volatile.Write(ref _fault, acquisitionError);
                try
                {
                    await RestoreStateCoreAsync(client, saved, CancellationToken.None).ConfigureAwait(false);
                }
                catch (StateRestoreException restorationError)
                {
                    throw new StateRestoreException(restorationError.Failures, acquisitionError);
                }
                throw;
            }

            return _activeLease = new RgbControlLease(this, saved, frame, options.RestoreStateOnDispose);
        }, cancellationToken, requireNoLease: true);
    }

    internal ValueTask RestoreLeaseAsync(RgbControlLease lease, bool restore) =>
        ExecuteAsync("RestoreState", async (client, token) =>
        {
            if (restore)
                await RestoreStateCoreAsync(client, lease.SavedState, token, lease).ConfigureAwait(false);
            _activeLease = null;
            lease.MarkRestored();
        }, CancellationToken.None, expectedLease: lease, allowFaulted: restore);

    private static readonly string[] RestorationFields =
        ["OverrideDisable", "Colors", "Brightness", "Effect", "OverrideEnable"];

    internal async ValueTask RestoreStateCoreAsync(
        PkrgV1Client client, RgbDeviceState state, CancellationToken token, RgbControlLease? lease = null)
    {
        List<StateRestoreFailure>? failures = null;
        var disabled = false;
        var colorsRestored = false;
        for (var step = 0; step < RestorationFields.Length; step++)
        {
            if (Volatile.Read(ref _fault) is { } fault)
            {
                (failures ??= []).Add(new StateRestoreFailure(RestorationFields[step],
                    new SessionFaultedException("RestoreState", Device, fault)));
                continue;
            }

            try
            {
                switch (step)
                {
                    case 0:
                        await client.SetEnabledAsync(false, token).ConfigureAwait(false);
                        disabled = true;
                        lease?.MarkEnabled(false);
                        break;
                    case 1:
                        await client.WriteRangeAsync(0, state.Colors, token).ConfigureAwait(false);
                        colorsRestored = true;
                        break;
                    case 2:
                        await client.SetBrightnessAsync(state.Brightness, token).ConfigureAwait(false);
                        break;
                    case 3:
                        await client.SetEffectAsync(state.Effect, token).ConfigureAwait(false);
                        break;
                    case 4 when disabled && !colorsRestored && state.Enabled:
                        // Do not turn on an incompletely restored frame after a successful disable.
                        break;
                    case 4:
                        await client.SetEnabledAsync(state.Enabled, token).ConfigureAwait(false);
                        lease?.MarkEnabled(state.Enabled);
                        break;
                }
            }
            catch (FirmwareRejectedRequestException error)
            {
                (failures ??= []).Add(new StateRestoreFailure(RestorationFields[step], error));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                (failures ??= []).Add(new StateRestoreFailure(RestorationFields[step], error));
                Volatile.Write(ref _fault, error);
            }
        }

        if (failures is { Count: > 0 })
            throw new StateRestoreException(failures);
    }

    /// <summary>Opens a session over an injected transport, transferring ownership of it to the session.</summary>
    public static async ValueTask<Crush80RgbSession> OpenAsync(
        IHidTransport transport,
        Crush80SessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        try
        {
            var client = new PkrgV1Client(transport, (options ?? new Crush80SessionOptions()).ResponseTimeout);
            var capabilities = await client.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            // The codec validates signature, version, LED count, chunk limit and enabled flag.
            return new Crush80RgbSession(transport, client, capabilities);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Opens the exact discovered wired VIA interface, then negotiates its protocol.</summary>
    public static ValueTask<Crush80RgbSession> OpenAsync(
        Crush80DeviceDescriptor descriptor,
        Crush80SessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new Crush80SessionOptions();
        return OpenAsync(HidSharpTransport.Open(descriptor, options.ResponseTimeout), options, cancellationToken);
    }

    /// <summary>Opens the first discovered wired VIA interface.</summary>
    public static ValueTask<Crush80RgbSession> OpenFirstAsync(
        Crush80SessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var devices = Crush80DeviceLocator.Enumerate();
        if (devices.Count == 0)
            throw new DeviceNotFoundException("Open");

        return OpenAsync(devices[0], options, cancellationToken);
    }

    internal async ValueTask<T> ExecuteAsync<T>(
        string operation,
        Func<PkrgV1Client, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken,
        bool requireNoLease = false,
        RgbControlLease? expectedLease = null,
        bool allowFaulted = false)
    {
        ThrowIfDisposedOrFaulted(operation, allowFaulted);
        cancellationToken.ThrowIfCancellationRequested();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposedOrFaulted(operation, allowFaulted);
            cancellationToken.ThrowIfCancellationRequested();
            if (requireNoLease && _activeLease is not null)
                throw new InvalidOperationException("Direct RGB mutations are unavailable while a control lease is active.");
            if (expectedLease is not null && !ReferenceEquals(_activeLease, expectedLease))
                throw new ObjectDisposedException(nameof(RgbControlLease));
            try
            {
                return await action(_client, cancellationToken).ConfigureAwait(false);
            }
            catch (FirmwareRejectedRequestException)
            {
                throw;
            }
            catch (StateRestoreException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _fault, exception);
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal async ValueTask ExecuteAsync(
        string operation,
        Func<PkrgV1Client, CancellationToken, ValueTask> action,
        CancellationToken cancellationToken,
        bool requireNoLease = false,
        RgbControlLease? expectedLease = null,
        bool allowFaulted = false)
    {
        await ExecuteAsync<object?>(operation, async (client, token) =>
        {
            await action(client, token).ConfigureAwait(false);
            return null;
        }, cancellationToken, requireNoLease, expectedLease, allowFaulted).ConfigureAwait(false);
    }

    private void ThrowIfDisposedOrFaulted(string operation, bool allowFaulted = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!allowFaulted && Volatile.Read(ref _fault) is { } fault)
            throw new SessionFaultedException(operation, Device, fault);
    }

    /// <summary>Stops accepting operations, restores an active lease when configured, then closes the transport.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_operationGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                if (_activeLease is { } lease)
                {
                    if (lease.RestoreStateOnDispose)
                        await RestoreStateCoreAsync(_client, lease.SavedState, CancellationToken.None, lease)
                            .ConfigureAwait(false);
                    _activeLease = null;
                    lease.MarkRestored();
                }
            }
            finally
            {
                _activeLease?.MarkRestored();
                _activeLease = null;
                await _transport.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }
}
