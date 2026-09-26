using Wobkey.Crush80.Sdk.Tests.Support;

namespace Wobkey.Crush80.Sdk.Tests.Session;

public sealed class SessionCompatibilityTests
{
    [Theory]
    [InlineData(2, 92, 8)]
    [InlineData(1, 91, 8)]
    [InlineData(1, 92, 7)]
    public async Task RejectsIncompatibleCapabilitiesWithoutMutationAndClosesTransport(
        byte version, byte leds, byte chunk)
    {
        await using var transport = new FakeFirmwareTransport
        {
            ProtocolVersion = version, LedCount = leds, ChunkLimit = chunk
        };

        await Assert.ThrowsAsync<IncompatibleFirmwareException>(async () =>
            await Crush80RgbSession.OpenAsync(transport));

        Assert.Equal(new[] { "GetCapabilities" }, transport.Operations);
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task OpensExactProtocolWithoutCapturingOrChangingLighting()
    {
        await using var transport = new FakeFirmwareTransport { Enabled = true };
        await using var session = await Crush80RgbSession.OpenAsync(transport);

        Assert.Equal(new PerKeyRgbCapabilities(1, 92, 8, true), session.Capabilities);
        Assert.Same(transport.Device, session.Device);
        Assert.Equal(new[] { "GetCapabilities" }, transport.Operations);
        Assert.True(transport.Enabled);
    }

    [Fact]
    public async Task HandshakeTimeoutClosesTransport()
    {
        await using var transport = new FakeFirmwareTransport { TimeoutNextRead = true };

        await Assert.ThrowsAsync<ProtocolViolationException>(async () =>
            await Crush80RgbSession.OpenAsync(transport));

        Assert.True(transport.IsDisposed);
        Assert.Equal(new[] { "GetCapabilities" }, transport.Operations);
    }
}
