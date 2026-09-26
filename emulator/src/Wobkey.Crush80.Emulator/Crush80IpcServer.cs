using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Wobkey.Crush80.Emulator;

internal sealed class Crush80IpcServer : IAsyncDisposable
{
    private readonly Crush80EmulatedTransport _transport;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _clientSync = new();
    private Task _activeClient = Task.CompletedTask;
    private readonly Task _acceptLoop;
    private int _disposed;

    private Crush80IpcServer(Crush80EmulatedTransport transport, TcpListener listener)
    {
        _transport = transport;
        _listener = listener;
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        Endpoint = new Uri($"tcp://127.0.0.1:{endpoint.Port}");
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
    }

    internal Uri Endpoint { get; }

    internal static Crush80IpcServer Start(Crush80EmulatedTransport transport, int port)
    {
        if ((uint)port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 0 and 65535.");
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return new Crush80IpcServer(transport, listener);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var busy = false;
            lock (_clientSync)
            {
                if (!_activeClient.IsCompleted)
                    busy = true;
                else
                    _activeClient = HandleClientAsync(client, cancellationToken);
            }

            if (busy)
                await RejectBusyClientAsync(client, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RejectBusyClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await Crush80IpcProtocol.WriteErrorAsync(
                    client.GetStream(),
                    Crush80IpcProtocol.Status.Busy,
                    "Another SDK client already owns the emulated keyboard.",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
            {
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var connected = false;
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            try
            {
                var handshake = new byte[Crush80IpcProtocol.Magic.Length + 1];
                await stream.ReadExactlyAsync(handshake, cancellationToken).ConfigureAwait(false);
                if (!handshake.AsSpan(0, Crush80IpcProtocol.Magic.Length).SequenceEqual(Crush80IpcProtocol.Magic) ||
                    handshake[^1] != Crush80IpcProtocol.Version)
                {
                    await Crush80IpcProtocol.WriteErrorAsync(
                        stream,
                        Crush80IpcProtocol.Status.IncompatibleVersion,
                        "The emulator IPC protocol version is incompatible.",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                var response = new byte[Crush80IpcProtocol.Magic.Length + 2];
                response[0] = (byte)Crush80IpcProtocol.Status.Success;
                Crush80IpcProtocol.Magic.CopyTo(response.AsSpan(1));
                response[^1] = Crush80IpcProtocol.Version;
                await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                _transport.SetClientConnected(true);
                connected = true;

                var operation = new byte[1];
                var payload = new byte[Crush80IpcProtocol.HidPayloadLength];
                var timeoutBytes = new byte[4];
                while (!cancellationToken.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(operation, cancellationToken).ConfigureAwait(false);
                    switch (operation[0])
                    {
                        case Crush80IpcProtocol.WriteOperation:
                            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
                            try
                            {
                                await _transport.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                                await Crush80IpcProtocol.WriteSuccessAsync(stream, cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception error)
                            {
                                await WriteOperationErrorAsync(stream, error, cancellationToken).ConfigureAwait(false);
                            }
                            break;

                        case Crush80IpcProtocol.ReadOperation:
                            await stream.ReadExactlyAsync(timeoutBytes, cancellationToken).ConfigureAwait(false);
                            var timeoutMilliseconds = BinaryPrimitives.ReadInt32LittleEndian(timeoutBytes);
                            var timeout = timeoutMilliseconds < 0
                                ? Timeout.InfiniteTimeSpan
                                : TimeSpan.FromMilliseconds(timeoutMilliseconds);
                            try
                            {
                                await _transport.ReadAsync(payload, timeout, cancellationToken).ConfigureAwait(false);
                                await Crush80IpcProtocol.WriteSuccessAsync(stream, cancellationToken).ConfigureAwait(false);
                                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception error)
                            {
                                await WriteOperationErrorAsync(stream, error, cancellationToken).ConfigureAwait(false);
                            }
                            break;

                        case Crush80IpcProtocol.CloseOperation:
                            await Crush80IpcProtocol.WriteSuccessAsync(stream, cancellationToken).ConfigureAwait(false);
                            return;

                        default:
                            await Crush80IpcProtocol.WriteErrorAsync(
                                stream,
                                Crush80IpcProtocol.Status.InvalidOperation,
                                "The emulator IPC operation is unsupported.",
                                cancellationToken).ConfigureAwait(false);
                            return;
                    }
                }
            }
            catch (Exception error) when (error is EndOfStreamException or IOException or SocketException ||
                error is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                if (connected)
                    _transport.SetClientConnected(false);
            }
        }
    }

    private static ValueTask WriteOperationErrorAsync(
        Stream stream,
        Exception error,
        CancellationToken cancellationToken)
    {
        var status = error switch
        {
            TimeoutException => Crush80IpcProtocol.Status.Timeout,
            InvalidOperationException or ArgumentException => Crush80IpcProtocol.Status.InvalidOperation,
            _ => Crush80IpcProtocol.Status.ServerError
        };
        return Crush80IpcProtocol.WriteErrorAsync(stream, status, error.Message, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException)
        {
        }

        Task activeClient;
        lock (_clientSync)
            activeClient = _activeClient;
        await activeClient.ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
