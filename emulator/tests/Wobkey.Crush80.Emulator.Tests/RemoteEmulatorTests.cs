using Wobkey.Crush80.Adapter;
using Wobkey.Crush80.Emulator;
using Wobkey.Crush80.Sdk.Models;
using Wobkey.Crush80.Sdk.Session;
using Xunit;

namespace Wobkey.Crush80.Emulator.Tests;

public sealed class RemoteEmulatorTests
{
    [Fact]
    public async Task RemoteAdapterPublishesFrameAndRestoresBeforeDisconnect()
    {
        await using var server = await Crush80EmulatorServer.StartAsync(0, 0);
        var transport = await Crush80RemoteTransport.ConnectAsync(server.TransportEndpoint);
        await using (var keyboard = await Crush80Keyboard.OpenAsync(transport))
        {
            keyboard.Grid.SetAll(default);
            keyboard.Grid.SetKey(Crush80Key.Esc, new Rgb24(255, 0, 0));
            keyboard.Grid.SetKey(Crush80Key.F1, new Rgb24(0, 255, 0));
            keyboard.Grid.SetKey(Crush80Key.CapsLock, new Rgb24(0, 0, 255));
            await keyboard.ApplyAsync();

            var active = server.CaptureState();
            Assert.True(active.ClientConnected);
            Assert.True(active.Enabled);
            Assert.Equal(6, active.Effect);
            Assert.Equal(new Rgb24(255, 0, 0), active.Colors.Span[0]);
            Assert.Equal(new Rgb24(0, 255, 0), active.Colors.Span[1]);
            Assert.Equal(new Rgb24(0, 0, 255), active.Colors.Span[51]);
            Assert.Equal(new Rgb24(0, 0, 255), active.Colors.Span[52]);
        }

        await WaitForClientStateAsync(server, connected: false);
        var restored = server.CaptureState();
        Assert.False(restored.Enabled);
        Assert.Equal(9, restored.Brightness);
        Assert.Equal(7, restored.Effect);
        Assert.All(restored.Colors.ToArray(), color => Assert.Equal(default, color));
    }

    [Fact]
    public async Task ExclusiveClientCanReconnectAfterUnreadReply()
    {
        await using var server = await Crush80EmulatorServer.StartAsync(0, 0);
        var first = await Crush80RemoteTransport.ConnectAsync(server.TransportEndpoint);

        await Assert.ThrowsAsync<IOException>(async () =>
            await Crush80RemoteTransport.ConnectAsync(server.TransportEndpoint));

        var unreadCapabilities = new byte[32];
        unreadCapabilities[0] = 8;
        unreadCapabilities[1] = 0x7F;
        await first.WriteAsync(unreadCapabilities);
        await first.DisposeAsync();
        await WaitForClientStateAsync(server, connected: false);

        var second = await Crush80RemoteTransport.ConnectAsync(server.TransportEndpoint);
        await using (var session = await Crush80RgbSession.OpenAsync(second))
        {
            Assert.Equal(2, session.Capabilities.ProtocolVersion);
            Assert.True(server.CaptureState().ClientConnected);
        }

        await WaitForClientStateAsync(server, connected: false);
    }

    private static async Task WaitForClientStateAsync(Crush80EmulatorServer server, bool connected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (server.CaptureState().ClientConnected == connected)
                return;
            await Task.Delay(10);
        }

        Assert.Equal(connected, server.CaptureState().ClientConnected);
    }
}
