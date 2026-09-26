using HidSharp;

namespace Wobkey.Crush80.Transport;

/// <summary>Discovers wired Crush 80 VIA HID interfaces on the current machine.</summary>
public static class Crush80DeviceLocator
{
    /// <summary>Returns interfaces whose VID, PID, top-level usage page and usage match wired VIA.</summary>
    public static IReadOnlyList<Crush80DeviceDescriptor> Enumerate()
    {
        var matches = new List<Crush80DeviceDescriptor>();
        foreach (var device in DeviceList.Local.GetHidDevices(HidDeviceMatcher.VendorId, HidDeviceMatcher.ProductId))
        {
            if (!TryGetWiredUsage(device, out var usage))
                continue;

            matches.Add(new Crush80DeviceDescriptor(
                device.DevicePath, (ushort)device.VendorID, (ushort)device.ProductID,
                (ushort)(usage >> 16), (ushort)usage,
                ReadOptionalString(device.GetSerialNumber), ReadOptionalString(device.GetProductName)));
        }

        return matches;
    }

    internal static HidDevice? FindByPath(string path)
    {
        foreach (var device in DeviceList.Local.GetHidDevices(HidDeviceMatcher.VendorId, HidDeviceMatcher.ProductId))
        {
            if (string.Equals(device.DevicePath, path, StringComparison.Ordinal) &&
                TryGetWiredUsage(device, out _))
                return device;
        }

        return null;
    }

    private static bool TryGetWiredUsage(HidDevice device, out uint usage)
    {
        try
        {
            // HidSharp 2.6.4 exposes top-level usages via the parsed report descriptor.
            return HidDeviceMatcher.TryFindWiredViaUsage(
                device.VendorID, device.ProductID, device.GetReportDescriptor(), out usage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // An unreadable usage cannot be treated as a match merely because VID/PID match.
        }
        usage = 0;

        return false;
    }

    private static string? ReadOptionalString(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
