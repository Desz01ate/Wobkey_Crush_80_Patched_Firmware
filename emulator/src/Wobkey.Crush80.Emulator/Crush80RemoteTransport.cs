using System.Buffers.Binary;
using System.Net.Sockets;
using Wobkey.Crush80.Sdk.Models;
using Wobkey.Crush80.Sdk.Transport;

namespace Wobkey.Crush80.Emulator;

/// <summary>An <see cref="IHidTransport"/> client for a standalone Crush 80 emulator server.</summary>
public sealed class Crush80RemoteTransport : IHidTransport
{
    private static readonly Uri DefaultEndpoint = new("tcp://127.0.0.1:5081");
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly byte[] _writePacket = new byte[Crush80IpcProtocol.HidPayloadLength + 1];
    private readonly byte[] _commandPacket = new byte[5];
    private readonly byte[] _status = new byte[1];
    private int _closed;
    private int _disposeStarted;

    private Crush80RemoteTransport(TcpClient client, Uri endpoint)
    {
        _client = client;
        _stream = client.GetStream();
        Device = new Crush80DeviceDescriptor(
            endpoint.AbsoluteUri,
            0x320F,
            0x5055,
            0xFF60,
            0x61,
            null,
            "Remote Emulated Crush 80");
    }

    /// <inheritdoc />
    public Crush80DeviceDescriptor Device { get; }

    /// <summary>Connects to the default loopback endpoint, <c>tcp://127.0.0.1:5081</c>.</summary>
    public static ValueTask<Crush80RemoteTransport> ConnectAsync(
        CancellationToken cancellationToken = default) =>
        ConnectAsync(DefaultEndpoint, cancellationToken);

    /// <summary>Connects to a standalone emulator server over a loopback TCP endpoint.</summary>
    public static async ValueTask<Crush80RemoteTransport> ConnectAsync(
        Uri endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "tcp" || endpoint.Port is < 1 or > 65535)
            throw new ArgumentException("The endpoint must be an absolute tcp:// URI with a valid port.", nameof(endpoint));
        if (!string.Equals(endpoint.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The emulator transport accepts loopback endpoints only.", nameof(endpoint));

        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
            var transport = new Crush80RemoteTransport(client, endpoint);
            await transport.HandshakeAsync(cancellationToken).ConfigureAwait(false);
            return transport;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async ValueTask HandshakeAsync(CancellationToken cancellationToken)
    {
        var request = new byte[Crush80IpcProtocol.Magic.Length + 1];
        Crush80IpcProtocol.Magic.CopyTo(request);
        request[^1] = Crush80IpcProtocol.Version;
        await _stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await ReadStatusAsync(cancellationToken).ConfigureAwait(false);

        var response = new byte[Crush80IpcProtocol.Magic.Length + 1];
        await _stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.AsSpan(0, Crush80IpcProtocol.Magic.Length).SequenceEqual(Crush80IpcProtocol.Magic) ||
            response[^1] != Crush80IpcProtocol.Version)
            throw new IOException("The emulator server returned an incompatible IPC handshake.");
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length != Crush80IpcProtocol.HidPayloadLength)
            throw new ArgumentException("A VIA payload must contain exactly 32 bytes.", nameof(payload));
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            _writePacket[0] = Crush80IpcProtocol.WriteOperation;
            payload.CopyTo(_writePacket.AsMemory(1));
            await _stream.WriteAsync(_writePacket, cancellationToken).ConfigureAwait(false);
            await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            Fault();
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask ReadAsync(
        Memory<byte> payload,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length != Crush80IpcProtocol.HidPayloadLength)
            throw new ArgumentException("A VIA payload must contain exactly 32 bytes.", nameof(payload));
        var timeoutMilliseconds = ToTimeoutMilliseconds(timeout);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            _commandPacket[0] = Crush80IpcProtocol.ReadOperation;
            BinaryPrimitives.WriteInt32LittleEndian(_commandPacket.AsSpan(1), timeoutMilliseconds);
            await _stream.WriteAsync(_commandPacket, cancellationToken).ConfigureAwait(false);
            await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            await _stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            Fault();
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask ReadStatusAsync(CancellationToken cancellationToken)
    {
        await _stream.ReadExactlyAsync(_status, cancellationToken).ConfigureAwait(false);
        var status = (Crush80IpcProtocol.Status)_status[0];
        if (status == Crush80IpcProtocol.Status.Success)
            return;
        var message = await Crush80IpcProtocol.ReadErrorAsync(_stream, cancellationToken).ConfigureAwait(false);
        throw Crush80IpcProtocol.CreateRemoteException(status, message);
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return -1;
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        return Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds));
    }

    private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

    private void Fault()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
            _client.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                try
                {
                    _commandPacket[0] = Crush80IpcProtocol.CloseOperation;
                    await _stream.WriteAsync(_commandPacket.AsMemory(0, 1)).ConfigureAwait(false);
                    await ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
                {
                }
                finally
                {
                    _client.Dispose();
                }
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }
}
