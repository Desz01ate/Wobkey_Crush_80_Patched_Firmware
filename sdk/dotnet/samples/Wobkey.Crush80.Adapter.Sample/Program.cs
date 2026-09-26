using System.Globalization;
using Wobkey.Crush80.Adapter;
using Wobkey.Crush80.Sdk.Models;

var devices = Crush80Keyboard.EnumerateDevices();

if (devices.Count == 0)
{
    Console.Error.WriteLine("Can't find any Crush 80 devices.");
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
    Console.Error.WriteLine($"Could not open the adapter: {error}");
    Console.Error.WriteLine(
        "Opening may have sent the black frame before failing. Any cleanup during failed opening does not verify restored lighting; inspect the keyboard state.");
    return 1;
}

Exception? applyFailure = null;
Exception? cleanupFailure = null;
try
{
    await using var activeKeyboard = keyboard;
    try
    {
        activeKeyboard.Grid.SetKey(Crush80Key.Esc, new Rgb24(255, 0, 0));
        activeKeyboard.Grid.SetAt(3, 0, new Rgb24(0, 255, 0)); // F1 in the provisional canvas map.
        await activeKeyboard.ApplyAsync();
        Console.WriteLine("ApplyAsync completed: Esc red and the F1 coordinate green were submitted.");
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
    Console.Error.WriteLine($"Grid edit or ApplyAsync failed: {applyFailure}");
    if (cleanupFailure is null)
        Console.Error.WriteLine(
            "await using disposal completed and asked the SDK to restore captured state; lighting was not independently verified. Inspect the keyboard.");
}

if (cleanupFailure is not null)
{
    Console.Error.WriteLine($"await using disposal failed: {cleanupFailure}");
    Console.Error.WriteLine(
        "Restoration is not verified. Inspect the keyboard; disconnects, transport errors, or concurrent writers can prevent recovery.");
}

if (applyFailure is not null || cleanupFailure is not null)
    return 1;

Console.WriteLine(
    "await using disposal completed and asked the SDK to restore captured state. Lighting was not read back or independently verified.");
return 0;