using System.Diagnostics;
using Wobkey.Crush80.Adapter;
using Wobkey.Crush80.Emulator;
using Wobkey.Crush80.Sdk.Models;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var openBrowser, out var frameLimit))
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

        await using var emulator = await Crush80Emulator.StartAsync(cancellationToken: cancellation.Token);
        Console.WriteLine($"Visualizer: {emulator.VisualizerUri}");
        Console.WriteLine(frameLimit == 0
            ? "Running until Ctrl+C."
            : $"Rendering {frameLimit} frames.");

        if (openBrowser)
            TryOpenBrowser(emulator.VisualizerUri);

        try
        {
            await using var keyboard = await Crush80Keyboard.OpenAsync(emulator.Transport, cancellation.Token);
            var keys = keyboard.Grid.ToArray();
            var frame = 0;
            while (!cancellation.IsCancellationRequested && (frameLimit == 0 || frame < frameLimit))
            {
                for (var index = 0; index < keys.Length; index++)
                {
                    var hue = (frame * 4 + index * 360 / keys.Length) % 360;
                    keyboard.Grid.SetKey(keys[index], RainbowColor(hue));
                }

                await keyboard.ApplyAsync(cancellation.Token);
                frame++;
                await Task.Delay(50, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            await Console.Error.WriteLineAsync(error.ToString());
            return 1;
        }

        return 0;
    }

    private static bool TryParseArguments(string[] args, out bool openBrowser, out int frameLimit)
    {
        openBrowser = false;
        frameLimit = 0;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--help":
                    break;
                case "--open":
                    openBrowser = true;
                    break;
                case "--frames" when index + 1 < args.Length &&
                    int.TryParse(args[++index], out frameLimit) && frameLimit > 0:
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
        Console.WriteLine("Usage: dotnet run --project emulator/app/Wobkey.Crush80.Emulator.App -- [--open] [--frames COUNT]");
        Console.WriteLine("  --open          Open the loopback visualizer in the default browser.");
        Console.WriteLine("  --frames COUNT  Stop after COUNT animation frames; otherwise run until Ctrl+C.");
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

    private static Rgb24 RainbowColor(int hue)
    {
        var rising = (byte)(hue % 60 * 255 / 60);
        var falling = (byte)(255 - rising);
        return (hue / 60) switch
        {
            0 => new Rgb24(255, rising, 0),
            1 => new Rgb24(falling, 255, 0),
            2 => new Rgb24(0, 255, rising),
            3 => new Rgb24(0, falling, 255),
            4 => new Rgb24(rising, 0, 255),
            _ => new Rgb24(255, 0, falling)
        };
    }
}
