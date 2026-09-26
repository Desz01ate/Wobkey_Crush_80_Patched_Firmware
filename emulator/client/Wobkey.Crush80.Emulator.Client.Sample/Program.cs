using Wobkey.Crush80.Adapter;
using Wobkey.Crush80.Emulator;
using Wobkey.Crush80.Sdk.Models;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var endpoint, out var seconds))
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
        if (seconds > 0)
            cancellation.CancelAfter(TimeSpan.FromSeconds(seconds));

        try
        {
            var transport = await Crush80RemoteTransport.ConnectAsync(endpoint, cancellation.Token);
            await using var keyboard = await Crush80Keyboard.OpenAsync(transport, cancellation.Token);

            keyboard.Grid.SetAll(default);
            keyboard.Grid.SetKey(Crush80Key.Esc, new Rgb24(255, 0, 0));
            keyboard.Grid.SetKey(Crush80Key.F1, new Rgb24(0, 255, 0));
            keyboard.Grid.SetKey(Crush80Key.F2, new Rgb24(0, 0, 255));
            keyboard.Grid.SetKey(Crush80Key.CapsLock, new Rgb24(255, 180, 0));
            keyboard.Grid.SetKey(Crush80Key.Enter, new Rgb24(0, 220, 255));
            keyboard.Grid.SetKey(Crush80Key.Space, new Rgb24(180, 0, 255));
            await keyboard.ApplyAsync(cancellation.Token);

            Console.WriteLine($"Static SDK frame applied through {endpoint}.");
            Console.WriteLine(seconds > 0
                ? $"Holding the frame for {seconds} seconds."
                : "Holding the frame until Ctrl+C.");
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception error)
        {
            await Console.Error.WriteLineAsync(error.ToString());
            return 1;
        }
    }

    private static bool TryParseArguments(string[] args, out Uri endpoint, out int seconds)
    {
        endpoint = new Uri("tcp://127.0.0.1:5081");
        seconds = 0;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--help":
                    break;
                case "--endpoint" when index + 1 < args.Length &&
                    Uri.TryCreate(args[++index], UriKind.Absolute, out var parsed):
                    endpoint = parsed;
                    break;
                case "--seconds" when index + 1 < args.Length &&
                    int.TryParse(args[++index], out seconds) && seconds > 0:
                    break;
                default:
                    Console.Error.WriteLine($"Unknown or invalid argument: {args[index]}");
                    PrintUsage();
                    return false;
            }
        }

        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run --project emulator/client/Wobkey.Crush80.Emulator.Client.Sample -- [options]");
        Console.WriteLine("  --endpoint URI    Emulator transport endpoint; default tcp://127.0.0.1:5081.");
        Console.WriteLine("  --seconds COUNT   Restore and disconnect after COUNT seconds; otherwise wait for Ctrl+C.");
    }
}
