using HidSharp.Reports;

namespace Wobkey.Crush80.Transport;

internal static class HidDeviceMatcher
{
    internal const int VendorId = 0x320F;
    internal const int ProductId = 0x5055;
    internal const ushort UsagePage = 0xFF60;
    internal const ushort Usage = 0x61;

    internal static bool IsWiredVia(int vendorId, int productId, uint topLevelUsage) =>
        IsWiredVia(vendorId, productId, (ushort)(topLevelUsage >> 16), (ushort)topLevelUsage);

    internal static bool IsWiredVia(int vendorId, int productId, ushort usagePage, ushort usage) =>
        vendorId == VendorId && productId == ProductId && usagePage == UsagePage && usage == Usage;

    internal static bool IsWiredVia(int vendorId, int productId, ReportDescriptor descriptor) =>
        TryFindWiredViaUsage(vendorId, productId, descriptor, out _);

    internal static bool TryFindWiredViaUsage(
        int vendorId, int productId, ReportDescriptor descriptor, out uint usage)
    {
        foreach (var item in descriptor.DeviceItems)
        {
            foreach (var candidate in item.Usages.GetAllValues())
            {
                if (!IsWiredVia(vendorId, productId, candidate))
                    continue;

                usage = candidate;
                return true;
            }
        }

        usage = 0;
        return false;
    }
}
