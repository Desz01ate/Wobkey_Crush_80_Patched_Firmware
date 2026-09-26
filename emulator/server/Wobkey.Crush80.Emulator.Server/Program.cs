using System.Diagnostics;
using Wobkey.Crush80.Emulator;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var openBrowser, out var visualizerPort, out var transportPort))
            return 2;
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            PrintUsage();
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            await using var server = await Crush80EmulatorServer.StartAsync(
                visualizerPort,
                transportPort,
                cancellation.Token);
            Console.WriteLine($"Visualizer: {server.VisualizerUri}");
            Console.WriteLine($"Transport: {server.TransportEndpoint}");
            Console.WriteLine("Idle emulator server running; waiting for an SDK client. Press Ctrl+C to stop.");

            if (openBrowser)
                TryOpenBrowser(server.VisualizerUri);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            return 0;
        }
        catch (Exception error)
        {
            await Console.Error.WriteLineAsync(error.ToString());
            return 1;
        }
    }

    private static bool TryParseArguments(
        string[] args,
        out bool openBrowser,
        out int visualizerPort,
        out int transportPort)
    {
        openBrowser = false;
        visualizerPort = 5080;
        transportPort = 5081;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--help":
                    break;
                case "--open":
                    openBrowser = true;
                    break;
                case "--visualizer-port" when TryReadPort(args, ref index, out visualizerPort):
                    break;
                case "--transport-port" when TryReadPort(args, ref index, out transportPort):
                    break;
                default:
                    Console.Error.WriteLine($"Unknown or invalid argument: {args[index]}");
                    PrintUsage();
                    return false;
            }
        }

        return true;
    }

    private static bool TryReadPort(string[] args, ref int index, out int port)
    {
        port = 0;
        return index + 1 < args.Length &&
            int.TryParse(args[++index], out port) &&
            port is >= 0 and <= 65535;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run --project emulator/server/Wobkey.Crush80.Emulator.Server -- [options]");
        Console.WriteLine("  --open                   Open the visualizer in the default browser.");
        Console.WriteLine("  --visualizer-port PORT   Browser port; default 5080, zero selects an available port.");
        Console.WriteLine("  --transport-port PORT    SDK IPC port; default 5081, zero selects an available port.");
    }

    private static void TryOpenBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Could not open the browser: {error.Message}");
        }
    }
}
