using HidSharp;
using HidSharp.Reports;
using Wobkey.Crush80.Sdk.Exceptions;
using Wobkey.Crush80.Sdk.Models;

namespace Wobkey.Crush80.Sdk.Transport;

/// <summary>Discovers wired Crush 80 VIA HID interfaces on the current machine.</summary>
public static class Crush80DeviceLocator
{
    /// <summary>Returns interfaces whose VID, PID, top-level usage page and usage match wired VIA.</summary>
    public static IReadOnlyList<Crush80DeviceDescriptor> Enumerate()
    {
        var matches = new List<Crush80DeviceDescriptor>();
        foreach (var device in DeviceList.Local.GetHidDevices(HidDeviceMatcher.VendorId, HidDeviceMatcher.ProductId))
        {
            var usage = ReadWiredUsage(
                device.VendorID, device.ProductID, device.DevicePath, device,
                static hid => hid.GetReportDescriptor());
            if (usage is null)
                continue;

            matches.Add(new Crush80DeviceDescriptor(
                device.DevicePath, (ushort)device.VendorID, (ushort)device.ProductID,
                (ushort)(usage.Value >> 16), (ushort)usage.Value,
                ReadOptionalString(device, static hid => hid.GetSerialNumber()),
                ReadOptionalString(device, static hid => hid.GetProductName())));
        }

        return matches;
    }

    internal static HidDevice? FindByPath(string path)
    {
        foreach (var device in DeviceList.Local.GetHidDevices(HidDeviceMatcher.VendorId, HidDeviceMatcher.ProductId))
        {
            if (string.Equals(device.DevicePath, path, StringComparison.Ordinal) &&
                ReadWiredUsage(device.VendorID, device.ProductID, path, device,
                    static hid => hid.GetReportDescriptor()) is not null)
                return device;
        }

        return null;
    }

    internal static uint? ReadWiredUsage<T>(
        int vendorId, int productId, string path, T source, Func<T, ReportDescriptor> readDescriptor)
    {
        try
        {
            // HidSharp 2.6.4 exposes top-level usages via the parsed report descriptor.
            return HidDeviceMatcher.TryFindWiredViaUsage(
                vendorId, productId, readDescriptor(source), out var usage) ? usage : null;
        }
        catch (Exception ex) when (HidSharpTransport.IsAccessDenied(ex))
        {
            // The path and USB IDs are known; usage is deliberately left unknown.
            var device = new Crush80DeviceDescriptor(path, (ushort)vendorId, (ushort)productId, 0, 0, null, null);
            throw new DeviceAccessDeniedException("Discover", device, ex);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // An unreadable usage cannot be treated as a match merely because VID/PID match.
            return null;
        }
    }

    private static string? ReadOptionalString<T>(T source, Func<T, string> read)
    {
        try
        {
            return read(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
