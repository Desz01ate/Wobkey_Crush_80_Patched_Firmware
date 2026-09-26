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

    internal async ValueTask<T> ExecuteAsync<T>(
        string operation,
        Func<PkrgV1Client, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposedOrFaulted(operation);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposedOrFaulted(operation);
            try
            {
                return await action(_client, cancellationToken).ConfigureAwait(false);
            }
            catch (FirmwareRejectedRequestException)
            {
                throw;
            }
            catch (StateRestoreException exception) when (exception.InnerException is FirmwareRejectedRequestException or AggregateException)
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
        CancellationToken cancellationToken)
    {
        await ExecuteAsync<object?>(operation, async (client, token) =>
        {
            await action(client, token).ConfigureAwait(false);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposedOrFaulted(string operation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _fault) is { } fault)
            throw new SessionFaultedException(operation, Device, fault);
    }

    /// <summary>Stops accepting operations, waits for the active exchange, and closes the transport.</summary>
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
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }
}
