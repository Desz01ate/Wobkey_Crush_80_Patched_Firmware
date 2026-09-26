using Wobkey.Crush80.Adapter;
using Wobkey.Crush80.Emulator;
using Wobkey.Crush80.Sdk.Models;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var devices = Crush80Keyboard.EnumerateDevices();
        Crush80Keyboard keyboard;

        if (devices.Count == 0)
        {
            var transport = await Crush80RemoteTransport.ConnectAsync(
                new Uri("tcp://127.0.0.1:5081"));

            keyboard = await Crush80Keyboard.OpenAsync(transport);
        }
        else
        {
            var selectedDevice = devices[0];
            try
            {
                keyboard = await Crush80Keyboard.OpenAsync(selectedDevice);
            }
            catch (Exception error)
            {
                await Console.Error.WriteLineAsync($"Could not open the adapter: {error}");
                await Console.Error.WriteLineAsync(
                    "Opening may have sent the black frame before failing. Any cleanup during failed opening does not verify restored lighting; inspect the keyboard state.");
                return 1;
            }
        }

        Exception? applyFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            var red = new Rgb24(255, 0, 0);
            var green = new Rgb24(0, 255, 0);
            var blue = new Rgb24(0, 0, 255);
            var black = new Rgb24(0, 0, 0);

            await using var activeKeyboard = keyboard;
            try
            {
                var grid = activeKeyboard.Grid;
                grid.SetAll(black);

                await activeKeyboard.ApplyAsync();

                grid.SetKey(Crush80Key.Esc, red);
                grid.SetAt(3, 0, green); // F1 canvas coordinate.
                await activeKeyboard.ApplyAsync();
                Console.WriteLine("ApplyAsync completed: Esc red and the F1 coordinate green were submitted.");
                Console.WriteLine(
                    "Press any key to start the circular rainbow animation. Press any key again to stop.");
                Console.ReadKey(intercept: true);

                var keys = new LinkedList<Crush80Key>(grid);
                var firstKey = keys.First!;
                var hueOffset = 0;
                while (!Console.KeyAvailable)
                {
                    grid.SetAll(black);
                    var current = firstKey;
                    var keyPosition = 0;
                    do
                    {
                        var hue = (hueOffset + keyPosition * 360 / keys.Count) % 360;
                        grid.SetKey(current.Value, RainbowColor(hue));
                        current = current.Next ?? firstKey;
                        keyPosition++;
                    } while (current != firstKey);
                
                    await activeKeyboard.ApplyAsync();
                    hueOffset = (hueOffset + 4) % 360;
                    await Task.Delay(50);
                }

                Console.ReadKey(intercept: true);
            }
            catch (Exception error)
            {
                applyFailure = error;
            }
        }
        catch (Exception error)
        {
            cleanupFailure = error;
        }

        if (applyFailure is not null)
        {
            await Console.Error.WriteLineAsync($"Grid edit or ApplyAsync failed: {applyFailure}");
            if (cleanupFailure is null)
                await Console.Error.WriteLineAsync(
                    "await using disposal completed and asked the SDK to restore captured state; lighting was not independently verified. Inspect the keyboard.");
        }

        if (cleanupFailure is not null)
        {
            await Console.Error.WriteLineAsync($"await using disposal failed: {cleanupFailure}");
            await Console.Error.WriteLineAsync(
                "Restoration is not verified. Inspect the keyboard; disconnects, transport errors, or concurrent writers can prevent recovery.");
        }

        if (applyFailure is not null || cleanupFailure is not null)
            return 1;

        Console.WriteLine(
            "await using disposal completed and asked the SDK to restore captured state. Lighting was not read back or independently verified.");
        return 0;
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