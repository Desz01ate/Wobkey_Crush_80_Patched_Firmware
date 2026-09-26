namespace Wobkey.Crush80.Sdk.Models;

/// <summary>Identifies a discovered Crush 80 HID device interface.</summary>
/// <param name="Path">The HID device path.</param>
/// <param name="VendorId">The USB vendor identifier.</param>
/// <param name="ProductId">The USB product identifier.</param>
/// <param name="UsagePage">The HID usage page.</param>
/// <param name="Usage">The HID usage.</param>
/// <param name="SerialNumber">The device serial number, if available.</param>
/// <param name="ProductName">The device product name, if available.</param>
public sealed record Crush80DeviceDescriptor(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    string? SerialNumber,
    string? ProductName);
