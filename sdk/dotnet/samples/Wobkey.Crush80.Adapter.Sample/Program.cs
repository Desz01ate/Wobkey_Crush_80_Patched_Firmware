using Wobkey.Crush80.Adapter;
using Wobkey.Crush80.Sdk.Models;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var devices = Crush80Keyboard.EnumerateDevices();

        if (devices.Count == 0)
        {
            await Console.Error.WriteLineAsync("Can't find any Crush 80 devices.");
            return 1;
        }

        var selectedDevice = devices[0];

        Crush80Keyboard keyboard;
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
                grid.SetAt(3, 0, green); // F1 in the provisional canvas map.
                await activeKeyboard.ApplyAsync();
                Console.WriteLine("ApplyAsync completed: Esc red and the F1 coordinate green were submitted.");
                Console.WriteLine("Press any key to iterate through mapped keys.");

                Console.ReadKey();

                foreach (var key in grid)
                {
                    grid.SetAll(black);
                    grid.SetKey(key, blue);
                    await activeKeyboard.ApplyAsync();
                    await Task.Delay(50);
                }
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
}