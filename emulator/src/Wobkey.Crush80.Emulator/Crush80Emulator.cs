namespace Wobkey.Crush80.Emulator;

/// <summary>Owns an emulated SDK transport and its loopback browser visualizer.</summary>
public sealed class Crush80Emulator : IAsyncDisposable
{
    private readonly Crush80Visualizer _visualizer;
    private int _disposed;

    private Crush80Emulator(Crush80EmulatedTransport transport, Crush80Visualizer visualizer)
    {
        Transport = transport;
        _visualizer = visualizer;
    }

    /// <summary>Gets the transport to inject into the SDK or grid adapter.</summary>
    public Crush80EmulatedTransport Transport { get; }

    /// <summary>Gets the local visualizer address.</summary>
    public Uri VisualizerUri => _visualizer.Uri;

    /// <summary>Starts an emulator bound only to the IPv4 loopback interface.</summary>
    /// <param name="port">Loopback TCP port, or zero to select an available port.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    public static async ValueTask<Crush80Emulator> StartAsync(
        int port = 0,
        CancellationToken cancellationToken = default)
    {
        var transport = new Crush80EmulatedTransport();
        try
        {
            var visualizer = await Crush80Visualizer.StartAsync(transport, port, cancellationToken)
                .ConfigureAwait(false);
            return new Crush80Emulator(transport, visualizer);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Stops the visualizer and closes the transport if its SDK owner has not already done so.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _visualizer.DisposeAsync().ConfigureAwait(false);
        await Transport.DisposeAsync().ConfigureAwait(false);
    }
}
