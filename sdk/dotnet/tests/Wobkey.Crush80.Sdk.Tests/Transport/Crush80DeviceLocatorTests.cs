using Wobkey.Crush80.Transport;

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

    [Fact]
    public void InaccessibleDescriptorDoesNotBecomeNoMatch()
    {
        var original = new UnauthorizedAccessException("hidraw denied");
        var error = Assert.Throws<DeviceAccessDeniedException>(() =>
            Crush80DeviceLocator.ReadWiredUsage(
                0x320F, 0x5055, "denied-path", original, static error => throw error));

        Assert.Equal("denied-path", error.Device?.Path);
        Assert.Same(original, error.InnerException);
    }

    [Fact]
    public void InaccessibleDescriptorIOExceptionDoesNotBecomeNoMatch()
    {
        var original = new IOException("descriptor access denied", unchecked((int)0x80070005));
        var error = Assert.Throws<DeviceAccessDeniedException>(() =>
            Crush80DeviceLocator.ReadWiredUsage(
                0x320F, 0x5055, "denied-path", original, static error => throw error));

        Assert.Equal("denied-path", error.Device?.Path);
        Assert.Same(original, error.InnerException);
    }

}
