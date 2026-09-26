namespace Wobkey.Crush80.Sdk.Tests.Transport;

public sealed class Crush80DeviceLocatorTests
{
    [Fact]
    public async Task DescriptorOpeningDoesNotSubstituteAnotherDeviceWithMatchingIdentifiers()
    {
        var descriptor = new Crush80DeviceDescriptor(
            "/no/such/crush80/device/path", 0x320F, 0x5055, 0xFF60, 0x61, null, null);

        var error = await Assert.ThrowsAsync<DeviceNotFoundException>(async () =>
            await Crush80RgbSession.OpenAsync(descriptor));

        Assert.Equal(descriptor, error.Device);
    }
}
