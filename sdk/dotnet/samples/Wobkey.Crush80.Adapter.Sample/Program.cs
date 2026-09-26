using System.Globalization;
using Wobkey.Crush80.Adapter;
using Wobkey.Crush80.Sdk.Models;

if (args is [] or ["--help"])
{
    Console.WriteLine("Usage: Wobkey.Crush80.Adapter.Sample --help | --list | --smoke [device-index]");
    Console.WriteLine("--list discovers wired devices only; it does not open a keyboard or change lighting.");
    Console.WriteLine("--smoke [device-index] requires typing SMOKE before opening; opening immediately sends black.");
    return 0;
}

if (args is ["--list"])
{
    try
    {
        var devices = Crush80Keyboard.EnumerateDevices();
        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            Console.WriteLine($"[{index}] {device.ProductName ?? "Crush 80"}: {device.Path}");
        }

        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Could not enumerate wired Crush 80 devices: {error}");
        return 1;
    }
}

if (args is not ["--smoke"] && args is not ["--smoke", _])
{
    Console.Error.WriteLine("Unknown command or invalid arguments. Use --help.");
    return 2;
}

int? selectedIndex = null;
if (args.Length == 2)
{
    if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedIndex) || parsedIndex < 0)
    {
        Console.Error.WriteLine("Device index must be a non-negative integer. Use --list to enumerate devices.");
        return 2;
    }

    selectedIndex = parsedIndex;
}

Crush80DeviceDescriptor? selectedDevice = null;
if (selectedIndex is int indexToSelect)
{
    try
    {
        var devices = Crush80Keyboard.EnumerateDevices();
        if (indexToSelect >= devices.Count)
        {
            Console.Error.WriteLine($"Device index {indexToSelect} is not in the current enumeration. Use --list.");
            return 2;
        }

        selectedDevice = devices[indexToSelect];
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Could not enumerate wired Crush 80 devices: {error}");
        return 1;
    }
}

Console.WriteLine("WARNING: --smoke writes to keyboard lighting. OpenAsync immediately acquires control and sends a black frame.");
Console.WriteLine("Close SignalRGB, VIA, and other keyboard controllers first. Adapter disposal asks the SDK to restore the captured state, but recovery is not guaranteed after disconnects, transport failures, or concurrent writes.");
Console.WriteLine(selectedDevice is null
    ? "Target: the first compatible wired device selected by the SDK."
    : $"Target: [{selectedIndex}] {selectedDevice.ProductName ?? "Crush 80"}: {selectedDevice.Path}");
Console.Write("Type exactly SMOKE to authorize opening and writing: ");
if (!string.Equals(Console.ReadLine(), "SMOKE", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Not authorized. OpenAsync was not called and no device was opened by this command.");
    return 2;
}

Crush80Keyboard keyboard;
try
{
    keyboard = selectedDevice is null
        ? await Crush80Keyboard.OpenAsync()
        : await Crush80Keyboard.OpenAsync(selectedDevice);
}
catch (Exception error)
{
    Console.Error.WriteLine($"Could not open the adapter: {error}");
    Console.Error.WriteLine("Opening may have sent the black frame before failing. Any cleanup during failed opening does not verify restored lighting; inspect the keyboard state.");
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
        Console.Error.WriteLine("await using disposal completed and asked the SDK to restore captured state; lighting was not independently verified. Inspect the keyboard.");
}

if (cleanupFailure is not null)
{
    Console.Error.WriteLine($"await using disposal failed: {cleanupFailure}");
    Console.Error.WriteLine("Restoration is not verified. Inspect the keyboard; disconnects, transport errors, or concurrent writers can prevent recovery.");
}

if (applyFailure is not null || cleanupFailure is not null)
    return 1;

Console.WriteLine("await using disposal completed and asked the SDK to restore captured state. Lighting was not read back or independently verified.");
return 0;
